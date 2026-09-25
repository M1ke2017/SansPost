using Microsoft.EntityFrameworkCore;
using SansPost.Features;
using SansPost.Features.Posts;
using SansPost.Features.Search;

namespace SansPost.Tests.Postgres
{
    // Wyszukiwanie pełnotekstowe na prawdziwym PostgreSQL. Każdy test używa unikalnego tokenu,
    // więc jest odizolowany od danych innych testów we wspólnej bazie.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Discovery")]
    public class SearchTests
    {
        private readonly PostgresFixture _pg;

        public SearchTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private static string NewToken() => "tok" + Guid.NewGuid().ToString("N")[..10];

        private async Task<ServiceResult<KeysetPage<PostSearchResult>>> SearchAsync(string q, PostCategory? category = null, string? cursor = null, int limit = 20, int? viewer = null)
        {
            await using var context = _pg.CreateContext();
            return await new SearchService(context).SearchPostsAsync(
                new PostSearchQuery { Q = q, Category = category, Cursor = cursor, Limit = limit }, viewer);
        }

        private async Task<List<int>> SearchAllPagesAsync(string q, int limit)
        {
            var ids = new List<int>();
            string? cursor = null;
            do
            {
                var page = await SearchAsync(q, cursor: cursor, limit: limit);
                Assert.True(page.Succeeded, page.Message);
                ids.AddRange(page.Value!.Items.Select(r => r.Id));
                cursor = page.Value.NextCursor;
            }
            while (cursor is not null);

            return ids;
        }

        // A — wystąpienie w tytule (waga A) wygrywa z równoważnym wystąpieniem w treści (waga B).
        [DockerFact]
        public async Task TitleMatch_OutranksEquivalentContentMatch()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sa");
            var createdAt = DateTime.UtcNow.AddMinutes(-5);
            var inContent = await _pg.SeedPostAsync(userId, "Zwykły tytuł", $"Treść zawiera {token} raz.", createdAt: createdAt);
            var inTitle = await _pg.SeedPostAsync(userId, $"Tytuł z {token}", "Treść bez słowa kluczowego.", createdAt: createdAt.AddMinutes(-1));

            var result = await SearchAsync(token);

            Assert.Equal(new[] { inTitle, inContent }, result.Value!.Items.Select(r => r.Id));
        }

        // B — tylko pasujące posty.
        [DockerFact]
        public async Task Search_ReturnsMatchingPosts_AndNothingElse()
        {
            var token = NewToken();
            var other = NewToken();
            var userId = await _pg.CreateUserAsync("sb");
            var a = await _pg.SeedPostAsync(userId, "Pierwszy", $"O podróżach {token}");
            var b = await _pg.SeedPostAsync(userId, $"{token} w tytule", "Treść");
            await _pg.SeedPostAsync(userId, "Niepasujący", $"Coś zupełnie innego {other}");

            var result = await SearchAsync(token);

            Assert.Equal(new[] { a, b }.OrderBy(x => x), result.Value!.Items.Select(r => r.Id).OrderBy(x => x));
            Assert.Empty((await SearchAsync(NewToken())).Value!.Items);
        }

        // C — filtr kategorii razem z wyszukiwaniem.
        [DockerFact]
        public async Task CategoryFilter_CombinesWithSearch()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sc");
            var travel = await _pg.SeedPostAsync(userId, $"Wyjazd {token}", "Treść", PostCategory.Travel);
            await _pg.SeedPostAsync(userId, $"Gra {token}", "Treść", PostCategory.Games);

            var result = await SearchAsync(token, PostCategory.Travel);

