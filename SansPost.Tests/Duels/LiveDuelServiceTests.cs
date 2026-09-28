using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Duels
{
    // Sprint 20 — pojedynek na żywo po stronie serwisów (bez transportu): wyzwania, gotowość, zegar rundy, kary za brak ruchu,
    // rozłączenia i okno powrotu, oddanie, rewanż, polityka konta. Silnik F# prawdziwy, czas ręczny (ManualTimeProvider),
    // zdarzenia zapisywane (RecordingNotifier) — każdy test deterministyczny, bez czekania.
    [Trait("Category", "Duels")]
    public sealed class LiveDuelServiceTests : IDisposable
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private readonly DuelAccounts _accounts = new();
        private readonly ManualTimeProvider _time = new();
        private readonly RecordingNotifier _events = new();
        private readonly DuelOptions _options = new();
        private readonly GameSessionService _sessions;
        private readonly DuelConnectionRegistry _registry = new();
        private readonly DuelChallengeService _challenges;
        private readonly int Anna, Bart, Cole;

        public LiveDuelServiceTests()
        {
            Anna = _accounts.Add();
            Bart = _accounts.Add();
            Cole = _accounts.Add();
            _sessions = _accounts.Sessions(_time, _events, _options);
            _challenges = new DuelChallengeService(_time, _sessions, _registry, _accounts.Scopes, _events, Options.Create(_options),
                NullLogger<DuelChallengeService>.Instance);
        }

        public void Dispose()
        {
            _challenges.Dispose();
            _sessions.Dispose();
            _accounts.Dispose();
        }

        private static T Ok<T>(ServiceResult<T> result)
        {
            Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
            return result.Value!;
        }

        private string Online(int userId, string alias)
        {
            var connection = Guid.NewGuid().ToString("N");
            _registry.Add(userId, alias, connection);
            return connection;
        }

        private string HandleOf(int userId) => _registry.Online().Single(p => p.UserId == userId).Handle;

        private async Task<Guid> LiveAsync(bool ready = true)
        {
            var duel = Ok(await _sessions.StartLiveAsync(Anna, "Anna", Bart, "Bart"));
            var id = Guid.Parse(duel.DuelId);
            if (ready)
            {
                Ok(await _sessions.SetReadyAsync(id, Anna));
                Ok(await _sessions.SetReadyAsync(id, Bart));
            }
            return id;
        }

        private async Task PlayAsync(Guid id, string annaCard, string bartCard)
        {
            var round = Ok(_sessions.Get(id, Anna)).Round;
            Ok(await _sessions.SubmitMoveAsync(id, Anna, annaCard, round));
            Ok(await _sessions.SubmitMoveAsync(id, Bart, bartCard, round));
        }

        private DuelSnapshot View(Guid id, int userId) => Ok(_sessions.Get(id, userId));

        // ---- Obecność i wyzwania --------------------------------------------------------------------------------------

        [Fact]
        public async Task OnlinePlayers_OnlyActiveOthersNotInDuel_NoPrivateData()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");
            Online(Cole, "Cole");
            _accounts.SetStatus(Cole, AccountStatus.Suspended);

            var seen = await _challenges.AvailablePlayersAsync(Anna);

            var bart = Assert.Single(seen);
            Assert.Equal("Bart", bart.Alias);
            Assert.Matches("^[0-9a-f]{16}$", bart.Handle);   // losowy uchwyt, nie UserId
            var json = JsonSerializer.Serialize(seen, Web);
            Assert.DoesNotContain("@", json);
            Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);

            _accounts.SetStatus(Cole, AccountStatus.Active);
            await LiveAsync();
            Assert.Equal(new[] { "Cole" }, (await _challenges.AvailablePlayersAsync(Anna)).Select(p => p.Alias));   // Bart w pojedynku
            Assert.Empty(await _challenges.AvailablePlayersAsync(Cole));                                           // obaj zajęci
        }

        [Fact]
        public async Task CannotChallengeSelf_OrOfflinePlayer()
        {
            Online(Anna, "Anna");

            Assert.Equal("challenge-self", (await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Anna))).Code);
            Assert.Equal("player-unavailable", (await _challenges.ChallengeAsync(Anna, "Anna", "0000000000000000")).Code);
            Assert.Equal("player-unavailable", (await _challenges.ChallengeAsync(Anna, "Anna", null)).Code);
            Assert.Equal(0, _challenges.PendingCount);
        }

        [Fact]
        public async Task Challenge_DeliveredToTarget_Minimal_NoGameStateUntilAccepted()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");

            var sent = Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));

            Assert.Equal(("outgoing", "Bart", "pending"), (sent.Direction, sent.OpponentAlias, sent.Status));
            var received = Assert.Single(_events.Payloads<ChallengeReceived>(Bart, DuelEvents.ChallengeReceived));
            Assert.Equal((sent.ChallengeId, "Anna"), (received.ChallengeId, received.ChallengerAlias));
            Assert.Equal(_time.GetUtcNow() + _options.ChallengeLifetime, received.ExpiresAt);
            using (var wire = JsonDocument.Parse(JsonSerializer.Serialize(received, Web)))
                Assert.Equal(new[] { "challengeId", "challengerAlias", "expiresAt", "remainingMs" },
                    wire.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(0, _sessions.SessionCount);

            // Jedno wysłane wyzwanie naraz — kolejne (nawet do innego gracza) czeka.
            Online(Cole, "Cole");
            Assert.Equal("challenge-already-pending", (await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Cole))).Code);
            Assert.Equal("challenge-crossed", (await _challenges.ChallengeAsync(Bart, "Bart", HandleOf(Anna))).Code);
        }

        [Fact]
        public async Task Reject_EndsChallenge_NoDuel_CooldownStopsSpam()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");
            var sent = Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));

            Ok(await _challenges.RejectAsync(Bart, sent.ChallengeId));

            Assert.Equal("rejected", _events.Payloads<ChallengeInfo>(Anna, DuelEvents.ChallengeUpdated).Last().Status);
            Assert.Equal(0, _sessions.SessionCount);
            Assert.Equal("challenge-not-found", (await _challenges.AcceptAsync(Bart, sent.ChallengeId)).Code);
            Assert.Equal("challenge-cooldown", (await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart))).Code);
            Assert.Equal(0, _time.ActiveTimers);

            _time.Advance(DuelChallengeService.Cooldown);
            Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));
        }

        [Fact]
        public async Task Challenge_Expires_BothSidesUpdated_TimerGone()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");
            var sent = Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));

            _time.Advance(_options.ChallengeLifetime - TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, _challenges.PendingCount);
            _time.Advance(TimeSpan.FromMilliseconds(1));

            Assert.Equal(0, _challenges.PendingCount);
            Assert.Equal("expired", _events.Payloads<ChallengeInfo>(Anna, DuelEvents.ChallengeUpdated).Last().Status);
            Assert.Equal("expired", _events.Payloads<ChallengeInfo>(Bart, DuelEvents.ChallengeUpdated).Last().Status);
            Assert.Equal("challenge-not-found", (await _challenges.AcceptAsync(Bart, sent.ChallengeId)).Code);
            Assert.Equal(0, _time.ActiveTimers);
        }

        [Fact]
        public async Task Accept_CreatesLiveDuel_InReadyCheck_BothNotified_OtherChallengesEnd()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");
            Online(Cole, "Cole");
            var sent = Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));
            var other = Ok(await _challenges.ChallengeAsync(Cole, "Cole", HandleOf(Bart)));

            Assert.Equal("challenge-not-found", (await _challenges.AcceptAsync(Cole, sent.ChallengeId)).Code);   // nie do niego
            var duel = Ok(await _challenges.AcceptAsync(Bart, sent.ChallengeId));

            Assert.Equal(("live", "ready-check", "Anna"), (duel.Mode, duel.Status, duel.Opponent!.Alias));
            Assert.Equal("ready-check", _events.Payloads<DuelSnapshot>(Anna, DuelEvents.DuelUpdated).Last().Status);
            Assert.Equal("accepted", _events.Payloads<ChallengeInfo>(Anna, DuelEvents.ChallengeUpdated).Last().Status);
            Assert.Equal("unavailable", _events.Payloads<ChallengeInfo>(Cole, DuelEvents.ChallengeUpdated).Last().Status);
            Assert.Equal("challenge-not-found", (await _challenges.AcceptAsync(Bart, other.ChallengeId)).Code);
            Assert.Equal(0, _challenges.PendingCount);
            Assert.True(_sessions.IsBusy(Anna) && _sessions.IsBusy(Bart));
            Assert.Equal(0, _time.ActiveTimers);   // bez zegara rundy przed gotowością
        }

        [Fact]
        public async Task SuspendedChallenger_Rejected_SuspendedTarget_CannotAccept_ChallengeUntouched()
        {
            Online(Anna, "Anna");
            Online(Bart, "Bart");
            _accounts.SetStatus(Anna, AccountStatus.Suspended);

            Assert.Equal("account-suspended", (await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart))).Code);
            Assert.Empty(_events.Events);

            _accounts.SetStatus(Anna, AccountStatus.Active);
            var sent = Ok(await _challenges.ChallengeAsync(Anna, "Anna", HandleOf(Bart)));
            _accounts.SetStatus(Bart, AccountStatus.Suspended);

            Assert.Equal("account-suspended", (await _challenges.AcceptAsync(Bart, sent.ChallengeId)).Code);
            Assert.Equal(1, _challenges.PendingCount);
            Assert.Equal(0, _sessions.SessionCount);
            Ok(await _challenges.RejectAsync(Bart, sent.ChallengeId));   // odrzucić może zawsze
        }

        // ---- Gotowość --------------------------------------------------------------------------------------------------

        [Fact]
        public async Task OnlyOneReady_GameDoesNotStart_BothReady_Round1WithServerDeadline()
        {
            var id = await LiveAsync(ready: false);
            var ready = Ok(await _sessions.SetReadyAsync(id, Anna));
            Ok(await _sessions.SetReadyAsync(id, Anna));   // powtórzone kliknięcie

            Assert.Equal("ready-check", ready.Status);
            Assert.True(View(id, Bart).Opponent!.StartReady);
            Assert.Equal("duel-not-started", (await _sessions.SubmitMoveAsync(id, Anna, "dodge", 1)).Code);
            Assert.Equal(0, _time.ActiveTimers);
            Assert.All(View(id, Anna).AvailableActions, a => Assert.False(a.Enabled));

            Ok(await _sessions.SetReadyAsync(id, Bart));

            var started = Assert.Single(_events.Payloads<RoundStartedEvent>(Bart, DuelEvents.RoundStarted));
            Assert.Equal((1, _time.GetUtcNow() + _options.RoundDuration, 15_000), (started.Round, started.DeadlineUtc, started.RemainingMs));
            Assert.Equal("in-progress", started.Snapshot.Status);
            Assert.Single(_events.Payloads<RoundStartedEvent>(Anna, DuelEvents.RoundStarted));
            Assert.Equal(1, _time.ActiveTimers);
        }

        // ---- Ruchy: równocześnie, ukryte, dokładnie raz, idempotentnie ------------------------------------------------

        [Fact]
        public async Task SimultaneousMoves_RoundResolvesExactlyOnce_BothGetResult()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var id = await LiveAsync();
                _events.Clear();
                using var gate = new Barrier(2);
                var moves = await Task.WhenAll(
                    Task.Run(() => { gate.SignalAndWait(); return _sessions.SubmitMoveAsync(id, Anna, "shoot", 1); }),
                    Task.Run(() => { gate.SignalAndWait(); return _sessions.SubmitMoveAsync(id, Bart, "dodge", 1); }));

                Assert.All(moves, m => Assert.True(m.Succeeded));
                Assert.Single(moves, m => m.Value!.Resolved);
                Assert.Equal(1, _events.Count(Anna, DuelEvents.RoundResolved));
                Assert.Equal(1, _events.Count(Bart, DuelEvents.RoundResolved));
                var result = _events.Payloads<RoundResolvedEvent>(Bart, DuelEvents.RoundResolved).Single().Result;
                Assert.Equal(("dodge", "shoot"), (result.YourCard, result.OpponentCard));
                Assert.Single(View(id, Anna).History);
                Ok(await _sessions.SurrenderAsync(id, Anna));   // wolne miejsce dla kolejnej próby
            }
        }

        [Fact]
        public async Task HiddenMove_OpponentOnlyLearnsReady_ServerNeverSendsTheCard()
        {
            var id = await LiveAsync();
            _events.Clear();

            Ok(await _sessions.SubmitMoveAsync(id, Anna, "shoot", 1));

            var bartSees = _events.Payloads<DuelSnapshot>(Bart, DuelEvents.DuelUpdated).Single();
            Assert.True(bartSees.OpponentReady);
            Assert.Null(bartSees.YourMove);
            Assert.Empty(bartSees.History);
            // Wszystko, co trafiło do Barta, bez listy jego własnych kart — żadnego śladu karty Anny.
            var wire = JsonSerializer.Serialize(bartSees with { AvailableActions = Array.Empty<AvailableAction>() }, Web);
            Assert.DoesNotContain("shoot", wire);
            Assert.Equal("shoot", View(id, Anna).YourMove);   // Anna (także w drugiej karcie) widzi swój wybór
            Assert.Equal("shoot", _events.Payloads<DuelSnapshot>(Anna, DuelEvents.DuelUpdated).Single().YourMove);
        }

        [Fact]
        public async Task StaleRound_AndDuplicateCall_ChangeNothing()
        {
            var id = await LiveAsync();
            Ok(await _sessions.SubmitMoveAsync(id, Anna, "reload", 1));
            _events.Clear();

            var duplicate = await _sessions.SubmitMoveAsync(id, Anna, "reload", 1);   // podwójne kliknięcie
            Assert.Equal("duel-move-already-submitted", duplicate.Code);
            Assert.Empty(_events.Events);

            Ok(await _sessions.SubmitMoveAsync(id, Bart, "reload", 1));
            var before = View(id, Anna);
            var stale = await _sessions.SubmitMoveAsync(id, Anna, "shoot", 1);        // spóźniony pakiet starej rundy
            Assert.Equal("duel-stale-round", stale.Code);
            var after = View(id, Anna);
            Assert.Equal((2, null, 1), (after.Round, after.YourMove, after.History.Count));
            Assert.Equal(before.Version, after.Version);
        }

        // ---- Zegar rundy i kary -----------------------------------------------------------------------------------------

        [Fact]
        public async Task RoundDeadline_IsServerAuthoritative_OneTimeout_LosesOnePrestige()
        {
            var id = await LiveAsync();
            Ok(await _sessions.SubmitMoveAsync(id, Anna, "shoot", 1));
            _events.Clear();

            _time.Advance(_options.RoundDuration - TimeSpan.FromMilliseconds(1));
            Assert.Empty(_events.Events);
            _time.Advance(TimeSpan.FromMilliseconds(1));

            var resolved = _events.Payloads<RoundResolvedEvent>(Anna, DuelEvents.RoundResolved).Single();
            Assert.Equal(("shoot", (string?)null), (resolved.Result.YourCard, resolved.Result.OpponentCard));
            Assert.Contains("Przeciwnik nie zdążył wybrać karty — traci 1 prestiżu.", resolved.Result.Effects);
            var anna = View(id, Anna);
            Assert.Equal((3, 1, 2, 2), (anna.You.Prestige, anna.You.Ammo, anna.Opponent!.Prestige, anna.Round));   // strzał nie padł
            var next = _events.Payloads<RoundStartedEvent>(Bart, DuelEvents.RoundStarted).Single();
            Assert.Equal((2, _time.GetUtcNow() + _options.RoundDuration), (next.Round, next.DeadlineUtc));

            // Ruch po terminie (klient z opóźnionym zegarem) trafia w starą rundę — odrzucony.
            Assert.Equal("duel-stale-round", (await _sessions.SubmitMoveAsync(id, Bart, "dodge", 1)).Code);
        }

        [Fact]
        public async Task BothTimeout_BothLose_ThreeTimesIsADraw_TimersCleaned()
        {
            var id = await LiveAsync();

            _time.Advance(_options.RoundDuration);
            Assert.Equal((2, 2), (View(id, Anna).You.Prestige, View(id, Anna).Opponent!.Prestige));

            _time.Advance(_options.RoundDuration);
            _time.Advance(_options.RoundDuration);

            var final = View(id, Anna);
            Assert.Equal(("finished", "draw", 3), (final.Status, final.Result, final.History.Count));
            Assert.Equal("draw", _events.Payloads<RoundResolvedEvent>(Bart, DuelEvents.RoundResolved).Last().GameOver);
            Assert.Equal(0, _time.ActiveTimers);
            Assert.Equal(0, _sessions.TimerCount);
            Assert.False(_sessions.IsBusy(Anna));
        }

        [Fact]
        public async Task Knockout_GameOver_WinAndLoss_NoMoreTimers()
        {
            var id = await LiveAsync();
            await PlayAsync(id, "shoot", "taunt");
            await PlayAsync(id, "reload", "taunt");
            await PlayAsync(id, "shoot", "taunt");
            await PlayAsync(id, "reload", "taunt");
            await PlayAsync(id, "shoot", "taunt");

            Assert.Equal(("finished", "win"), (View(id, Anna).Status, View(id, Anna).Result));
            Assert.Equal("loss", _events.Payloads<RoundResolvedEvent>(Bart, DuelEvents.RoundResolved).Last().GameOver);
            Assert.Equal(5, View(id, Bart).History.Count);
            Assert.Equal(0, _time.ActiveTimers);
            Assert.Contains(_events.Events, e => e.Name == DuelEvents.PresenceChanged);
        }

        // ---- Rozłączenia, okno powrotu, oddanie ---------------------------------------------------------------------------

        [Fact]
        public async Task Disconnect_OpponentSeesGrace_RoundClockStops_ReconnectBeforeMove_FullTimeAgain()
        {
            var id = await LiveAsync();
            _time.Advance(TimeSpan.FromSeconds(10));
            _events.Clear();

            await _sessions.PlayerOfflineAsync(Anna);

            var bartSees = _events.Payloads<DuelSnapshot>(Bart, DuelEvents.DuelUpdated).Single();
            Assert.Equal((false, 20_000), (bartSees.Opponent!.Online, bartSees.Opponent.GraceRemainingMs));
            Assert.Null(bartSees.RoundDeadline);
            _time.Advance(TimeSpan.FromSeconds(19));
            Assert.Empty(View(id, Bart).History);   // zegar rundy stoi — brak kary za czas bez połączenia

            await _sessions.PlayerOnlineAsync(Anna);

            var resumed = _events.Payloads<RoundStartedEvent>(Bart, DuelEvents.RoundStarted).Single();
            Assert.Equal((1, 15_000, true), (resumed.Round, resumed.RemainingMs, resumed.Snapshot.Opponent!.Online));
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("in-progress", View(id, Anna).Status);   // okno powrotu anulowane — bez oddania
        }

        [Fact]
        public async Task ReconnectAfterOwnMove_OwnCardShownToSelf_StillHiddenFromOpponent()
        {
            var id = await LiveAsync();
            Ok(await _sessions.SubmitMoveAsync(id, Anna, "block", 1));

            await _sessions.PlayerOfflineAsync(Anna);
            await _sessions.PlayerOnlineAsync(Anna);

            var anna = Ok(_sessions.GetActive(Anna));   // RequestState po powrocie
            Assert.Equal(("block", 1), (anna.YourMove, anna.Round));
            var bart = View(id, Bart);
            Assert.True(bart.OpponentReady);
            Assert.DoesNotContain("block", JsonSerializer.Serialize(bart with { AvailableActions = Array.Empty<AvailableAction>() }, Web));
            Ok(await _sessions.SubmitMoveAsync(id, Bart, "shoot", 1));
            Assert.Equal(("block", "shoot"), (View(id, Anna).History.Single().YourCard, View(id, Anna).History.Single().OpponentCard));
        }

        [Fact]
        public async Task GraceExpires_Forfeit_OpponentWins_EndingDisconnect()
        {
            var id = await LiveAsync();
            Ok(await _sessions.SubmitMoveAsync(id, Anna, "shoot", 1));
            await _sessions.PlayerOfflineAsync(Bart);

            _time.Advance(_options.DisconnectGrace);

            var anna = View(id, Anna);
            Assert.Equal(("finished", "win", "disconnect"), (anna.Status, anna.Result, anna.Ending));
            Assert.Equal("loss", View(id, Bart).Result);
            Assert.Empty(anna.History);   // ukryta karta nie została odsłonięta
            Assert.Equal(0, _time.ActiveTimers);
            Assert.False(_sessions.IsBusy(Anna));
        }

        [Fact]
        public async Task Surrender_OpponentWins_Once_AllowedWhileSuspended()
        {
            var id = await LiveAsync();
            _accounts.SetStatus(Anna, AccountStatus.Suspended);

            var surrendered = Ok(await _sessions.SurrenderAsync(id, Anna));

            Assert.Equal(("loss", "surrender"), (surrendered.Result, surrendered.Ending));
            Assert.Equal("win", _events.Payloads<DuelSnapshot>(Bart, DuelEvents.DuelUpdated).Last().Result);
            Assert.Equal("duel-finished", (await _sessions.SurrenderAsync(id, Bart)).Code);
            Assert.Equal("duel-not-found", (await _sessions.SurrenderAsync(id, Cole)).Code);
            Assert.Equal(0, _time.ActiveTimers);
        }

        [Fact]
        public async Task SuspendedMidDuel_CannotMoveOrRematch_TimeoutsStillApply()
        {
            var id = await LiveAsync();
            _accounts.SetStatus(Bart, AccountStatus.Suspended);

            Assert.Equal("account-suspended", (await _sessions.SubmitMoveAsync(id, Bart, "dodge", 1)).Code);
            Ok(await _sessions.SubmitMoveAsync(id, Anna, "reload", 1));
            _time.Advance(_options.RoundDuration);

            var bart = View(id, Bart);   // odczyt zostaje
            Assert.Equal((2, 2), (bart.You.Prestige, bart.Round));

            Ok(await _sessions.SurrenderAsync(id, Anna));
            Assert.Equal("account-suspended", (await _sessions.RequestRematchAsync(id, Bart)).Code);
        }

        // ---- Rewanż ------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Rematch_NeedsBothPlayers_NewDuel_OldStateUntouched()
        {
            var id = await LiveAsync();
            await PlayAsync(id, "reload", "dodge");
            Ok(await _sessions.SurrenderAsync(id, Bart));
            var old = View(id, Anna);

            var asked = Ok(await _sessions.RequestRematchAsync(id, Anna));
            Assert.Equal("you", asked.Rematch);
            Assert.Equal("opponent", View(id, Bart).Rematch);
            Assert.False(_sessions.IsBusy(Anna));

            var rematch = Ok(await _sessions.RequestRematchAsync(id, Bart));

            Assert.NotEqual(id.ToString("N"), rematch.DuelId);
            Assert.Equal(("live", "ready-check", 1), (rematch.Mode, rematch.Status, rematch.Round));
            Assert.Equal(rematch.DuelId, View(id, Anna).RematchDuelId);
            Assert.Equal(rematch.DuelId, Ok(await _sessions.RequestRematchAsync(id, Anna)).DuelId);   // ten sam, nie trzeci
            Assert.Equal(JsonSerializer.Serialize(old.History, Web), JsonSerializer.Serialize(View(id, Anna).History, Web));
            Assert.Equal("win", View(id, Anna).Result);
            Assert.Equal(rematch.DuelId, Ok(_sessions.GetActive(Anna)).DuelId);
        }

        [Fact]
        public async Task Rematch_SimultaneousRequests_ExactlyOneNewDuel()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var id = await LiveAsync();
                Ok(await _sessions.SurrenderAsync(id, Anna));
                using var gate = new Barrier(2);
                var both = await Task.WhenAll(
                    Task.Run(() => { gate.SignalAndWait(); return _sessions.RequestRematchAsync(id, Anna); }),
                    Task.Run(() => { gate.SignalAndWait(); return _sessions.RequestRematchAsync(id, Bart); }));

                var rematchId = Ok(_sessions.GetActive(Anna)).DuelId;
                Assert.NotEqual(id.ToString("N"), rematchId);
                Assert.Equal(rematchId, View(id, Bart).RematchDuelId);
                Assert.All(both, r => Assert.True(r.Succeeded, r.Code));
                Ok(await _sessions.SurrenderAsync(Guid.Parse(rematchId), Anna));
            }
        }

        [Fact]
        public async Task LeaveAfterGameOver_OpponentSees_RematchRejected_DuelNotResumed()
        {
            var id = await LiveAsync();
            Assert.Equal("duel-in-progress", (await _sessions.LeaveAsync(id, Anna)).Code);
            Ok(await _sessions.SurrenderAsync(id, Anna));

            Ok(await _sessions.LeaveAsync(id, Anna));

            Assert.True(View(id, Bart).OpponentLeft);
            Assert.Equal("rematch-opponent-left", (await _sessions.RequestRematchAsync(id, Bart)).Code);
            Assert.Equal(ServiceError.NotFound, _sessions.GetActive(Anna).Error);
            Assert.Equal(id.ToString("N"), Ok(_sessions.GetActive(Bart)).DuelId);
        }

        // ---- Sprzątanie ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task ThirtyDuelCycles_NoTimersLeft_FinishedSessionsSweptAfterRetention()
        {
            for (var cycle = 0; cycle < 30; cycle++)
            {
                var id = await LiveAsync();
                await PlayAsync(id, "reload", "reload");
                _time.Advance(_options.RoundDuration);
                Ok(await _sessions.SurrenderAsync(id, cycle % 2 == 0 ? Anna : Bart));
            }

            Assert.Equal(0, _time.ActiveTimers);
            Assert.Equal(0, _sessions.TimerCount);
            Assert.Equal(30, _sessions.SessionCount);   // zakończone czekają FinishedRetention (odczyt wyniku, rewanż)

            _time.Advance(GameSessionService.FinishedRetention + TimeSpan.FromSeconds(1));
            Ok(await _sessions.CreateAsync(Cole, "Cole", DuelMode.Training));
            Assert.Equal(1, _sessions.SessionCount);
        }
    }
}
