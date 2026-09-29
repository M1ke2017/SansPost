using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace SansPost.Tests.Postgres
{
    // Sprint 21 na prawdziwym PostgreSQL: migracja z ograniczeniami, równoległy zapis tego samego pojedynku (UNIQUE),
    // oraz pomiary i plany (EXPLAIN ANALYZE) zapisu wyniku, statystyk gracza i rankingu na syntetycznych 20 000 wyników.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "Postgres")]
    public sealed class DuelPrestigePostgresTests
    {
        private readonly PostgresFixture _pg;
        private readonly ITestOutputHelper _output;

        public DuelPrestigePostgresTests(PostgresFixture pg, ITestOutputHelper output)
        {
            _pg = pg;
            _output = output;
        }

        private static DuelResultRecord Result(Guid duelId, int one, int two, int? winner, DateTime? at = null) => new()
        {
            DuelId = duelId,
            PlayerOneId = one,
            PlayerTwoId = two,
            WinnerId = winner,
            ResultType = winner is null ? DuelResultType.Draw : DuelResultType.Win,
            RoundCount = 5,
            FinishedAt = at ?? DateTime.UtcNow,
            FinishReason = DuelFinishReason.Knockout
        };

        private static async Task<List<int>> UsersAsync(ApplicationDbContext context, int count)
        {
            var users = Enumerable.Range(0, count).Select(_ =>
            {
                var name = TestUsers.RawName("dp");
                return new User
                {
                    Username = name, NormalizedUsername = IdentityNormalizer.Normalize(name),
                    Email = $"{name}@example.com", NormalizedEmail = IdentityNormalizer.Normalize($"{name}@example.com"),
                    PasswordHash = "test-data-no-login", CreatedAt = DateTime.UtcNow
                };
            }).ToList();
            context.Users.AddRange(users);
            await context.SaveChangesAsync();
            return users.Select(u => u.Id).ToList();
        }

        [DockerFact]
        public async Task ConcurrentFinish_SameDuel_ExactlyOneRow_ConstraintsHold()
        {
            var connection = await _pg.CreateMigratedDatabaseAsync($"duels_{Guid.NewGuid():N}"[..30]);
            List<int> ids;
            await using (var setup = _pg.CreateContext(connection))
                ids = await UsersAsync(setup, 3);
            var duelId = Guid.NewGuid();

            using var gate = new Barrier(8);
            var inserted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await using var context = _pg.CreateContext(connection);
                var service = new DuelStandingsService(context, new MemoryCache(new MemoryCacheOptions()));
                gate.SignalAndWait();
                return await service.RecordAsync(Result(duelId, ids[0], ids[1], ids[0]));
            })));

            Assert.Equal(1, inserted.Count(i => i));
            await using var check = _pg.CreateContext(connection);
            Assert.Equal(1, await check.DuelResults.CountAsync(r => r.DuelId == duelId));

            // Baza pilnuje spójności niezależnie od aplikacji: zwycięzca spoza pojedynku, gracz sam ze sobą, remis ze zwycięzcą.
            var drawWithWinner = Result(Guid.NewGuid(), ids[0], ids[1], ids[0]);
            drawWithWinner.ResultType = DuelResultType.Draw;
            foreach (var invalid in new[] { Result(Guid.NewGuid(), ids[0], ids[1], ids[2]), Result(Guid.NewGuid(), ids[0], ids[0], null), drawWithWinner })
            {
                await using var context = _pg.CreateContext(connection);
                context.DuelResults.Add(invalid);
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            }
        }

        [DockerFact]
        public async Task Synthetic20k_PersistStatsTop10Champion_Measured_WithPlans()
        {
            var connection = await _pg.CreateMigratedDatabaseAsync($"duels_{Guid.NewGuid():N}"[..30]);
            List<int> ids;
            await using (var setup = _pg.CreateContext(connection))
            {
                ids = await UsersAsync(setup, 400);
                var rnd = new Random(21);
                var start = DateTime.UtcNow.AddDays(-60);
                var rows = Enumerable.Range(0, 20_000).Select(i =>
                {
                    var one = ids[rnd.Next(ids.Count)];
                    var two = ids[(ids.IndexOf(one) + 1 + rnd.Next(ids.Count - 1)) % ids.Count];
                    var roll = rnd.Next(10);
                    return Result(Guid.NewGuid(), one, two, roll == 0 ? null : roll % 2 == 0 ? one : two, start.AddMinutes(i * 4));
                }).ToList();
                setup.ChangeTracker.AutoDetectChangesEnabled = false;
                foreach (var chunk in rows.Chunk(2000))
                {
                    setup.DuelResults.AddRange(chunk);
                    await setup.SaveChangesAsync();
                    setup.ChangeTracker.Clear();
                }
            }
            await using (var analyze = new NpgsqlConnection(connection))
            {
                await analyze.OpenAsync();
                await using var command = new NpgsqlCommand("ANALYZE duelresults; ANALYZE users;", analyze);
                await command.ExecuteNonQueryAsync();
            }

            static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);
            async Task<(double Ms, T Value)> TimeAsync<T>(Func<DuelStandingsService, Task<T>> action, CommandCapture? capture = null)
            {
                await using var context = capture is null ? _pg.CreateContext(connection) : _pg.CreateContext(connection, capture);
                var service = new DuelStandingsService(context, new MemoryCache(new MemoryCacheOptions()));   // bez pamięci podręcznej
                var watch = Stopwatch.StartNew();
                var value = await action(service);
                return (watch.Elapsed.TotalMilliseconds, value);
            }

            // Rozgrzewka połączeń i planów.
            await TimeAsync(s => s.GetStandingsAsync());
            await TimeAsync(s => s.GetStatsAsync(ids[0]));

            var persist = new List<double>();
            var stats = new List<double>();
            var top = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                persist.Add((await TimeAsync(s => s.RecordAsync(Result(Guid.NewGuid(), ids[i], ids[i + 1], ids[i])))).Ms);
                stats.Add((await TimeAsync(s => s.GetStatsAsync(ids[i * 7 % ids.Count]))).Ms);
                top.Add((await TimeAsync(s => s.GetStandingsAsync())).Ms);
            }

            // Pamięć podręczna procesu: kolejne wejścia do sali (plakietka mistrza) bez zapytania.
            var cache = new MemoryCache(new MemoryCacheOptions());
            var cached = new List<double>();
            await using (var context = _pg.CreateContext(connection))
            {
                var service = new DuelStandingsService(context, cache);
                await service.GetStandingsAsync();
                for (var i = 0; i < 30; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await service.GetStandingsAsync();
                    cached.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            _output.WriteLine($"20 000 wyników, 400 graczy — mediany: zapis wyniku {Median(persist):F2} ms, statystyki gracza {Median(stats):F2} ms, " +
                $"Top 10 + Mistrz (bez cache) {Median(top):F2} ms, Top 10 z pamięci podręcznej {Median(cached):F4} ms");

            // Plany: statystyki gracza — indeksy graczy/zwycięzcy; ranking — jedno zapytanie (bez N+1).
            var statsCapture = new CommandCapture();
            await TimeAsync(s => s.GetStatsAsync(ids[3]), statsCapture);
            var topCapture = new CommandCapture();
            var (_, standings) = await TimeAsync(s => s.GetStandingsAsync(), topCapture);
            Assert.Single(statsCapture.Commands);
            Assert.Single(topCapture.Commands);
            Assert.Equal(10, standings.Top.Count);
            Assert.NotNull(standings.Champion);

            foreach (var (name, command) in new[] { ("statystyki", statsCapture.Commands[0]), ("Top 10", topCapture.Commands[0]) })
            {
                var plan = await ExplainAnalyzeAsync(connection, command);
                _output.WriteLine($"--- {name}: {command.Sql}\n{string.Join("\n", plan)}");
                if (name == "statystyki")
                    Assert.Contains(plan, line => line.Contains("IX_duelresults_player", StringComparison.Ordinal));
            }
        }

        private static async Task<List<string>> ExplainAnalyzeAsync(string connectionString, (string Sql, NpgsqlParameter[] Parameters) command)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + command.Sql, connection);
            explain.Parameters.AddRange(command.Parameters.Select(p => p.Clone()).ToArray());
            var plan = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
            return plan;
        }
    }
}
