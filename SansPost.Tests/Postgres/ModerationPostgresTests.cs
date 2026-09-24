using Microsoft.EntityFrameworkCore;
using Npgsql;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using SansPost.Features.Search;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Moderacja na prawdziwym PostgreSQL: częściowy UNIQUE zgłoszeń, atomowość akcji, rollback, filtrowanie
    // wszystkich publicznych read modeli (w tym FTS) i append-only audyt.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Moderation")]
    public class ModerationPostgresTests
    {
        private readonly PostgresFixture _pg;

        public ModerationPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private static string NewToken() => "mod" + Guid.NewGuid().ToString("N")[..10];

        private async Task<T> WithAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using var context = _pg.CreateContext();
            return await action(context);
        }

        private Task<ServiceResult<ReportSubmission>> ReportAsync(int reporterId, ReportTargetType type, int targetId) =>
            WithAsync(context => new ReportService(context, TimeProvider.System, TestServices.Guard(context))
                .CreateAsync(reporterId, new CreateReportRequest { TargetType = type, TargetId = targetId, Reason = ReportReason.Spam }));

        private Task<ServiceResult> ModerateAsync(Func<ModerationService, Task<ServiceResult>> action) =>
            WithAsync(context => action(new ModerationService(context, TimeProvider.System)));

        private async Task<int> CreateAdminAsync()
        {
            var id = await _pg.CreateUserAsync("admin");
            await using var context = _pg.CreateContext();
            await context.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin));
            return id;
        }

        private async Task<PostDetailsResponse> CreatePostAsync(int userId, string title = "Post moderowany", string content = "Treść")
        {
            var result = await WithAsync(context => PostgresFixture.CreatePostService(context).CreateAsync(userId,
                new PostRequest { Title = title, Content = content, Category = PostCategory.Ideas }));
            Assert.True(result.Succeeded, result.Message);
            return result.Value!;
        }

        private Task<CommentResponse> CreateCommentAsync(int userId, int postId, string content = "Komentarz") =>
            WithAsync(async context => (await SansPost.Tests.TestInfrastructure.TestServices.Comments(context)
                .AddAsync(userId, postId, new CommentRequest { Content = content })).Value!);

        // J — 20 równoległych identycznych zgłoszeń: najwyżej jedno Pending, zero błędów.
        [DockerFact]
        public async Task TwentyParallelDuplicateReports_LeaveExactlyOnePending()
        {
            var authorId = await _pg.CreateUserAsync("ra");
            var reporterId = await _pg.CreateUserAsync("rr");
            var post = await CreatePostAsync(authorId);

            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => ReportAsync(reporterId, ReportTargetType.Post, post.Id))));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
            Assert.Equal(1, results.Count(r => r.Value!.Created));
            Assert.Single(results.Select(r => r.Value!.Report.Id).Distinct());
            Assert.Equal(1, await WithAsync(c => c.Reports.CountAsync(r => r.ReporterUserId == reporterId && r.Status == ReportStatus.Pending)));
        }

        [DockerFact]
        public async Task PartialUniqueIndex_BlocksSecondPending_ButAllowsNewReportAfterReview()
        {
            var authorId = await _pg.CreateUserAsync("pa");
            var reporterId = await _pg.CreateUserAsync("pr");
            var adminId = await CreateAdminAsync();
            var post = await CreatePostAsync(authorId);
            var first = (await ReportAsync(reporterId, ReportTargetType.Post, post.Id)).Value!.Report;

            // Baza odrzuca drugi Pending także z pominięciem serwisu.
            await using (var context = _pg.CreateContext())
            {
                context.Reports.Add(new Report { ReporterUserId = reporterId, TargetType = ReportTargetType.Post, TargetId = post.Id, Reason = ReportReason.Other, CreatedAt = DateTime.UtcNow });
                var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
                Assert.Contains("UX_reports_pending_per_reporter", exception.InnerException!.Message);
            }

            Assert.True((await ModerateAsync(m => m.DismissReportAsync(adminId, first.Id, null))).Succeeded);
            var again = await ReportAsync(reporterId, ReportTargetType.Post, post.Id);

            Assert.True(again.Value!.Created);
            Assert.NotEqual(first.Id, again.Value.Report.Id);
        }

        // U — zmiana stanu + rozstrzygnięcie zgłoszeń + audyt są zapisane razem.
        [DockerFact]
        public async Task HidePost_ChangesState_ResolvesAllPendingReports_AndWritesOneAudit()
        {
            var authorId = await _pg.CreateUserAsync("ua");
            var adminId = await CreateAdminAsync();
            var post = await CreatePostAsync(authorId);
            foreach (var reporter in await _pg.CreateUsersFastAsync(3))
                await ReportAsync(reporter, ReportTargetType.Post, post.Id);

            var result = await ModerateAsync(m => m.HidePostAsync(adminId, post.Id, "Spam"));

            Assert.True(result.Succeeded);
            await using var context = _pg.CreateContext();
            var stored = await context.Posts.SingleAsync(p => p.Id == post.Id);
            Assert.Equal((ContentStatus.Hidden, post.Version + 1), (stored.Status, stored.Version));
            Assert.All(await context.Reports.Where(r => r.TargetId == post.Id && r.TargetType == ReportTargetType.Post).ToListAsync(), r =>
            {
                Assert.Equal(ReportStatus.Resolved, r.Status);
                Assert.Equal(adminId, r.ReviewedByUserId);
            });
            Assert.Equal(1, await context.ModerationActions.CountAsync(a => a.ActionType == ModerationActionType.HidePost && a.TargetId == post.Id));
        }

        // V — realna awaria w trakcie transakcji moderacji (anulowanie, gdy UPDATE posta czeka na blokadę wiersza):
        // rozstrzygnięcie zgłoszeń wykonane wcześniej w tej transakcji zostaje wycofane, audyt nie powstaje.
        [DockerFact]
        public async Task CancelledModerationTransaction_RollsBackReportResolution_AndLeavesNoAudit()
        {
            var authorId = await _pg.CreateUserAsync("va");
            var reporterId = await _pg.CreateUserAsync("vr");
            var adminId = await CreateAdminAsync();
            var post = await CreatePostAsync(authorId);
            var report = (await ReportAsync(reporterId, ReportTargetType.Post, post.Id)).Value!.Report;

            await using (var holder = _pg.CreateContext())
            {
                await using var holderTransaction = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM posts WHERE id = {post.Id} FOR UPDATE");

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                await using var context = _pg.CreateContext();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new ModerationService(context, TimeProvider.System).HidePostAsync(adminId, post.Id, "Spam", cts.Token));

                await holderTransaction.RollbackAsync();
            }

            await using (var check = _pg.CreateContext())
            {
                Assert.Equal(ContentStatus.Published, (await check.Posts.SingleAsync(p => p.Id == post.Id)).Status);
                Assert.Equal(ReportStatus.Pending, (await check.Reports.SingleAsync(r => r.Id == report.Id)).Status);
                Assert.Equal(0, await check.ModerationActions.CountAsync(a => a.TargetId == post.Id && a.TargetType == ModerationTargetType.Post));
            }

            // Brak zawieszonej blokady ani częściowego stanu — ponowna akcja przechodzi w całości.
            Assert.True((await ModerateAsync(m => m.HidePostAsync(adminId, post.Id, "Spam"))).Succeeded);
        }

        // P + pełny test braku wycieków: ukryte i usunięte treści nie trafiają do ŻADNEGO publicznego read modelu.
        [DockerFact]
        public async Task HiddenAndDeletedContent_NeverLeaksIntoPublicReadModels()
        {
            var token = NewToken();
            var authorName = TestUsers.UniqueName("lk");
            var authorId = (await WithAsync(c => TestServices.Auth(c).RegisterAsync(new RegisterRequest
            {
                Username = authorName,
                Email = $"{authorName}@example.com",
                Password = TestUsers.Password
            }))).Value!.Id;
            var commenterId = await _pg.CreateUserAsync("lc");
            var adminId = await CreateAdminAsync();

            var visible = await CreatePostAsync(authorId, $"Widoczny {token}", $"treść {token}");
            var hidden = await CreatePostAsync(authorId, $"Ukryty {token}", $"treść {token}");
            var deleted = await CreatePostAsync(authorId, $"Usunięty {token}", $"treść {token}");

            var visibleComment = await CreateCommentAsync(authorId, visible.Id, "zostaje");
            var hiddenComment = await CreateCommentAsync(authorId, visible.Id, "ukryty komentarz");
            await CreateCommentAsync(commenterId, hidden.Id, "pod ukrytym postem");

            Assert.True((await ModerateAsync(m => m.HidePostAsync(adminId, hidden.Id, null))).Succeeded);
            Assert.True((await ModerateAsync(m => m.HideCommentAsync(adminId, hiddenComment.Id, null))).Succeeded);
            Assert.True((await WithAsync(c => PostgresFixture.CreatePostService(c).DeleteAsync(authorId, deleted.Id, deleted.Version))).Succeeded);

            await using var context = _pg.CreateContext();
            var posts = PostgresFixture.CreatePostService(context);
            var comments = SansPost.Tests.TestInfrastructure.TestServices.Comments(context);
            var search = new PostgresSearchService(context);
            var visibleOnly = new[] { visible.Id };

            // Feedy: Newest, Popular, autor, kategoria
            Assert.Equal(visibleOnly, (await posts.GetFeedAsync(new PostFeedQuery { AuthorId = authorId }, null)).Value!.Items.Select(p => p.Id));
            Assert.Equal(visibleOnly, (await posts.GetFeedAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular }, null)).Value!.Items.Select(p => p.Id));
            Assert.Equal(visibleOnly, (await posts.GetFeedAsync(new PostFeedQuery { AuthorId = authorId, Category = PostCategory.Ideas }, null)).Value!.Items.Select(p => p.Id));

            // Szczegóły: ukryty/usunięty = 404; CommentCount liczy tylko opublikowane komentarze
            Assert.Null(await posts.GetByIdAsync(hidden.Id, null));
            Assert.Null(await posts.GetByIdAsync(deleted.Id, null));
            Assert.Equal(1, (await posts.GetByIdAsync(visible.Id, null))!.CommentCount);

            // FTS: searchvector nadal zawiera tekst, ale filtr statusu jest obowiązkowy
            Assert.Equal(visibleOnly, (await search.SearchPostsAsync(new PostSearchQuery { Q = token }, null)).Value!.Items.Select(p => p.Id));
            Assert.Equal(1, (await search.SearchUsersAsync(authorName, 5)).Value!.Single().PostCount);

            // Komentarze: lista, pojedynczy, pod ukrytym postem
            Assert.Equal(new[] { visibleComment.Id }, (await comments.GetByPostAsync(visible.Id, new CommentPageQuery())).Value!.Items.Select(c => c.Id));
            Assert.Null(await comments.GetByIdAsync(hiddenComment.Id));
            Assert.Equal(ServiceError.NotFound, (await comments.GetByPostAsync(hidden.Id, new CommentPageQuery())).Error);
            Assert.Empty(await comments.GetRecentByAuthorAsync(commenterId, 10));

            // Profil: liczniki i aktywność
            var profile = (await new ProfileService(context, posts, comments).GetByUsernameAsync(authorName, null))!;
            Assert.Equal((1, 1), (profile.PostCount, profile.CommentCount));
            Assert.Equal(visibleOnly, profile.RecentPosts.Select(p => p.Id));
            Assert.Equal(new[] { visibleComment.Id }, profile.RecentComments.Select(c => c.Id));

            // Kategorie: liczba opublikowanych postów autora w kategorii Ideas (izolacja: świeża kategoria w tej bazie
            // jest współdzielona z innymi testami, więc porównujemy przyrost widocznych, a nie ukrytych).
            var ideas = (await posts.GetCategoriesAsync()).Single(c => c.Category == PostCategory.Ideas).PostCount;
            var publishedIdeas = await context.Posts.CountAsync(p => p.Category == PostCategory.Ideas && p.Status == ContentStatus.Published);
            Assert.Equal(publishedIdeas, ideas);
        }

        [DockerFact]
        public async Task ModerationAudit_IsAppendOnly_InTheDatabase()
        {
            var authorId = await _pg.CreateUserAsync("au");
            var adminId = await CreateAdminAsync();
            var post = await CreatePostAsync(authorId);
            await ModerateAsync(m => m.HidePostAsync(adminId, post.Id, "powód"));

            await using var connection = new NpgsqlConnection(_pg.ConnectionString);
            await connection.OpenAsync();
            foreach (var sql in new[] { "UPDATE moderationactions SET reason = 'zmienione'", "DELETE FROM moderationactions" })
            {
                await using var command = new NpgsqlCommand(sql, connection);
                var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains("append-only", exception.MessageText);
            }
        }

        [DockerFact]
        public async Task HideVsConcurrentAuthorEdit_NeverSilentlyOverwrites()
        {
            var authorId = await _pg.CreateUserAsync("he");
            var adminId = await CreateAdminAsync();
            var post = await CreatePostAsync(authorId);

            var edit = Task.Run(() => WithAsync(c => PostgresFixture.CreatePostService(c).UpdateAsync(authorId, post.Id, post.Version,
                new PostRequest { Title = "Edycja autora", Content = "Treść", Category = PostCategory.Ideas })));
            var hide = Task.Run(() => ModerateAsync(m => m.HidePostAsync(adminId, post.Id, null)));
            await Task.WhenAll(edit, hide);

            var stored = await WithAsync(c => c.Posts.SingleAsync(p => p.Id == post.Id));
            if (hide.Result.Succeeded)
                Assert.Equal(ContentStatus.Hidden, stored.Status);
            else
                Assert.Equal("content-changed", hide.Result.Code);   // edycja wygrała — moderator ponawia na aktualnej wersji
            Assert.True(edit.Result.Succeeded || edit.Result.Error is ServiceError.NotFound or ServiceError.PreconditionFailed);
        }
    }
}
