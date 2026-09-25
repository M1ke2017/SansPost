using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Demo;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Sprint 8C: treści startowe przez PRAWDZIWY start aplikacji (hosted service) na świeżym PostgreSQL —
    // to, co widzi gość po `dotnet run` z SeedContent = true.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "DemoSeed")]
    public sealed class DemoSeedStartupPostgresTests
    {
        private static readonly Dictionary<string, string?> SeedEnabled = new()
        {
            ["PublicDemo:SeedContent"] = "true",
            ["PublicDemo:MaxPublicAccounts"] = "100"
        };

        private readonly PostgresFixture _pg;

        public DemoSeedStartupPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        // 2, 3, 4 — start aplikacji → gość (bez logowania) widzi posty, liczniki kategorii > 0, wyszukiwarka je znajduje.
        // 5 — drugi start na tej samej bazie niczego nie duplikuje.
        [DockerFact]
        public async Task AppStartup_WithSeedContent_GivesGuestNonEmptyFeed_AndSecondStartDoesNotDuplicate()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"startup_seed_{Guid.NewGuid():N}");

            using (var factory = new PostgresApiFactory(db, SeedEnabled))
            {
                var guest = factory.CreateHttpsClient();

                var feed = await guest.GetFromJsonAsync<KeysetPage<PostSummaryResponse>>("/api/posts?limit=50", SansPostFactory.Json);
                Assert.Equal(DemoContent.Posts.Count, feed!.Items.Count);

                var categories = await guest.GetFromJsonAsync<List<CategorySummaryResponse>>("/api/categories", SansPostFactory.Json);
                Assert.All(categories!, c => Assert.True(c.PostCount > 0, $"{c.Category}: brak postów"));
                Assert.Equal(DemoContent.Posts.Count, categories!.Sum(c => c.PostCount));

                using var search = JsonDocument.Parse(await guest.GetStringAsync("/api/search/posts?q=planszówka"));
                Assert.NotEmpty(search.RootElement.GetProperty("items").EnumerateArray());

                var home = await guest.GetStringAsync("/saloon");
                Assert.Contains(DemoContent.WelcomeTitle, home);
                Assert.DoesNotContain("Nie ma jeszcze post", home);
            }

            using (var restarted = new PostgresApiFactory(db, SeedEnabled))
            {
                restarted.CreateHttpsClient(); // start hosta = ponowne uruchomienie seedera
                await using var context = _pg.CreateContext(db);
                Assert.Equal(DemoContent.Posts.Count, await context.Posts.CountAsync());
                Assert.Equal(DemoContent.Authors.Count, await context.Users.CountAsync());
            }
        }

        // Domyślna konfiguracja (bez SeedContent): świeża baza pozostaje pusta — nic nie jest dopisywane "na siłę".
        [DockerFact]
        public async Task AppStartup_WithoutSeedContent_LeavesDatabaseEmpty()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"startup_noseed_{Guid.NewGuid():N}");

            using var factory = new PostgresApiFactory(db);
            var feed = await factory.CreateHttpsClient().GetFromJsonAsync<KeysetPage<PostSummaryResponse>>("/api/posts", SansPostFactory.Json);

            Assert.Empty(feed!.Items);
        }

        // Niepełny seed (konta demo bez postów) nie może dawać fałszywego AlreadySeeded — seeder dokańcza treści,
        // używając istniejących kont (bez duplikatów).
        [DockerFact]
        public async Task IncompleteSeed_AccountsWithoutPosts_IsCompleted()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"partial_seed_{Guid.NewGuid():N}");
            await using (var context = _pg.CreateContext(db))
            {
                foreach (var alias in DemoContent.Authors.Take(3))
                {
                    var email = $"{alias.ToLowerInvariant()}@{DemoContent.EmailDomain}";
                    context.Users.Add(new User
                    {
                        Username = alias,
                        NormalizedUsername = IdentityNormalizer.Normalize(alias),
                        Email = email,
                        NormalizedEmail = IdentityNormalizer.Normalize(email),
                        PasswordHash = "!demo-account-no-login"
                    });
                }
                await context.SaveChangesAsync();
            }

            await using (var context = _pg.CreateContext(db))
            {
                var seeder = new DemoContentSeeder(context, Options.Create(new PublicDemoOptions { SeedContent = true, MaxPublicAccounts = 100 }),
                    new AliasGenerator(context), TimeProvider.System, NullLogger<DemoContentSeeder>.Instance);
                Assert.Equal(DemoSeedResult.Seeded, await seeder.RunAsync());
            }

            await using var check = _pg.CreateContext(db);
            Assert.Equal(DemoContent.Authors.Count, await check.Users.CountAsync());
            Assert.Equal(DemoContent.Posts.Count, await check.Posts.CountAsync());
        }
    }
}
