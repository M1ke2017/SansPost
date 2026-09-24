using SansPost.Features;
using SansPost.Features.Posts;
using SansPost.Features.Search;

using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Tryby feedu i discovery na prawdziwym PostgreSQL. Izolacja przez filtr autora (każdy test ma własnego).
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Discovery")]
    public class DiscoveryFeedTests
    {
        private readonly PostgresFixture _pg;

        public DiscoveryFeedTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private async Task<KeysetPage<PostSummaryResponse>> FeedAsync(PostFeedQuery query, string? connectionString = null)
        {
            await using var context = _pg.CreateContext(connectionString);
            var result = await PostgresFixture.CreatePostService(context).GetFeedAsync(query, null);
            Assert.True(result.Succeeded, result.Message);
            return result.Value!;
        }

        private async Task<List<int>> AllPagesAsync(PostFeedQuery template, int limit)
        {
            var ids = new List<int>();
            string? cursor = null;
            do
            {
                var page = await FeedAsync(new PostFeedQuery
                {
                    AuthorId = template.AuthorId,
                    Category = template.Category,
                    Sort = template.Sort,
                    Limit = limit,
                    Cursor = cursor
                });
                ids.AddRange(page.Items.Select(p => p.Id));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            return ids;
        }

        // H — Newest: CreatedAt DESC, Id DESC (także przy identycznych znacznikach czasu).
        [DockerFact]
        public async Task Newest_IsOrderedByCreatedAtThenId()
        {
            var authorId = await _pg.CreateUserAsync("fh");
            var baseTime = DateTime.UtcNow.AddHours(-2);
            var seeded = new List<(int Id, DateTime CreatedAt)>();
            for (var i = 0; i < 12; i++)
            {
                var createdAt = baseTime.AddMinutes(i / 2); // pary z tym samym czasem
                seeded.Add((await _pg.SeedPostAsync(authorId, $"Post {i}", "Treść", createdAt: createdAt), createdAt));
            }

            var ids = await AllPagesAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Newest }, limit: 5);

            var expected = seeded.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Select(p => p.Id);
            Assert.Equal(expected, ids);
        }

        // I — przy porównywalnym wieku wygrywa większe zaangażowanie.
        [DockerFact]
        public async Task Popular_HigherEngagementWins_AtComparableAge()
        {
            var authorId = await _pg.CreateUserAsync("fi");
            var fans = await _pg.CreateUsersFastAsync(6);
            var createdAt = DateTime.UtcNow.AddHours(-1);
            var quiet = await _pg.SeedPostAsync(authorId, "Cichy", "Treść", createdAt: createdAt.AddMinutes(1));
            var engaging = await _pg.SeedPostAsync(authorId, "Angażujący", "Treść", createdAt: createdAt);
            await _pg.AddLikesAsync(quiet, fans.Take(1));
            await _pg.AddLikesAsync(engaging, fans.Take(4));
            await _pg.AddCommentsAsync(engaging, fans[5], 2);

            var feed = await FeedAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular });

            Assert.Equal(new[] { engaging, quiet }, feed.Items.Select(p => p.Id));
        }

        // J — stary post z historycznymi polubieniami nie dominuje nad świeżą, aktywną treścią.
        [DockerFact]
        public async Task Popular_OldHeavilyLikedPost_DoesNotDominateFreshActivePost()
        {
            var authorId = await _pg.CreateUserAsync("fj");
            var fans = await _pg.CreateUsersFastAsync(30);
            var old = await _pg.SeedPostAsync(authorId, "Stary hit", "Treść", createdAt: DateTime.UtcNow.AddDays(-10));
            var fresh = await _pg.SeedPostAsync(authorId, "Świeży", "Treść", createdAt: DateTime.UtcNow.AddHours(-1));
            await _pg.AddLikesAsync(old, fans);                 // 30 polubień, 10 dni temu
            await _pg.AddLikesAsync(fresh, fans.Take(2));       // 2 polubienia, godzinę temu

            var feed = await FeedAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular });

            Assert.Equal(new[] { fresh, old }, feed.Items.Select(p => p.Id));
        }

        [DockerFact]
        public async Task Popular_ExcludesPostsOutsideTheFreshnessWindow_ButNewestStillShowsThem()
        {
            var authorId = await _pg.CreateUserAsync("fw");
            var ancient = await _pg.SeedPostAsync(authorId, "Archiwum", "Treść", createdAt: DateTime.UtcNow.AddDays(-(PostLimits.PopularWindowDays + 1)));
            var recent = await _pg.SeedPostAsync(authorId, "Aktualny", "Treść", createdAt: DateTime.UtcNow.AddDays(-1));

            var popular = await FeedAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular });
            var newest = await FeedAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Newest });

            Assert.Equal(new[] { recent }, popular.Items.Select(p => p.Id));
            Assert.Equal(new[] { recent, ancient }, newest.Items.Select(p => p.Id));
        }

        // K — paginacja Popular: stabilna, bez duplikatów i pominięć (także przy remisach wyniku).
        [DockerFact]
        public async Task PopularPagination_IsStable_WithoutDuplicatesOrGaps()
        {
            var authorId = await _pg.CreateUserAsync("fk");
            var fans = await _pg.CreateUsersFastAsync(4);
            var baseTime = DateTime.UtcNow.AddHours(-6);
            var all = new HashSet<int>();

            for (var i = 0; i < 40; i++)
            {
                // Remisy celowo: te same polubienia i ten sam czas w grupach.
                var id = await _pg.SeedPostAsync(authorId, $"Popularny {i}", "Treść", createdAt: baseTime.AddMinutes(i / 4 * 10));
                await _pg.AddLikesAsync(id, fans.Take(i % 5));
                all.Add(id);
            }

            var small = await AllPagesAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular }, limit: 6);
            var large = await AllPagesAsync(new PostFeedQuery { AuthorId = authorId, Sort = PostSort.Popular }, limit: 50);

            Assert.Equal(40, small.Count);
            Assert.Equal(small.Count, small.Distinct().Count());
            Assert.True(all.SetEquals(small));
            Assert.Equal(large, small);
        }

        [DockerFact]
        public async Task Popular_RespectsCategoryFilter()
        {
            var authorId = await _pg.CreateUserAsync("fc");
            var ideas = await _pg.SeedPostAsync(authorId, "Pomysł", "Treść", PostCategory.Ideas, DateTime.UtcNow.AddHours(-1));
            await _pg.SeedPostAsync(authorId, "Gra", "Treść", PostCategory.Games, DateTime.UtcNow.AddHours(-1));

            var feed = await FeedAsync(new PostFeedQuery { AuthorId = authorId, Category = PostCategory.Ideas, Sort = PostSort.Popular });

            Assert.Equal(ideas, Assert.Single(feed.Items).Id);
        }

        [DockerFact]
        public async Task CategoryDiscovery_ReturnsAllCategoriesWithCountsAndLatestActivity()
        {
            var connectionString = await _pg.CreateMigratedDatabaseAsync($"categories_{Guid.NewGuid():N}");
            await using var context = _pg.CreateContext(connectionString);
            var authorId = (await TestServices.Auth(context).RegisterAsync(new()
            {
                Username = "kategorie",
                Email = "kategorie@example.com",
                Password = "correct-horse-battery"
            })).Value!.Id;

            var latestTravel = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
            context.Posts.AddRange(
                new Post { UserId = authorId, Title = "T1", Content = "x", Category = PostCategory.Travel, CreatedAt = latestTravel.AddDays(-2) },
                new Post { UserId = authorId, Title = "T2", Content = "x", Category = PostCategory.Travel, CreatedAt = latestTravel },
                new Post { UserId = authorId, Title = "T3", Content = "x", Category = PostCategory.Travel, CreatedAt = latestTravel.AddDays(-1) },
                new Post { UserId = authorId, Title = "G1", Content = "x", Category = PostCategory.Games, CreatedAt = latestTravel.AddDays(-5) });
            await context.SaveChangesAsync();

            var categories = await PostgresFixture.CreatePostService(context).GetCategoriesAsync();

            Assert.Equal(Enum.GetValues<PostCategory>(), categories.Select(c => c.Category));
            Assert.Equal((3, (DateTime?)latestTravel), categories.Where(c => c.Category == PostCategory.Travel).Select(c => (c.PostCount, c.LatestPostAt)).Single());
            Assert.Equal(1, categories.Single(c => c.Category == PostCategory.Games).PostCount);
            Assert.Equal((0, (DateTime?)null), categories.Where(c => c.Category == PostCategory.Ideas).Select(c => (c.PostCount, c.LatestPostAt)).Single());
        }

        [DockerFact]
        public async Task UserSearch_IsCaseInsensitivePrefix_OnPublicUsernameOnly()
        {
            var marker = "zz" + Guid.NewGuid().ToString("N")[..8];
            var matchIds = await _pg.CreateUsersFastAsync(3, prefix: marker.ToUpperInvariant());
            // Użytkownik, którego EMAIL (nie nazwa) zaczyna się od markera — nie może zostać znaleziony.
            await _pg.CreateUsersFastAsync(1, prefix: "other", emailPrefix: marker + "secret");

            await using var context = _pg.CreateContext();
            var service = new PostgresSearchService(context);
            var byPrefix = await service.SearchUsersAsync(marker.ToLowerInvariant(), 10);
            var byEmail = await service.SearchUsersAsync(marker + "secret", 10);

            Assert.True(matchIds.ToHashSet().SetEquals(byPrefix.Value!.Select(u => u.UserId)));
            Assert.Empty(byEmail.Value!);
            Assert.Equal(new[] { "UserId", "Username", "JoinedAt", "PostCount" },
                typeof(UserSearchResult).GetProperties().Select(p => p.Name));
        }

        [DockerFact]
        public async Task UserSearch_TreatsLikeWildcardsLiterally()
        {
            await using var context = _pg.CreateContext();
            var result = await new PostgresSearchService(context).SearchUsersAsync("%_", 10);

            Assert.True(result.Succeeded);
            Assert.Empty(result.Value!);
        }
    }
}