            Assert.Equal(travel, Assert.Single(result.Value!.Items).Id);
        }

        // D — kilkadziesiąt wyników: brak duplikatów, brak pominięć, kolejność niezależna od rozmiaru strony.
        [DockerFact]
        public async Task Pagination_OverSixtyResults_HasNoDuplicatesOrGaps_AndIsStable()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sd");
            var baseTime = DateTime.UtcNow.AddHours(-1);
            var titleMatches = new HashSet<int>();
            var all = new HashSet<int>();

            for (var i = 0; i < 60; i++)
            {
                // Co trzy posty ten sam CreatedAt — remisy rozstrzyga Id.
                var createdAt = baseTime.AddSeconds(-(i / 3));
                var id = i % 3 == 0
                    ? await _pg.SeedPostAsync(userId, $"Tytuł {token} {i}", "Treść bez tokenu", createdAt: createdAt)
                    : await _pg.SeedPostAsync(userId, $"Tytuł {i}", $"Treść z {token}", createdAt: createdAt);
                all.Add(id);
                if (i % 3 == 0)
                    titleMatches.Add(id);
            }

            var smallPages = await SearchAllPagesAsync(token, limit: 7);
            var largePages = await SearchAllPagesAsync(token, limit: 50);

            Assert.Equal(60, smallPages.Count);
            Assert.Equal(smallPages.Count, smallPages.Distinct().Count());
            Assert.True(all.SetEquals(smallPages));
            Assert.Equal(largePages, smallPages);

            // Wszystkie trafienia w tytule przed trafieniami tylko w treści.
            Assert.True(titleMatches.SetEquals(smallPages.Take(titleMatches.Count)));
        }

        // E — identyczny rank i CreatedAt → rozstrzyga Id (malejąco).
        [DockerFact]
        public async Task EqualRankAndCreatedAt_AreTieBrokenById()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("se");
            var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            var first = await _pg.SeedPostAsync(userId, $"Bliźniak {token}", "Ta sama treść", createdAt: createdAt);
            var second = await _pg.SeedPostAsync(userId, $"Bliźniak {token}", "Ta sama treść", createdAt: createdAt);

            var page1 = await SearchAsync(token, limit: 1);
            var page2 = await SearchAsync(token, cursor: page1.Value!.NextCursor, limit: 1);

            Assert.Equal(second, page1.Value.Items.Single().Id);
            Assert.Equal(first, page2.Value!.Items.Single().Id);
            Assert.False(page2.Value.HasMore);
        }

        // F — polskie znaki: bez wyjątku, dopasowanie bez względu na wielkość liter.
        [DockerFact]
        public async Task PolishCharacters_MatchCaseInsensitively()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sf");
            var id = await _pg.SeedPostAsync(userId, "Zażółć gęślą jaźń", $"Łódź, źdźbło {token}");

            var byTitle = await SearchAsync($"ZAŻÓŁĆ {token}");
            var byContent = await SearchAsync($"łódź {token}");

            Assert.Equal(id, Assert.Single(byTitle.Value!.Items).Id);
            Assert.Equal(id, Assert.Single(byContent.Value!.Items).Id);
        }

        // G — znaki specjalne: brak błędu SQL, brak wstrzyknięcia (tekst zawsze jako parametr).
        [DockerTheory]
        [InlineData("'; DROP TABLE posts; --")]
        [InlineData("\"niezamknięty cudzysłów")]
        [InlineData("a & b | c ! (d)")]
        [InlineData("100% _dopasowanie_")]
        [InlineData("back\\slash :* <-> ::")]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("--")]
        [InlineData("!!! ??? ...")]
        public async Task SpecialCharacters_NeverCauseSqlErrors(string query)
        {
            var result = await SearchAsync(query);

            Assert.True(result.Succeeded, result.Message);
            await using var context = _pg.CreateContext();
            Assert.True(await context.Posts.AnyAsync());
        }

        [DockerFact]
        public async Task ApostrophesAndQuotes_StillFindMatchingPosts()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sq");
            var id = await _pg.SeedPostAsync(userId, $"Książka O'Reilly {token}", "Treść");

            Assert.Equal(id, Assert.Single((await SearchAsync($"O'Reilly {token}")).Value!.Items).Id);
            Assert.Equal(id, Assert.Single((await SearchAsync($"\"O'Reilly {token}\"")).Value!.Items).Id);
        }

        [DockerFact]
        public async Task EditedPost_IsReindexedAutomatically_ByGeneratedColumn()
        {
            var before = NewToken();
            var after = NewToken();
            var userId = await _pg.CreateUserAsync("sg");

            await using (var context = _pg.CreateContext())
            {
                var created = (await PostgresFixture.CreatePostService(context).CreateAsync(userId,
                    new PostRequest { Title = $"Stary {before}", Content = "Treść", Category = PostCategory.General })).Value!;
                context.ChangeTracker.Clear();
                var updated = await PostgresFixture.CreatePostService(context).UpdateAsync(userId, created.Id, created.Version,
                    new PostRequest { Title = $"Nowy {after}", Content = "Treść", Category = PostCategory.General });
                Assert.True(updated.Succeeded);
            }

            Assert.Empty((await SearchAsync(before)).Value!.Items);
            Assert.Single((await SearchAsync(after)).Value!.Items);
        }

        [DockerFact]
        public async Task CursorFromDifferentQuery_IsRejected()
        {
            var token = NewToken();
            var userId = await _pg.CreateUserAsync("sh");
            await _pg.SeedPostAsync(userId, $"A {token}", "x");
            await _pg.SeedPostAsync(userId, $"B {token}", "x");

            var page = await SearchAsync(token, limit: 1);
            var reused = await SearchAsync(NewToken(), cursor: page.Value!.NextCursor, limit: 1);

            Assert.Equal(ServiceError.Validation, reused.Error);
        }
    }
}
