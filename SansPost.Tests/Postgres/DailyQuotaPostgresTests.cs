using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Duels;
using SansPost.Features.Usage;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Dzienne limity (v1.0) na prawdziwym PostgreSQL: równoległe requesty jednego użytkownika (wiele połączeń jak w puli,
    // także jak przy wielu instancjach) nie przekraczają limitu, a pojedynek dwóch graczy zużywa limit obu albo żadnego.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Concurrency")]
    [Trait("Category", "Quota")]
    public sealed class DailyQuotaPostgresTests
    {
        private readonly PostgresFixture _pg;

        public DailyQuotaPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        [DockerFact]
        public async Task CommentRace_19Used_FiveParallel_OneSucceeds()
        {
            var author = await _pg.CreateUserAsync();
            var commenter = await _pg.CreateUserAsync();
            var postId = await _pg.SeedPostAsync(author, "Rozmowa", "Treść");
            await _pg.SeedUsageAsync(commenter, comments: DailyQuota.Comments - 1);

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
            {
                await using var context = _pg.CreateContext();
                return await TestServices.Comments(context).AddAsync(commenter, postId, new CommentRequest { Content = $"Komentarz {i}" });
            })));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(DailyQuota.CommentLimitCode, r.Code));
            Assert.Equal(DailyQuota.Comments, (await _pg.UsageAsync(commenter)).Comments);
        }

        // Komentarz, który się nie zapisze (post nie istnieje), nie zużywa limitu.
        [DockerFact]
        public async Task FailedComment_DoesNotConsumeQuota()
        {
            var commenter = await _pg.CreateUserAsync();
            await using var context = _pg.CreateContext();

            var result = await TestServices.Comments(context).AddAsync(commenter, int.MaxValue, new CommentRequest { Content = "Do nikąd" });

            Assert.Equal(ServiceError.NotFound, result.Error);
            Assert.Equal(0, (await _pg.UsageAsync(commenter)).Comments);
        }

        private GameSessionService Sessions(ServiceProvider services) =>
            new(TimeProvider.System, services.GetRequiredService<IServiceScopeFactory>(), new RecordingNotifier(), Options.Create(new DuelOptions()),
                NullLogger<GameSessionService>.Instance);

        private ServiceProvider Services()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => _pg.CreateContext());
            services.AddScoped(provider => TestServices.Guard(provider.GetRequiredService<ApplicationDbContext>()));
            services.AddMemoryCache();
            services.AddScoped<DuelStandingsService>();
            return services.BuildServiceProvider();
        }

        [DockerFact]
        public async Task TrainingRace_14Used_FiveParallel_OneStarts()
        {
            var player = await _pg.CreateUserAsync();
            await _pg.SeedUsageAsync(player, games: DailyQuota.Games - 1);
            await using var services = Services();
            using var sessions = Sessions(services);

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => sessions.CreateAsync(player, "Gracz", DuelMode.Training))));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(DailyQuota.GameLimitCode, r.Code));
            Assert.Equal(DailyQuota.Games, (await _pg.UsageAsync(player)).Games);
        }

        // A: 14/15, B: 15/15, obaj "GOTOWY" → gra nie startuje; A nadal 14, B nadal 15 (obaj albo żaden).
        [DockerFact]
        public async Task LiveDuel_OnePlayerAtLimit_DoesNotStart_NoQuotaConsumed()
        {
            var a = await _pg.CreateUserAsync();
            var b = await _pg.CreateUserAsync();
            await _pg.SeedUsageAsync(a, games: DailyQuota.Games - 1);
            await _pg.SeedUsageAsync(b, games: DailyQuota.Games);
            await using var services = Services();
            using var sessions = Sessions(services);

            var duel = await sessions.StartLiveAsync(a, "A", b, "B");
            Assert.True(duel.Succeeded, duel.Message);
            var id = Guid.Parse(duel.Value!.DuelId);
            Assert.True((await sessions.SetReadyAsync(id, a)).Succeeded);
            var second = await sessions.SetReadyAsync(id, b);

            Assert.Equal(DailyQuota.GameLimitCode, second.Code);                     // B — własny limit
            Assert.Equal("ready-check", sessions.Get(id, a).Value!.Status);         // runda 1 nie wystartowała
            Assert.Equal(DailyQuota.Games - 1, (await _pg.UsageAsync(a)).Games);
            Assert.Equal(DailyQuota.Games, (await _pg.UsageAsync(b)).Games);
        }
    }
}
