using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Posts;
using SansPost.Features.Reactions;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Tests.Postgres
{
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Concurrency")]
    public class SocialConcurrencyTests
    {
        private readonly PostgresFixture _pg;

        public SocialConcurrencyTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private async Task<T> WithContextAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using var context = _pg.CreateContext();
            return await action(context);
        }

        private Task<ServiceResult<T>> CommentsAsync<T>(Func<CommentService, Task<ServiceResult<T>>> action) =>
            WithContextAsync(context => action(new CommentService(context, TimeProvider.System)));

        private Task<ServiceResult<LikeSummaryResponse>> LikesAsync(Func<LikeService, Task<ServiceResult<LikeSummaryResponse>>> action) =>
            WithContextAsync(context => action(new LikeService(context, TimeProvider.System)));

        private async Task<PostDetailsResponse> CreatePostAsync(int userId)
        {
            var result = await WithContextAsync(context => PostgresFixture.CreatePostService(context).CreateAsync(userId,
                new PostRequest { Title = "Post do dyskusji", Content = "Treść", Category = PostCategory.General }));
            Assert.True(result.Succeeded, result.Message);
            return result.Value!;
        }

        private Task<ServiceResult<CommentResponse>> AddCommentAsync(int userId, int postId, string content = "Komentarz") =>
            CommentsAsync(s => s.AddAsync(userId, postId, new CommentRequest { Content = content }));

        private Task<ServiceResult<CommentResponse>> UpdateCommentAsync(int userId, int commentId, int version, string content) =>
            CommentsAsync(s => s.UpdateAsync(userId, commentId, version, new CommentRequest { Content = content }));

        private Task<ServiceResult> DeleteCommentAsync(int userId, int commentId, int version) =>
            WithContextAsync(context => new CommentService(context, TimeProvider.System).DeleteAsync(userId, commentId, version));

        private Task<CommentResponse?> GetCommentAsync(int commentId) =>
            WithContextAsync(context => new CommentService(context, TimeProvider.System).GetByIdAsync(commentId));

        private Task<int> CountAsync(Func<ApplicationDbContext, Task<int>> query) => WithContextAsync(query);

        // ---------------- Comments ----------------

        // Test A — lost update komentarza.
        [DockerFact]
        public async Task CommentLostUpdate_StaleWriterGets412_AndFirstWriteSurvives()
        {
            var userId = await _pg.CreateUserAsync("ca");
            var post = await CreatePostAsync(userId);
            var created = (await AddCommentAsync(userId, post.Id, "Oryginał")).Value!;

            var readByA = (await GetCommentAsync(created.Id))!;
            var readByB = (await GetCommentAsync(created.Id))!;

            var saveA = await UpdateCommentAsync(userId, created.Id, readByA.Version, "Zmiana A");
            var saveB = await UpdateCommentAsync(userId, created.Id, readByB.Version, "Zmiana B");

            Assert.True(saveA.Succeeded);
            Assert.Equal(ServiceError.PreconditionFailed, saveB.Error);

            var final = (await GetCommentAsync(created.Id))!;
            Assert.Equal("Zmiana A", final.Content);
            Assert.Equal(2, final.Version);
            Assert.Equal(DateTimeKind.Utc, final.UpdatedAt!.Value.Kind);
        }

        // Test A (równolegle) — o zwycięzcy decyduje UPDATE ... WHERE version.
        [DockerFact]
        public async Task ParallelCommentEdits_OfSameVersion_ExactlyOneWins()
        {
            var userId = await _pg.CreateUserAsync("cp");
            var post = await CreatePostAsync(userId);
            var comment = (await AddCommentAsync(userId, post.Id)).Value!;

            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => Task.Run(() => UpdateCommentAsync(userId, comment.Id, 1, $"Wersja {i}"))));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(ServiceError.PreconditionFailed, r.Error));
            Assert.Equal(2, (await GetCommentAsync(comment.Id))!.Version);
        }

        // Test B — A czyta, B usuwa, A zapisuje.
        [DockerFact]
        public async Task CommentDeletedBetweenReadAndUpdate_Returns404_WithoutResurrection()
        {
            var userId = await _pg.CreateUserAsync("cd");
            var post = await CreatePostAsync(userId);
            var comment = (await AddCommentAsync(userId, post.Id)).Value!;

            var readByA = (await GetCommentAsync(comment.Id))!;
            Assert.True((await DeleteCommentAsync(userId, comment.Id, readByA.Version)).Succeeded);

            var update = await UpdateCommentAsync(userId, comment.Id, readByA.Version, "Wskrzeszenie");

            Assert.Equal(ServiceError.NotFound, update.Error);
            Assert.Null(await GetCommentAsync(comment.Id));
        }

        // Test B (równolegle) — delete i update tej samej wersji: nigdy wskrzeszenie ani wyjątek.
        [DockerFact]
        public async Task ParallelCommentDeleteAndUpdate_KeepConsistentState()
        {
            var userId = await _pg.CreateUserAsync("cr");
            var post = await CreatePostAsync(userId);

            for (var i = 0; i < 10; i++)
            {
                var comment = (await AddCommentAsync(userId, post.Id, $"Wyścig {i}")).Value!;

                var update = Task.Run(() => UpdateCommentAsync(userId, comment.Id, 1, $"Edytowany {i}"));
                var delete = Task.Run(() => DeleteCommentAsync(userId, comment.Id, 1));
                await Task.WhenAll(update, delete);

                var remaining = await GetCommentAsync(comment.Id);
                if (delete.Result.Succeeded)
                {
                    Assert.Null(remaining);
                    Assert.True(update.Result.Error is ServiceError.NotFound or ServiceError.PreconditionFailed || update.Result.Succeeded);
                }
                else
                {
                    Assert.Equal(ServiceError.PreconditionFailed, delete.Result.Error);
                    Assert.True(update.Result.Succeeded);
                    Assert.Equal($"Edytowany {i}", remaining!.Content);
                }
            }
        }

        // Test C — równoległe DELETE posta i POST komentarza: nigdy komentarz bez posta, nigdy surowy wyjątek.
        [DockerFact]
        public async Task PostDeleteVsCommentCreate_NeverLeavesOrphanComment()
        {
            var authorId = await _pg.CreateUserAsync("pd");
            var commenterId = await _pg.CreateUserAsync("pc");

            for (var i = 0; i < 15; i++)
            {
                var post = await CreatePostAsync(authorId);

                var comments = Enumerable.Range(0, 3).Select(_ => Task.Run(() => AddCommentAsync(commenterId, post.Id))).ToArray();
                var delete = Task.Run(() => WithContextAsync(context =>
                    PostgresFixture.CreatePostService(context).DeleteAsync(authorId, post.Id, post.Version)));
                await Task.WhenAll(comments.Cast<Task>().Append(delete));

                Assert.True(delete.Result.Succeeded);
                Assert.All(comments, c => Assert.True(c.Result.Succeeded || c.Result.Error == ServiceError.NotFound));
            }

            var orphans = await CountAsync(context => context.Comments.CountAsync(c => !context.Posts.Any(p => p.Id == c.PostId)));
            Assert.Equal(0, orphans);
            Assert.Equal(0, await CountAsync(context => context.Comments.CountAsync(c => c.UserId == commenterId)));
        }

        // Stabilna paginacja przy identycznych znacznikach czasu (tie-breaker = Id).
        [DockerFact]
        public async Task CommentsPagination_IsStable_WithEqualTimestamps()
        {
            var userId = await _pg.CreateUserAsync("ts");
            var post = await CreatePostAsync(userId);
            var sameInstant = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            await using (var context = _pg.CreateContext())
            {
                for (var i = 0; i < 7; i++)
                    context.Comments.Add(new Comment { PostId = post.Id, UserId = userId, Content = $"Równy {i}", CreatedAt = sameInstant });
                await context.SaveChangesAsync();
            }

            var ids = new List<int>();
            string? cursor = null;
            do
            {
                var page = await CommentsAsync(s => s.GetByPostAsync(post.Id, new CommentPageQuery { Limit = 3, Cursor = cursor }));
                ids.AddRange(page.Value!.Items.Select(c => c.Id));
                cursor = page.Value.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(7, ids.Count);
            Assert.Equal(ids.OrderBy(id => id), ids);
            Assert.Equal(ids.Distinct(), ids);
        }

        // ---------------- Reactions ----------------

        private Task<int> LikeRowsAsync(int postId, int? userId = null) =>
            CountAsync(context => context.Likes.CountAsync(l => l.PostId == postId && (userId == null || l.UserId == userId)));

        // Test D — 20 równoległych PUT tego samego użytkownika.
        [DockerFact]
        public async Task TwentyParallelLikes_ProduceExactlyOneRow()
        {
            var userId = await _pg.CreateUserAsync("ld");
            var post = await CreatePostAsync(userId);

            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => LikesAsync(s => s.LikeAsync(userId, post.Id)))));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
            Assert.All(results, r => Assert.True(r.Value!.LikedByCurrentUser));
            Assert.Equal(1, await LikeRowsAsync(post.Id));
        }

        // Test E — 20 równoległych DELETE.
        [DockerFact]
        public async Task TwentyParallelUnlikes_LeaveZeroRows_WithoutErrors()
        {
            var userId = await _pg.CreateUserAsync("le");
            var post = await CreatePostAsync(userId);
            await LikesAsync(s => s.LikeAsync(userId, post.Id));

            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => LikesAsync(s => s.UnlikeAsync(userId, post.Id)))));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
            Assert.Equal(0, await LikeRowsAsync(post.Id));
        }

        // Test F — mieszane PUT/DELETE: stan końcowy zależy od kolejności commitów, ale zawsze 0..1 i bez wyjątków.
        [DockerFact]
        public async Task MixedParallelLikeAndUnlike_KeepInvariantZeroOrOne()
        {
            var userId = await _pg.CreateUserAsync("lf");
            var post = await CreatePostAsync(userId);

            for (var round = 0; round < 10; round++)
            {
                var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                    LikesAsync(s => i % 2 == 0 ? s.LikeAsync(userId, post.Id) : s.UnlikeAsync(userId, post.Id)))));

                Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
                Assert.InRange(await LikeRowsAsync(post.Id), 0, 1);
            }
        }

        [DockerFact]
        public async Task ManyUsersLikingInParallel_EachCountedOnce()
        {
            var authorId = await _pg.CreateUserAsync("lm");
            var post = await CreatePostAsync(authorId);
            var fans = new List<int>();
            for (var i = 0; i < 10; i++)
                fans.Add(await _pg.CreateUserAsync("fan"));

            await Task.WhenAll(fans.SelectMany(fan => new[]
            {
                Task.Run(() => LikesAsync(s => s.LikeAsync(fan, post.Id))),
                Task.Run(() => LikesAsync(s => s.LikeAsync(fan, post.Id)))
            }));

            Assert.Equal(10, await LikeRowsAsync(post.Id));
        }

        [DockerFact]
        public async Task LikeVsPostDelete_NeverLeavesOrphanLike_OrRawException()
        {
            var authorId = await _pg.CreateUserAsync("lp");
            var fanId = await _pg.CreateUserAsync("lq");

            for (var i = 0; i < 15; i++)
            {
                var post = await CreatePostAsync(authorId);

                var like = Task.Run(() => LikesAsync(s => s.LikeAsync(fanId, post.Id)));
                var delete = Task.Run(() => WithContextAsync(context =>
                    PostgresFixture.CreatePostService(context).DeleteAsync(authorId, post.Id, post.Version)));
                await Task.WhenAll(like, delete);

                Assert.True(delete.Result.Succeeded);
                Assert.True(like.Result.Succeeded || like.Result.Error == ServiceError.NotFound);
            }

            Assert.Equal(0, await CountAsync(context => context.Likes.CountAsync(l => l.UserId == fanId)));
        }

        [DockerFact]
        public async Task Database_RejectsDuplicateLike_EvenWhenBypassingService()
        {
            var userId = await _pg.CreateUserAsync("lu");
            var post = await CreatePostAsync(userId);
            await LikesAsync(s => s.LikeAsync(userId, post.Id));

            await using var context = _pg.CreateContext();
            context.Likes.Add(new Like { PostId = post.Id, UserId = userId, CreatedAt = DateTime.UtcNow });

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("UX_likes_post_user", exception.InnerException!.Message);
        }
    }
}
