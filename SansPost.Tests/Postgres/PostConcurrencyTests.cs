using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Tests.Postgres
{
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Concurrency")]
    public class PostConcurrencyTests
    {
        private readonly PostgresFixture _pg;

        public PostConcurrencyTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private static PostRequest Request(string title) => new()
        {
            Title = title,
            Content = "Treść posta",
            Category = PostCategory.General
        };

        private async Task<PostDetailsResponse> CreatePostAsync(int userId, string title = "Oryginał")
        {
            await using var context = _pg.CreateContext();
            var result = await PostgresFixture.CreatePostService(context).CreateAsync(userId, Request(title));
            Assert.True(result.Succeeded, result.Message);
            return result.Value!;
        }

        private async Task<ServiceResult<PostDetailsResponse>> UpdateAsync(int userId, int postId, int version, string title)
        {
            await using var context = _pg.CreateContext();
            return await PostgresFixture.CreatePostService(context).UpdateAsync(userId, postId, version, Request(title));
        }

        private async Task<ServiceResult> DeleteAsync(int userId, int postId, int version)
        {
            await using var context = _pg.CreateContext();
            return await PostgresFixture.CreatePostService(context).DeleteAsync(userId, postId, version);
        }

        private async Task<PostDetailsResponse?> GetAsync(int postId)
        {
            await using var context = _pg.CreateContext();
            return await PostgresFixture.CreatePostService(context).GetByIdAsync(postId, null);
        }

        // Test A — lost update.
        [DockerFact]
        public async Task LostUpdate_StaleWriterIsRejected_AndFirstWriteSurvives()
        {
            var userId = await _pg.CreateUserAsync();
            var post = await CreatePostAsync(userId);

            var readByA = (await GetAsync(post.Id))!;
            var readByB = (await GetAsync(post.Id))!;
            Assert.Equal(readByA.Version, readByB.Version);

            var saveA = await UpdateAsync(userId, post.Id, readByA.Version, "Zmiana A");
            var saveB = await UpdateAsync(userId, post.Id, readByB.Version, "Zmiana B");

            Assert.True(saveA.Succeeded);
            Assert.Equal(ServiceError.PreconditionFailed, saveB.Error);

            var final = (await GetAsync(post.Id))!;
            Assert.Equal("Zmiana A", final.Title);
            Assert.Equal(readByA.Version + 1, final.Version);
            Assert.NotNull(final.UpdatedAt);
            Assert.Equal(DateTimeKind.Utc, final.UpdatedAt!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, final.CreatedAt.Kind);
            Assert.Equal(post.CreatedAt, final.CreatedAt);
        }

        // Test A (wariant równoległy) — wszyscy przechodzą sprawdzenie wersji w pamięci; rozstrzyga WHERE version w UPDATE.
        [DockerFact]
        public async Task ParallelUpdatesOfSameVersion_ExactlyOneWins()
        {
            var userId = await _pg.CreateUserAsync();
            var post = await CreatePostAsync(userId);

            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => Task.Run(() => UpdateAsync(userId, post.Id, post.Version, $"Wersja {i}"))));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(ServiceError.PreconditionFailed, r.Error));

            var final = (await GetAsync(post.Id))!;
            Assert.Equal(post.Version + 1, final.Version);
            Assert.Equal(results.Single(r => r.Succeeded).Value!.Title, final.Title);
        }

        // Test B — dzienny limit postów (v1.0) pod równoległymi requestami: licznik w PostgreSQL nigdy nie przekracza 10,
        // a postów powstaje dokładnie tyle, ile sukcesów (zużycie limitu i INSERT w jednej transakcji).
        [DockerTheory]
        [InlineData(9, 5, 1)]
        [InlineData(0, 25, 10)]
        public async Task QuotaRace_ParallelCreates_NeverExceedDailyLimit(int usedToday, int parallelRequests, int expectedSuccesses)
        {
            var userId = await _pg.CreateUserAsync();
            if (usedToday > 0)
                await _pg.SeedUsageAsync(userId, posts: usedToday);

            var results = await Task.WhenAll(Enumerable.Range(0, parallelRequests).Select(i => Task.Run(async () =>
            {
                await using var context = _pg.CreateContext();
                return await PostgresFixture.CreatePostService(context).CreateAsync(userId, Request($"Równoległy {i}"));
            })));

            Assert.Equal(expectedSuccesses, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r =>
            {
                Assert.Equal(ServiceError.RateLimited, r.Error);
                Assert.Equal("daily-post-limit-reached", r.Code);
            });
            Assert.Equal(expectedSuccesses, await _pg.CountPostsAsync(userId));
            Assert.Equal(SansPost.Features.Usage.DailyQuota.Posts, (await _pg.UsageAsync(userId)).Posts);
        }

        // Test C — usunięcie między odczytem a zapisem.
        [DockerFact]
        public async Task DeleteBetweenReadAndUpdate_Returns404_AndDoesNotRecreatePost()
        {
            var userId = await _pg.CreateUserAsync();
            var post = await CreatePostAsync(userId);

            var readByA = (await GetAsync(post.Id))!;
            Assert.True((await DeleteAsync(userId, post.Id, readByA.Version)).Succeeded);

            var staleUpdate = await UpdateAsync(userId, post.Id, readByA.Version, "Po usunięciu");

            Assert.Equal(ServiceError.NotFound, staleUpdate.Error);
            Assert.Null(await GetAsync(post.Id));
            Assert.Equal(0, await _pg.CountPostsAsync(userId));
        }

        // Test C (wariant równoległy) — niezależnie od kolejności: brak wskrzeszenia, brak wyjątku, spójny stan.
        [DockerFact]
        public async Task ParallelDeleteAndUpdate_NeverResurrectOrThrow()
        {
            var userId = await _pg.CreateUserAsync();

            for (var i = 0; i < 10; i++)
            {
                var post = await CreatePostAsync(userId, $"Wyścig {i}");

                var update = Task.Run(() => UpdateAsync(userId, post.Id, post.Version, $"Edytowany {i}"));
                var delete = Task.Run(() => DeleteAsync(userId, post.Id, post.Version));
                await Task.WhenAll(update, delete);

                var remaining = await GetAsync(post.Id);
                if (delete.Result.Succeeded)
                {
                    Assert.Null(remaining);
                    Assert.True(update.Result.Succeeded || update.Result.Error == ServiceError.NotFound);
                }
                else
                {
                    // Update wygrał — DELETE ze starą wersją odrzucony (412), edycja zachowana.
                    Assert.Equal(ServiceError.PreconditionFailed, delete.Result.Error);
                    Assert.True(update.Result.Succeeded);
                    Assert.Equal($"Edytowany {i}", remaining!.Title);
                    Assert.True((await DeleteAsync(userId, post.Id, remaining.Version)).Succeeded);
                }
            }

            Assert.Equal(0, await _pg.CountPostsAsync(userId));
        }

        // Test D — realny scenariusz niepowodzenia transakcyjnego CreatePost:
        // klient anuluje request, gdy transakcja czeka na blokadę limitu. Żadnego częściowego stanu, blokada zwolniona.
        [DockerFact]
        public async Task CancelledWhileWaitingForQuotaLock_RollsBackWithoutPartialState()
        {
            var userId = await _pg.CreateUserAsync();
            await _pg.SeedPostsAsync(userId, 3);

            await using (var holder = _pg.CreateContext())
            {
                await using var holderTransaction = await holder.Database.BeginTransactionAsync();
                Assert.True(await holder.LockUserRowAsync(userId, CancellationToken.None));

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await using var context = _pg.CreateContext();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    PostgresFixture.CreatePostService(context).CreateAsync(userId, Request("Anulowany"), cts.Token));

                await holderTransaction.RollbackAsync();
            }

            Assert.Equal(3, await _pg.CountPostsAsync(userId));
            Assert.Equal(0, (await _pg.UsageAsync(userId)).Posts);   // nieudana operacja nie zużywa dziennego limitu

            // Blokada nie wisi po anulowanej transakcji — kolejny request przechodzi od razu.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var next = _pg.CreateContext();
            var result = await PostgresFixture.CreatePostService(next).CreateAsync(userId, Request("Po anulowaniu"), timeout.Token);

            Assert.True(result.Succeeded);
            Assert.Equal(4, await _pg.CountPostsAsync(userId));
        }
    }
}
