using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Notifications;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Notifications
{
    // Notifications Unit/API: reguły tworzenia, własność, licznik, odczyt, paginacja i bezpieczeństwo DTO.
    [Trait("Category", "Notifications")]
    public class NotificationServiceTests : IDisposable
    {
        private readonly TestDatabase _db = new();

        private async Task<(AuthenticatedUser A, AuthenticatedUser B, int PostId)> ArrangeAsync()
        {
            var a = await _db.RegisterAsync(TestUsers.UniqueName("a"));
            var b = await _db.RegisterAsync(TestUsers.UniqueName("b"));
            using var context = _db.CreateContext();
            var post = new Post { UserId = a.Id, Title = "Post autora A", Content = "treść", Category = PostCategory.General, CreatedAt = DateTime.UtcNow };
            context.Posts.Add(post);
            await context.SaveChangesAsync();
            return (a, b, post.Id);
        }

        private async Task<ServiceResult<CommentResponse>> CommentAsync(int actorId, int postId, string content = "Komentarz")
        {
            using var context = _db.CreateContext();
            return await TestServices.Comments(context, _db.Time).AddAsync(actorId, postId, new CommentRequest { Content = content });
        }

        private T WithService<T>(Func<NotificationService, T> action)
        {
            using var context = _db.CreateContext();
            return action(new NotificationService(context, _db.Time));
        }

        private async Task<T> WithServiceAsync<T>(Func<NotificationService, Task<T>> action)
        {
            using var context = _db.CreateContext();
            return await action(new NotificationService(context, _db.Time));
        }

        private Task<List<NotificationResponse>> ListAsync(int userId) =>
            WithServiceAsync(async s => (await s.GetAsync(userId, new NotificationPageQuery { Limit = 50 })).Value!.Items.ToList());

        // 1
        [Fact]
        public async Task CommentByAnotherUser_CreatesNotificationForPostAuthor()
        {
            var (a, b, postId) = await ArrangeAsync();

            var comment = await CommentAsync(b.Id, postId);

            var notification = Assert.Single(await ListAsync(a.Id));
            Assert.Equal((NotificationType.CommentOnPost, b.Username, postId, comment.Value!.Id, "Post autora A", true, false),
                (notification.Type, notification.ActorUsername, notification.PostId, notification.CommentId, notification.PostTitle, notification.TargetAvailable, notification.IsRead));
        }

        // 2
        [Fact]
        public async Task CommentOnOwnPost_CreatesNoNotification()
        {
            var (a, _, postId) = await ArrangeAsync();

            Assert.True((await CommentAsync(a.Id, postId)).Succeeded);

            Assert.Empty(await ListAsync(a.Id));
        }

        // 3 — nieudany komentarz (walidacja, ukryty post, zawieszone konto) nie tworzy powiadomienia.
        [Fact]
        public async Task FailedComment_CreatesNoNotification()
        {
            var (a, b, postId) = await ArrangeAsync();

            Assert.False((await CommentAsync(b.Id, postId, content: "   ")).Succeeded);

            using (var context = _db.CreateContext())
            {
                await context.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Suspended));
            }
            Assert.False((await CommentAsync(b.Id, postId)).Succeeded);

            using (var context = _db.CreateContext())
            {
                await context.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Active));
                await context.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ContentStatus.Hidden));
            }
            Assert.Equal(ServiceError.NotFound, (await CommentAsync(b.Id, postId)).Error);

            using var check = _db.CreateContext();
            Assert.Equal(0, await check.Notifications.CountAsync());
            Assert.Equal(0, await check.Comments.CountAsync());
        }

        // 4, 5 — powiadomienie widzi i oznacza wyłącznie odbiorca.
        [Fact]
        public async Task Notification_BelongsOnlyToRecipient()
        {
            var (a, b, postId) = await ArrangeAsync();
            await CommentAsync(b.Id, postId);
            var id = (await ListAsync(a.Id)).Single().Id;

            Assert.Empty(await ListAsync(b.Id));
            Assert.Equal(0, await WithServiceAsync(s => s.GetUnreadCountAsync(b.Id)));

            var foreign = await WithServiceAsync(s => s.MarkReadAsync(b.Id, id));
            Assert.Equal(ServiceError.NotFound, foreign.Error);
            Assert.Equal(0, await WithServiceAsync(s => s.MarkAllReadAsync(b.Id)));
            Assert.False((await ListAsync(a.Id)).Single().IsRead);
        }

        // 6, 7, 8
        [Fact]
        public async Task UnreadCount_AndMarkRead_AreIdempotent()
        {
            var (a, b, postId) = await ArrangeAsync();
            await CommentAsync(b.Id, postId, "pierwszy");
            await CommentAsync(b.Id, postId, "drugi");
            Assert.Equal(2, await WithServiceAsync(s => s.GetUnreadCountAsync(a.Id)));

            var id = (await ListAsync(a.Id)).First().Id;
            var first = await WithServiceAsync(s => s.MarkReadAsync(a.Id, id));
            _db.Time.Advance(TimeSpan.FromMinutes(5));
            var again = await WithServiceAsync(s => s.MarkReadAsync(a.Id, id));

            Assert.True(first.Succeeded && again.Succeeded);
            Assert.NotNull(first.Value!.ReadAt);
            Assert.Equal(first.Value.ReadAt, again.Value!.ReadAt);   // pierwszy odczyt zostaje
            Assert.Equal(1, await WithServiceAsync(s => s.GetUnreadCountAsync(a.Id)));
        }

        // 9
        [Fact]
        public async Task ReadAll_MarksEverything_AndIsIdempotent()
        {
            var (a, b, postId) = await ArrangeAsync();
            for (var i = 0; i < 3; i++)
                await CommentAsync(b.Id, postId, $"komentarz {i}");

            Assert.Equal(3, await WithServiceAsync(s => s.MarkAllReadAsync(a.Id)));
            Assert.Equal(0, await WithServiceAsync(s => s.MarkAllReadAsync(a.Id)));
            Assert.Equal(0, await WithServiceAsync(s => s.GetUnreadCountAsync(a.Id)));
            Assert.All(await ListAsync(a.Id), n => Assert.True(n.IsRead));
        }

        // 10 — stabilna paginacja keyset: te same znaczniki czasu, kolejność po Id, bez duplikatów i dziur.
        [Fact]
        public async Task Pagination_IsStable_WithIdenticalTimestamps()
        {
            var (a, b, postId) = await ArrangeAsync();
            for (var i = 0; i < 23; i++)
                await CommentAsync(b.Id, postId, $"komentarz {i}");   // zegar testowy stoi → identyczne CreatedAt

            var seen = new List<NotificationResponse>();
            string? cursor = null;
            do
            {
                var page = await WithServiceAsync(s => s.GetAsync(a.Id, new NotificationPageQuery { Cursor = cursor, Limit = 10 }));
                seen.AddRange(page.Value!.Items);
                cursor = page.Value.NextCursor;
            } while (cursor is not null);

            Assert.Equal(23, seen.Count);
            Assert.Equal(23, seen.Select(n => n.Id).Distinct().Count());
            Assert.Equal(seen.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Select(n => n.Id), seen.Select(n => n.Id));
            Assert.Equal(ServiceError.Validation, (await WithServiceAsync(s => s.GetAsync(a.Id, new NotificationPageQuery { Cursor = "garbage" }))).Error);
        }

        // Ukryty/usunięty cel: historia zostaje, ale bez tytułu i z flagą niedostępności (UI: "treść niedostępna").
        [Fact]
        public async Task HiddenOrDeletedTarget_DoesNotLeakContent()
        {
            var (a, b, postId) = await ArrangeAsync();
            await CommentAsync(b.Id, postId);
            var commentId = (await ListAsync(a.Id)).Single().CommentId!.Value;

            using (var context = _db.CreateContext())
                await context.Comments.Where(c => c.Id == commentId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ContentStatus.Hidden));
            var commentHidden = (await ListAsync(a.Id)).Single();
            Assert.False(commentHidden.TargetAvailable);

            using (var context = _db.CreateContext())
                await context.Posts.Where(p => p.Id == postId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ContentStatus.Deleted));
            var postDeleted = (await ListAsync(a.Id)).Single();
            Assert.False(postDeleted.TargetAvailable);
            Assert.Null(postDeleted.PostTitle);
        }

        // 11 — DTO bez danych uwierzytelnienia i bez treści komentarza.
        [Fact]
        public void PublicDto_ContainsNoAuthOrContentData()
        {
            var properties = typeof(NotificationResponse).GetProperties().Select(p => p.Name).ToList();

            foreach (var forbidden in new[] { "Email", "PasswordHash", "AuthVersion", "Role", "Status", "Content", "UserId", "ActorUserId", "Reason" })
                Assert.DoesNotContain(properties, p => p.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose() => _db.Dispose();
    }
}
