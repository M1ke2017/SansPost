using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Hubs;
using SansPost.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace SansPost.Tests.Duels
{
    // Sprint 20 — DuelHub przez prawdziwy SignalR (WebSocket w TestServer, JWT): obecność, wyzwanie i jego doręczenie,
    // gotowość, ruchy na żywo z ukrytą kartą, druga karta przeglądarki, rozłączenie z oknem powrotu i wznowienie stanu,
    // tożsamość wyłącznie z połączenia, polityka konta z serwisu. Czasy zmierzone w teście pomiarowym.
    [Trait("Category", "Duels")]
    public sealed class DuelHubTests : IClassFixture<DuelHubTests.HubFactory>
    {
        // Baza współdzielona przez równoległe połączenia (każdy kontekst — własne połączenie SQLite), krótkie okno powrotu.
        public sealed class HubFactory : SansPostFactory
        {
            private readonly string _database = $"DataSource=hub-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            private SqliteConnection? _keepAlive;

            protected override IDictionary<string, string?> Settings
            {
                get
                {
                    var settings = base.Settings;
                    settings["Duels:GraceSeconds"] = "2";
                    return settings;
                }
            }

            protected override void ConfigureDatabase(DbContextOptionsBuilder options)
            {
                if (_keepAlive is null)
                {
                    _keepAlive = new SqliteConnection(_database);
                    _keepAlive.Open();
                    using var schema = new ApplicationDbContextFactoryShim(_database).Create();
                    schema.Database.EnsureCreated();
                }
                options.UseSqlite(_database);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                _keepAlive?.Dispose();
            }
        }

        private sealed record ApplicationDbContextFactoryShim(string ConnectionString)
        {
            public SansPost.Infrastructure.Persistence.ApplicationDbContext Create() =>
                new(new DbContextOptionsBuilder<SansPost.Infrastructure.Persistence.ApplicationDbContext>().UseSqlite(ConnectionString).Options);
        }

        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        private readonly HubFactory _factory;
        private readonly ITestOutputHelper _output;

        public DuelHubTests(HubFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _output = output;
        }

        // Klient stołu gry: połączenie + zapis wszystkich zdarzeń (JSON) do sprawdzenia i oczekiwania.
        private sealed class Player : IAsyncDisposable
        {
            private readonly ConcurrentQueue<(string Name, JsonElement Payload)> _events = new();
            private readonly SemaphoreSlim _signal = new(0);

            public Player(string alias, string token, HubConnection connection)
            {
                Alias = alias;
                Token = token;
                Connection = connection;
                foreach (var name in new[] { DuelEvents.ChallengeReceived, DuelEvents.ChallengeUpdated, DuelEvents.DuelUpdated, DuelEvents.RoundStarted, DuelEvents.RoundResolved })
                    connection.On<JsonElement>(name, payload => { _events.Enqueue((name, payload)); _signal.Release(); });
                connection.On(DuelEvents.PresenceChanged, () => { _events.Enqueue((DuelEvents.PresenceChanged, default)); _signal.Release(); });
            }

            public string Alias { get; }
            public string Token { get; }
            public HubConnection Connection { get; }

            public IEnumerable<(string Name, JsonElement Payload)> Events => _events.ToArray();

            public string Wire => string.Join("\n", _events.Select(e => e.Name + " " + (e.Payload.ValueKind == JsonValueKind.Undefined ? "" : e.Payload.GetRawText())));

            public void Clear() => _events.Clear();

            public async Task<JsonElement> NextAsync(string name, Func<JsonElement, bool>? match = null)
            {
                var deadline = DateTime.UtcNow + Wait;
                while (DateTime.UtcNow < deadline)
                {
                    var found = _events.FirstOrDefault(e => e.Name == name && (match is null || match(e.Payload)));
                    if (found.Name is not null)
                        return found.Payload;
                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(100));
                }
                throw new TimeoutException($"{Alias}: brak zdarzenia {name}. Otrzymane:\n{Wire}");
            }

            public async Task<JsonElement> InvokeAsync(string method, params object?[] args)
            {
                var result = await Connection.InvokeCoreAsync<JsonElement>(method, args);
                return result;
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private HubConnection Connect(string? token) =>
            new HubConnectionBuilder()
                .WithUrl(new Uri(_factory.Server.BaseAddress, DuelHub.Path.TrimStart('/')), options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        var client = _factory.Server.CreateWebSocketClient();
                        if (token is not null)
                            client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                        return await client.ConnectAsync(context.Uri, cancellationToken);
                    };
                })
                .Build();

        private async Task<Player> PlayerAsync(string? existingAlias = null, string? token = null)
        {
            var alias = existingAlias ?? TestUsers.UniqueName("hub");
            if (token is null)
            {
                await _factory.CreateUserAsync(alias);
                token = (await _factory.LoginApiAsync(_factory.CreateHttpsClient(), alias)).AccessToken;
            }
            var player = new Player(alias, token, Connect(token));
            await player.Connection.StartAsync();
            return player;
        }

        private static string Handle(JsonElement players, string alias) =>
            players.EnumerateArray().Single(p => p.GetProperty("alias").GetString() == alias).GetProperty("handle").GetString()!;

        private static void AssertOk(JsonElement result) =>
            Assert.True(result.GetProperty("succeeded").GetBoolean(), result.GetRawText());

        private static string? Code(JsonElement result) => result.GetProperty("code").GetString();

        private static string DuelId(JsonElement result) => result.GetProperty("value").GetProperty("duelId").GetString()!;

        // A wyzywa B, B przyjmuje, obaj gotowi — pojedynek w rundzie 1.
        private async Task<string> DuelAsync(Player a, Player b)
        {
            var challenge = await a.InvokeAsync("ChallengeUser", Handle(await a.InvokeAsync("GetPlayers"), b.Alias));
            AssertOk(challenge);
            var received = await b.NextAsync(DuelEvents.ChallengeReceived);
            var accepted = await b.InvokeAsync("AcceptChallenge", received.GetProperty("challengeId").GetString());
            AssertOk(accepted);
            var duelId = DuelId(accepted);
            AssertOk(await a.InvokeAsync("SetReady", duelId));
            AssertOk(await b.InvokeAsync("SetReady", duelId));
            await a.NextAsync(DuelEvents.RoundStarted, e => e.GetProperty("duelId").GetString() == duelId);
            await b.NextAsync(DuelEvents.RoundStarted, e => e.GetProperty("duelId").GetString() == duelId);
            return duelId;
        }

        [Fact]
        public async Task Anonymous_CannotConnect()
        {
            await using var anonymous = Connect(null);
            await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
            await using var forged = Connect("not-a-jwt");
            await Assert.ThrowsAnyAsync<Exception>(() => forged.StartAsync());
        }

        [Fact]
        public async Task FullRealTimeFlow_Presence_Challenge_Ready_HiddenMove_RoundResolved()
        {
            await using var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();

            var annaSees = await anna.InvokeAsync("GetPlayers");
            Assert.Contains(annaSees.EnumerateArray(), p => p.GetProperty("alias").GetString() == bart.Alias);
            Assert.DoesNotContain(annaSees.EnumerateArray(), p => p.GetProperty("alias").GetString() == anna.Alias);   // nie sam siebie
            Assert.DoesNotContain("@", annaSees.GetRawText());

            var self = await anna.InvokeAsync("ChallengeUser", Handle(await bart.InvokeAsync("GetPlayers"), anna.Alias));
            Assert.Equal("challenge-self", Code(self));

            var duelId = await DuelAsync(anna, bart);
            anna.Clear();
            bart.Clear();

            AssertOk(await anna.InvokeAsync("SubmitMove", duelId, 1, "shoot"));
            var bartUpdate = await bart.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("opponentReady").GetBoolean());
            Assert.Equal(JsonValueKind.Null, bartUpdate.GetProperty("yourMove").ValueKind);
            // Na kablu do Barta nie ma karty Anny (poza listą jego własnych kart do wyboru).
            Assert.DoesNotContain("shoot", StripActions(bart.Wire));

            var second = await bart.InvokeAsync("SubmitMove", duelId, 1, "dodge");
            AssertOk(second);
            var annaResult = await anna.NextAsync(DuelEvents.RoundResolved);
            Assert.Equal("shoot", annaResult.GetProperty("result").GetProperty("yourCard").GetString());
            Assert.Equal("dodge", annaResult.GetProperty("result").GetProperty("opponentCard").GetString());
            var bartResult = await bart.NextAsync(DuelEvents.RoundResolved);
            Assert.Equal("shoot", bartResult.GetProperty("result").GetProperty("opponentCard").GetString());
            var next = await bart.NextAsync(DuelEvents.RoundStarted, e => e.GetProperty("round").GetInt32() == 2);
            Assert.InRange(next.GetProperty("remainingMs").GetInt32(), 1, 15_000);

            // Stary pakiet (runda 1) i podwójne kliknięcie nic nie zmieniają.
            Assert.Equal("duel-stale-round", Code(await anna.InvokeAsync("SubmitMove", duelId, 1, "reload")));
            AssertOk(await anna.InvokeAsync("SubmitMove", duelId, 2, "reload"));
            Assert.Equal("duel-move-already-submitted", Code(await anna.InvokeAsync("SubmitMove", duelId, 2, "reload")));

            // Ruch w cudzym pojedynku — jak nieistniejący; nie ma jak podać UserId (tylko z połączenia).
            await using var cole = await PlayerAsync();
            Assert.Equal("duel-not-found", Code(await cole.InvokeAsync("SubmitMove", duelId, 2, "dodge")));
            Assert.Equal("duel-not-found", Code(await cole.InvokeAsync("Surrender", duelId)));

            AssertOk(await anna.InvokeAsync("Surrender", duelId));
            var over = await bart.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("status").GetString() == "finished");
            Assert.Equal(("win", "surrender"), (over.GetProperty("result").GetString(), over.GetProperty("ending").GetString()));
        }

        private static string StripActions(string wire) =>
            System.Text.RegularExpressions.Regex.Replace(wire, "\"availableActions\":\\[.*?\\]", "");

        [Fact]
        public async Task RejectAndCancel_NotifyBothSides_NoDuel()
        {
            await using var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();

            AssertOk(await anna.InvokeAsync("ChallengeUser", Handle(await anna.InvokeAsync("GetPlayers"), bart.Alias)));
            var received = await bart.NextAsync(DuelEvents.ChallengeReceived);
            Assert.Equal(anna.Alias, received.GetProperty("challengerAlias").GetString());
            Assert.True(received.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

            AssertOk(await bart.InvokeAsync("RejectChallenge", received.GetProperty("challengeId").GetString()));
            await anna.NextAsync(DuelEvents.ChallengeUpdated, e => e.GetProperty("status").GetString() == "rejected");
            Assert.Equal(JsonValueKind.Null, (await bart.InvokeAsync("RequestState")).GetProperty("duel").ValueKind);

            await using var cole = await PlayerAsync();
            AssertOk(await cole.InvokeAsync("ChallengeUser", Handle(await cole.InvokeAsync("GetPlayers"), bart.Alias)));
            await bart.NextAsync(DuelEvents.ChallengeReceived, e => e.GetProperty("challengerAlias").GetString() == cole.Alias);
            AssertOk(await cole.InvokeAsync("CancelChallenge"));
            await bart.NextAsync(DuelEvents.ChallengeUpdated, e => e.GetProperty("status").GetString() == "cancelled");
            Assert.Empty((await bart.InvokeAsync("RequestState")).GetProperty("challenges").EnumerateArray());
        }

        [Fact]
        public async Task SuspendedChallenger_GetsSafeCode_TargetGetsNothing()
        {
            await using var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();
            var handle = Handle(await anna.InvokeAsync("GetPlayers"), bart.Alias);
            SetStatus(anna.Alias, AccountStatus.Suspended);

            var denied = await anna.InvokeAsync("ChallengeUser", handle);

            Assert.Equal("account-suspended", Code(denied));
            Assert.False(denied.GetProperty("succeeded").GetBoolean());
            Assert.Equal(JsonValueKind.Null, denied.GetProperty("value").ValueKind);
            Assert.DoesNotContain(bart.Events, e => e.Name == DuelEvents.ChallengeReceived);
            Assert.DoesNotContain((await bart.InvokeAsync("GetPlayers")).EnumerateArray(), p => p.GetProperty("alias").GetString() == anna.Alias);
            SetStatus(anna.Alias, AccountStatus.Active);
        }

        [Fact]
        public async Task SecondTab_GetsEvents_ClosingOneTabKeepsPlayerOnline_ClosingAll_GraceThenResume()
        {
            var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();
            var duelId = await DuelAsync(anna, bart);
            await using var annaTab2 = await PlayerAsync(anna.Alias, anna.Token);
            AssertOk(await anna.InvokeAsync("SubmitMove", duelId, 1, "block"));

            // Druga karta dostaje ten sam stan (także własną wybraną kartę).
            var tab2 = await annaTab2.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("yourMove").GetString() == "block");
            Assert.Equal(duelId, tab2.GetProperty("duelId").GetString());

            bart.Clear();
            await anna.DisposeAsync();                                     // zamknięta jedna karta
            await Task.Delay(300);
            Assert.DoesNotContain(bart.Events, e => e.Name == DuelEvents.DuelUpdated);   // Anna dalej przy stole

            await annaTab2.Connection.StopAsync();                         // zamknięta ostatnia karta
            var lost = await bart.NextAsync(DuelEvents.DuelUpdated, e => !e.GetProperty("opponent").GetProperty("online").GetBoolean());
            Assert.InRange(lost.GetProperty("opponent").GetProperty("graceRemainingMs").GetInt32(), 1, 2_000);

            await using var back = await PlayerAsync(anna.Alias, anna.Token);   // odświeżenie strony w oknie powrotu
            var state = await back.InvokeAsync("RequestState");
            var duel = state.GetProperty("duel");
            Assert.Equal((duelId, "block", "in-progress"), (duel.GetProperty("duelId").GetString(), duel.GetProperty("yourMove").GetString(), duel.GetProperty("status").GetString()));
            var resumed = await bart.NextAsync(DuelEvents.RoundStarted, e => e.GetProperty("snapshot").GetProperty("opponent").GetProperty("online").GetBoolean());
            Assert.True(resumed.GetProperty("snapshot").GetProperty("opponentReady").GetBoolean());

            await Task.Delay(TimeSpan.FromSeconds(2.5));                  // po oknie powrotu — dalej trwa
            Assert.Equal("in-progress", (await bart.InvokeAsync("RequestState")).GetProperty("duel").GetProperty("status").GetString());
            AssertOk(await back.InvokeAsync("Surrender", duelId));
        }

        [Fact]
        public async Task Disconnect_WithoutReturn_ForfeitsAfterGrace()
        {
            var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();
            var duelId = await DuelAsync(anna, bart);

            await anna.DisposeAsync();

            var over = await bart.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("status").GetString() == "finished");
            Assert.Equal(("win", "disconnect"), (over.GetProperty("result").GetString(), over.GetProperty("ending").GetString()));
            Assert.Equal(duelId, over.GetProperty("duelId").GetString());
        }

        [Fact]
        public async Task Rematch_NeedsBoth_ThroughHub()
        {
            await using var anna = await PlayerAsync();
            await using var bart = await PlayerAsync();
            var duelId = await DuelAsync(anna, bart);
            AssertOk(await bart.InvokeAsync("Surrender", duelId));

            var asked = await anna.InvokeAsync("RequestRematch", duelId);
            Assert.Equal("you", asked.GetProperty("value").GetProperty("rematch").GetString());
            await bart.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("rematch").GetString() == "opponent");

            var rematch = await bart.InvokeAsync("RequestRematch", duelId);
            AssertOk(rematch);
            var newId = DuelId(rematch);
            Assert.NotEqual(duelId, newId);
            await anna.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("duelId").GetString() == newId && e.GetProperty("status").GetString() == "ready-check");
            AssertOk(await anna.InvokeAsync("Surrender", newId));
        }

        private void SetStatus(string alias, AccountStatus status)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SansPost.Infrastructure.Persistence.ApplicationDbContext>();
            db.Users.Single(u => u.Username == alias).Status = status;
            db.SaveChanges();
        }

        // Pomiar (nie próg): 30 pełnych cykli pojedynku tej samej pary (nowe połączenia w każdym cyklu): połączenie,
        // doręczenie wyzwania, "przeciwnik gotowy", rozstrzygnięcie, ponowne połączenie; potem pamięć i sprzątanie.
        [Fact]
        public async Task Measurements_ThirtyDuelCycles_LatencyMemoryCleanup()
        {
            var connect = new List<double>();
            var delivery = new List<double>();
            var ready = new List<double>();
            var resolved = new List<double>();
            var reconnect = new List<double>();
            var sessions = _factory.Services.GetRequiredService<GameSessionService>();
            var registry = _factory.Services.GetRequiredService<DuelConnectionRegistry>();
            var challenges = _factory.Services.GetRequiredService<DuelChallengeService>();

            string annaAlias, bartAlias, annaToken, bartToken;
            await using (var anna = await PlayerAsync())
            await using (var bart = await PlayerAsync())
                (annaAlias, bartAlias, annaToken, bartToken) = (anna.Alias, bart.Alias, anna.Token, bart.Token);
            await Task.Delay(200);
            var sessionsBefore = sessions.SessionCount;
            var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

            for (var cycle = 0; cycle < 30; cycle++)
            {
                var watch = Stopwatch.StartNew();
                await using var anna = await PlayerAsync(annaAlias, annaToken);
                connect.Add(watch.Elapsed.TotalMilliseconds);
                await using var bart = await PlayerAsync(bartAlias, bartToken);

                var handle = Handle(await anna.InvokeAsync("GetPlayers"), bart.Alias);
                watch.Restart();
                AssertOk(await anna.InvokeAsync("ChallengeUser", handle));
                var received = await bart.NextAsync(DuelEvents.ChallengeReceived);
                delivery.Add(watch.Elapsed.TotalMilliseconds);

                var duelId = DuelId(await bart.InvokeAsync("AcceptChallenge", received.GetProperty("challengeId").GetString()));
                AssertOk(await anna.InvokeAsync("SetReady", duelId));
                AssertOk(await bart.InvokeAsync("SetReady", duelId));
                await bart.NextAsync(DuelEvents.RoundStarted);
                bart.Clear();
                anna.Clear();

                watch.Restart();
                AssertOk(await anna.InvokeAsync("SubmitMove", duelId, 1, "reload"));
                await bart.NextAsync(DuelEvents.DuelUpdated, e => e.GetProperty("opponentReady").GetBoolean());
                ready.Add(watch.Elapsed.TotalMilliseconds);

                watch.Restart();
                AssertOk(await bart.InvokeAsync("SubmitMove", duelId, 1, "reload"));
                await anna.NextAsync(DuelEvents.RoundResolved);
                resolved.Add(watch.Elapsed.TotalMilliseconds);

                await anna.Connection.StopAsync();
                watch.Restart();
                await using var back = await PlayerAsync(annaAlias, annaToken);
                var state = await back.InvokeAsync("RequestState");
                reconnect.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(duelId, state.GetProperty("duel").GetProperty("duelId").GetString());

                AssertOk(await back.InvokeAsync("Surrender", duelId));
                AssertOk(await back.InvokeAsync("LeaveDuel", duelId));
                AssertOk(await bart.InvokeAsync("LeaveDuel", duelId));
            }

            await Task.Delay(300);
            var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
            static string Median(List<double> values) => $"{values.OrderBy(v => v).ElementAt(values.Count / 2):F1} ms";
            static string P95(List<double> values) => $"{values.OrderBy(v => v).ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1):F1} ms";
            _output.WriteLine($"30 cykli — mediana / p95: connect {Median(connect)} / {P95(connect)} | challenge→delivery {Median(delivery)} / {P95(delivery)} | " +
                $"move→opponent ready {Median(ready)} / {P95(ready)} | second move→RoundResolved {Median(resolved)} / {P95(resolved)} | " +
                $"reconnect+RequestState {Median(reconnect)} / {P95(reconnect)}");
            _output.WriteLine($"pamięć zarządzana: przed {memoryBefore / 1024.0 / 1024:F1} MB, po 30 cyklach {memoryAfter / 1024.0 / 1024:F1} MB (Δ {(memoryAfter - memoryBefore) / 1024.0:F0} KB)");
            _output.WriteLine($"po cyklach: połączenia {registry.ConnectionCount}, gracze online {registry.UserCount}, wyzwania {challenges.PendingCount}, " +
                $"sesje z timerami {sessions.TimerCount}, sesje {sessions.SessionCount - sessionsBefore} (zakończone — do sprzątnięcia po FinishedRetention)");

            Assert.Equal(0, sessions.TimerCount);
            Assert.Equal(0, challenges.PendingCount);
            Assert.Equal(0, registry.ConnectionCount);
        }
    }
}
