using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Demo;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Przydomki i treści startowe demo na prawdziwym PostgreSQL (blokada bramy, UNIQUE, transakcje).
    // Każdy test na własnej bazie — seed i losowe przydomki nie mieszają się z pulą przydomków testowych.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Aliases")]
    public sealed class AliasAndDemoSeedPostgresTests
    {
        private readonly PostgresFixture _pg;

        public AliasAndDemoSeedPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private DemoContentSeeder Seeder(Infrastructure.Persistence.ApplicationDbContext context, bool enabled = true, int max = 1000) =>
            new(context, Options.Create(new PublicDemoOptions { SeedContent = enabled, MaxPublicAccounts = max }),
                new AliasGenerator(context), TimeProvider.System, NullLogger<DemoContentSeeder>.Instance);

        private async Task<DemoSeedResult> SeedAsync(string db, bool enabled = true, int max = 1000)
        {
            await using var context = _pg.CreateContext(db);
            return await Seeder(context, enabled, max).RunAsync();
        }

        // G5 — ten sam przydomek w 10 równoległych rejestracjach: dokładnie jedno konto, reszta 409 alias-taken, zero 500.
        [DockerFact]
        public async Task ParallelRegistrations_WithSameAlias_CreateExactlyOneAccount()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"alias_{Guid.NewGuid():N}");

            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            {
                await using var context = _pg.CreateContext(db);
                return await TestServices.Auth(context).RegisterAsync(new RegisterRequest
                {
                    Username = "DustyHawk",
                    Email = $"race{i}@example.com",
                    Password = TestUsers.Password
                });
            }));

            Assert.Single(results, r => r.Succeeded);
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(AuthService.AliasTakenCode, r.Code));
            await using var check = _pg.CreateContext(db);
            Assert.Equal(1, await check.Users.CountAsync(u => u.NormalizedUsername == "DUSTYHAWK"));
        }

        // Rejestracje bez przydomka (przydział przez serwer) równolegle — każdy dostaje inny, poprawny przydomek.
        [DockerFact]
        public async Task ParallelRegistrations_WithoutAlias_GetDistinctCuratedAliases()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"alias_auto_{Guid.NewGuid():N}");

            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
            {
                await using var context = _pg.CreateContext(db);
                return await TestServices.Auth(context).RegisterAsync(new RegisterRequest { Email = $"auto{i}@example.com", Password = TestUsers.Password });
            }));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
            var aliases = results.Select(r => r.Value!.Username).ToList();
            Assert.All(aliases, a => Assert.True(WesternAliases.IsCurated(a)));
            Assert.Equal(aliases.Count, aliases.Distinct().Count());
        }

        // G8
        [DockerFact]
        public async Task SeedDisabled_CreatesNothing()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_off_{Guid.NewGuid():N}");

            Assert.Equal(DemoSeedResult.Disabled, await SeedAsync(db, enabled: false));

            await using var context = _pg.CreateContext(db);
            Assert.Equal(0, await context.Users.CountAsync());
            Assert.Equal(0, await context.Posts.CountAsync());
        }

        // G9, G10, G12, G13 — pierwszy seed tworzy dane z FK w porządku; drugi nic nie duplikuje.
        [DockerFact]
        public async Task Seed_CreatesStarterContentOnce_AcrossCategories_WithValidRelations()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_{Guid.NewGuid():N}");

            Assert.Equal(DemoSeedResult.Seeded, await SeedAsync(db));
            Assert.Equal(DemoSeedResult.AlreadySeeded, await SeedAsync(db));

            await using var context = _pg.CreateContext(db);
            var users = await context.Users.ToListAsync();
            Assert.Equal(DemoContent.Authors.Count, users.Count);
            Assert.All(users, u =>
            {
                Assert.True(WesternAliases.IsCurated(u.Username));
                Assert.True(DemoContent.IsDemoEmail(u.Email));
                Assert.Equal(UserRole.User, u.Role);
            });

            Assert.Equal(DemoContent.Posts.Count, await context.Posts.CountAsync());
            Assert.True(await context.Posts.Select(p => p.Category).Distinct().CountAsync() >= 6);

            var expectedComments = DemoContent.Posts.Sum(p => p.Comments.Count);
            var expectedLikes = DemoContent.Posts.Sum(p => p.LikedBy.Distinct().Count());
            Assert.Equal(expectedComments, await context.Comments.CountAsync(c => c.Post.UserId > 0 && c.User.Id > 0));
            Assert.Equal(expectedLikes, await context.Likes.CountAsync(l => l.Post.Id > 0 && l.User.Id > 0));
            Assert.True(await context.Posts.CountAsync(p => !context.Likes.Any(l => l.PostId == p.Id)) > 0);   // nie wszystko ma reakcje

            // Konta demo nie mogą się zalogować.
            var auth = TestServices.Auth(context);
            Assert.Null(await auth.AuthenticateAsync(users[0].Email, "anything-at-all"));
        }

        // Tablica "Zajęte miejsca" po świeżym wdrożeniu z treściami startowymi: konta demo nie zajmują miejsc (0 / 100),
        // dwie zwykłe rejestracje — 2 / 100 (reguła liczenia przetłumaczona na SQL PostgreSQL).
        [DockerFact]
        public async Task Capacity_AfterDemoSeed_CountsOnlyRegisteredAccounts()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_{Guid.NewGuid():N}");
            Assert.Equal(DemoSeedResult.Seeded, await SeedAsync(db));

            await using var context = _pg.CreateContext(db);
            var auth = TestServices.Auth(context, new PublicDemoOptions { MaxPublicAccounts = 100 });
            Assert.Equal(new RegistrationCapacity(0, 100), await auth.GetCapacityAsync());

            foreach (var email in new[] { "first@example.com", "second@example.com" })
                Assert.True((await auth.RegisterAsync(new RegisterRequest { Email = email, Password = TestUsers.Password })).Succeeded);
            Assert.Equal(new RegistrationCapacity(2, 100), await auth.GetCapacityAsync());
        }

        // G11 — kilka instancji startuje jednocześnie: blokada bramy → seed dokładnie raz.
        [DockerFact]
        public async Task ParallelStartup_SeedsExactlyOnce()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_par_{Guid.NewGuid():N}");

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SeedAsync(db)));

            Assert.Single(results, r => r == DemoSeedResult.Seeded);
            Assert.All(results.Where(r => r != DemoSeedResult.Seeded), r => Assert.Equal(DemoSeedResult.AlreadySeeded, r));
            await using var context = _pg.CreateContext(db);
            Assert.Equal(DemoContent.Posts.Count, await context.Posts.CountAsync());
            Assert.Equal(DemoContent.Authors.Count, await context.Users.CountAsync());
        }

        // E8 — konta demo zajmują publiczne sloty; przy braku miejsca seed jest pomijany zamiast omijać limit.
        [DockerFact]
        public async Task Seed_RespectsPublicAccountCapacity()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_cap_{Guid.NewGuid():N}");

            Assert.Equal(DemoSeedResult.InsufficientCapacity, await SeedAsync(db, max: DemoContent.Authors.Count - 1));

            await using var context = _pg.CreateContext(db);
            Assert.Equal(0, await context.Users.CountAsync());
        }

        // G14, G15 — starter content widoczny w publicznym feedzie; ukryty przez moderację znika jak każdy inny post.
        [DockerFact]
        public async Task SeededContent_IsPublic_AndSubjectToModerationFilters()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_feed_{Guid.NewGuid():N}");
            await SeedAsync(db);

            await using var context = _pg.CreateContext(db);
            var feed = await PostgresFixture.CreatePostService(context).GetFeedAsync(new PostFeedQuery { Limit = 50 }, null);
            Assert.Equal(DemoContent.Posts.Count, feed.Value!.Items.Count);

            var target = feed.Value.Items.First(p => p.Title != DemoContent.WelcomeTitle);
            var admin = new User { Username = "admin", NormalizedUsername = "ADMIN", Email = "admin@example.com", NormalizedEmail = "ADMIN@EXAMPLE.COM", PasswordHash = "x", Role = UserRole.Admin };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            Assert.True((await new ModerationService(context, TimeProvider.System).HidePostAsync(admin.Id, target.Id, "test")).Succeeded);

            await using var after = _pg.CreateContext(db);
            var visible = await PostgresFixture.CreatePostService(after).GetFeedAsync(new PostFeedQuery { Limit = 50 }, null);
            Assert.DoesNotContain(visible.Value!.Items, p => p.Id == target.Id);
            Assert.Null(await PostgresFixture.CreatePostService(after).GetByIdAsync(target.Id, null));
        }

        // Post powitalny: bez jawnego FeaturedPostId wskazuje go seeder; jawna konfiguracja ma pierwszeństwo.
        [DockerFact]
        public async Task FeaturedPost_FallsBackToSeededWelcomePost()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"seed_welcome_{Guid.NewGuid():N}");
            await SeedAsync(db);

            await using var context = _pg.CreateContext(db);
            var welcomeId = await context.Posts.Where(p => p.Title == DemoContent.WelcomeTitle).Select(p => p.Id).SingleAsync();

            var fromSeed = new FeaturedPostLocator(context, Options.Create(new PublicDemoOptions { SeedContent = true }));
            var configured = new FeaturedPostLocator(context, Options.Create(new PublicDemoOptions { SeedContent = true, FeaturedPostId = 12345 }));
            var disabled = new FeaturedPostLocator(context, Options.Create(new PublicDemoOptions()));

            Assert.Equal(welcomeId, await fromSeed.GetFeaturedPostIdAsync());
            Assert.Equal(12345, await configured.GetFeaturedPostIdAsync());
            Assert.Null(await disabled.GetFeaturedPostIdAsync());
        }
    }
}
