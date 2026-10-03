using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SansPost.Features;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Duels
{
    // Sprint 19 — sesje pojedynków po stronie serwera: uczestnicy, ukryte karty, runda rozstrzygana dokładnie raz,
    // współbieżne ruchy, powtórzone żądania, trening z manekinem. Silnik F# jest prawdziwy (bez mocków).
    // Sprint 19-FIX — polityka konta: prawdziwa brama WriteGuard na bazie SQLite (konta Active / Suspended).
    [Trait("Category", "Duels")]
    public sealed class GameSessionServiceTests : IDisposable
    {
        private readonly DuelAccounts _accounts = new();
        private readonly int Anna, Bart, Stranger;

        public GameSessionServiceTests()
        {
            Anna = _accounts.Add();
            Bart = _accounts.Add();
            Stranger = _accounts.Add();
        }

        public void Dispose() => _accounts.Dispose();

        private void SetStatus(int userId, AccountStatus status) => _accounts.SetStatus(userId, status);

        private GameSessionService Service(TimeProvider? time = null) => _accounts.Sessions(time ?? TimeProvider.System, new RecordingNotifier());

        private static T Ok<T>(ServiceResult<T> result)
        {
            Assert.True(result.Succeeded, $"{result.Error}: {result.Message}");
            return result.Value!;
        }

        private async Task<(GameSessionService Service, Guid Id)> ChallengeAsync()
        {
            _accounts.ClearDailyUsage();   // testy wielu pojedynków z rzędu; dzienny limit gier — DailyGameQuotaTests
            var service = Service();
            var created = Ok(await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Challenge));
            Ok(await service.JoinAsync(Guid.Parse(created.DuelId), Bart, "KowbojBart"));
            return (service, Guid.Parse(created.DuelId));
        }

        [Fact]
        public async Task Create_WaitsForOpponent_Join_StartsDuel()
        {
            var service = Service();
            var created = Ok((await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Challenge)));

            Assert.Equal("waiting-for-opponent", created.Status);
            Assert.Null(created.Opponent);
            Assert.All(created.AvailableActions, a => Assert.False(a.Enabled));
            Assert.Equal(ServiceError.Conflict, (await service.SubmitMoveAsync(Guid.Parse(created.DuelId), Anna, "dodge", 1)).Error);

            var joined = Ok((await service.JoinAsync(Guid.Parse(created.DuelId), Bart, "KowbojBart")));
            Assert.Equal("in-progress", joined.Status);
            Assert.Equal("KowbojBart", joined.You.Alias);
            Assert.Equal("SzeryfAnna", joined.Opponent!.Alias);
            Assert.Equal((3, 1), (joined.You.Prestige, joined.You.Ammo));
            Assert.Equal(new[] { "shoot", "dodge", "reload", "block", "taunt" }, joined.AvailableActions.Select(a => a.Card));
        }

        [Fact]
        public async Task Join_Rules_OwnFullAndUnknown()
        {
            var (service, id) = await ChallengeAsync();

            Assert.Equal("duel-own", (await service.JoinAsync(id, Anna, "SzeryfAnna")).Code);
            Assert.Equal("duel-full", (await service.JoinAsync(id, Stranger, "Obcy")).Code);
            Assert.True((await service.JoinAsync(id, Bart, "KowbojBart")).Succeeded);          // powtórzone dołączenie — bez zmian
            Assert.Equal(ServiceError.NotFound, (await service.JoinAsync(Guid.NewGuid(), Stranger, "Obcy")).Error);
        }

        // Autoryzacja: obcy nie widzi ani nie gra; uczestnik gra tylko za siebie (miejsce wynika z tożsamości).
        [Fact]
        public async Task Stranger_CannotReadOrMove_PlayersMoveOnlyForThemselves()
        {
            var (service, id) = await ChallengeAsync();

            Assert.Equal(ServiceError.NotFound, service.Get(id, Stranger).Error);
            Assert.Equal(ServiceError.NotFound, (await service.SubmitMoveAsync(id, Stranger, "shoot", 1)).Error);

            Ok((await service.SubmitMoveAsync(id, Anna, "block", 1)));
            var bart = Ok(service.Get(id, Bart));
            Assert.Null(bart.YourMove);                                  // ruch Anny nie stał się ruchem Barta
            Assert.True(bart.OpponentReady);
        }

        // Ukryta karta: po pierwszym ruchu przeciwnik widzi tylko gotowość — w całym JSON nie ma śladu wybranej karty.
        [Fact]
        public async Task FirstMove_IsHiddenFromOpponent_UntilResolution()
        {
            var (service, id) = await ChallengeAsync();

            var first = Ok((await service.SubmitMoveAsync(id, Anna, "taunt", 1)));
            Assert.True(first.Accepted);
            Assert.False(first.Resolved);
            Assert.False(first.OpponentReady);
            Assert.Equal("taunt", first.Snapshot.YourMove);

            var bartView = Ok(service.Get(id, Bart));
            var json = JsonSerializer.Serialize(bartView, SansPostFactory.Json);
            Assert.True(bartView.OpponentReady);
            Assert.True(bartView.Opponent!.Ready);
            Assert.Empty(bartView.History);
            Assert.DoesNotContain("\"yourMove\":\"taunt\"", json);
            Assert.DoesNotContain("Prowokacja przeciwnika", json);
            Assert.Null(bartView.YourMove);

            var second = Ok((await service.SubmitMoveAsync(id, Bart, "dodge", 1)));
            Assert.True(second.Resolved);
            Assert.Equal("dodge", second.RoundResult!.YourCard);
            Assert.Equal("taunt", second.RoundResult.OpponentCard);        // odsłonięte dopiero teraz
            Assert.Contains("Prowokacja przeciwnika ukarała Twoją defensywę.", second.RoundResult.Effects);
            Assert.Equal((2, 3), (second.Snapshot.You.Prestige, second.Snapshot.Opponent!.Prestige));
            Assert.Equal(2, second.Snapshot.Round);
        }

        [Fact]
        public async Task DuplicateAndStaleMoves_DoNotChangeState()
        {
            var (service, id) = await ChallengeAsync();
            Ok((await service.SubmitMoveAsync(id, Anna, "reload", 1)));

            var duplicate = (await service.SubmitMoveAsync(id, Anna, "shoot", 1));
            Assert.Equal("duel-move-already-submitted", duplicate.Code);
            Assert.Equal("reload", Ok(service.Get(id, Anna)).YourMove);

            Ok((await service.SubmitMoveAsync(id, Bart, "dodge", 1)));
            var retry = (await service.SubmitMoveAsync(id, Bart, "dodge", 1));            // ponowione żądanie po rozstrzygnięciu
            Assert.Equal("duel-stale-round", retry.Code);
            var state = Ok(service.Get(id, Anna));
            Assert.Equal(2, state.Round);
            Assert.Single(state.History);
            Assert.False(state.OpponentReady);

            Assert.Equal("duel-unknown-card", (await service.SubmitMoveAsync(id, Anna, "lasso", 2)).Code);
            Assert.Equal(ServiceError.Validation, (await service.SubmitMoveAsync(id, Anna, "lasso", 2)).Error);
        }

        [Fact]
        public async Task ShootWithoutAmmo_IsRejectedByEngine()
        {
            var (service, id) = await ChallengeAsync();
            Ok((await service.SubmitMoveAsync(id, Anna, "shoot", 1)));
            Ok((await service.SubmitMoveAsync(id, Bart, "block", 1)));

            var noAmmo = (await service.SubmitMoveAsync(id, Anna, "shoot", 2));

            Assert.Equal(ServiceError.Validation, noAmmo.Error);
            Assert.Equal("duel-no-ammo", noAmmo.Code);
            var shoot = Ok(service.Get(id, Anna)).AvailableActions.Single(a => a.Card == "shoot");
            Assert.False(shoot.Enabled);
            Assert.Equal("Brak naboju.", shoot.DisabledReason);
        }

        // Współbieżność: obaj gracze równocześnie, wiele razy — runda zawsze rozstrzygnięta dokładnie raz, oba ruchy zapisane.
        [Theory]
        [InlineData(10)]
        [InlineData(50)]
        public async Task SimultaneousMoves_ResolveExactlyOnce(int duels)
        {
            for (var i = 0; i < duels; i++)
            {
                var (service, id) = await ChallengeAsync();
                using var start = new ManualResetEventSlim(false);
                var anna = Task.Run(async () => { start.Wait(); return (await service.SubmitMoveAsync(id, Anna, "shoot", 1)); });
                var bart = Task.Run(async () => { start.Wait(); return (await service.SubmitMoveAsync(id, Bart, "reload", 1)); });
                start.Set();
                var results = await Task.WhenAll(anna, bart);

                Assert.All(results, r => Assert.True(r.Succeeded));
                Assert.Equal(1, results.Count(r => r.Value!.Resolved));
                var state = Ok(service.Get(id, Anna));
                var round = Assert.Single(state.History);
                Assert.Equal(("shoot", "reload"), (round.YourCard, round.OpponentCard));
                Assert.Equal((3, 2), (state.You.Prestige, state.Opponent!.Prestige));
                Assert.Equal(2, state.Round);
            }
        }

        // Burza powtórzonych żądań obu graczy: dokładnie jeden przyjęty ruch na gracza, jedno rozstrzygnięcie.
        [Fact]
        public async Task ManyParallelDuplicates_OneMovePerPlayer_OneResolution()
        {
            var (service, id) = await ChallengeAsync();
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                start.Wait();
                return i % 2 == 0 ? (await service.SubmitMoveAsync(id, Anna, "taunt", 1)) : (await service.SubmitMoveAsync(id, Bart, "block", 1));
            })).ToList();
            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(2, results.Count(r => r.Succeeded));
            Assert.Equal(1, results.Count(r => r.Succeeded && r.Value!.Resolved));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.Code, new[] { "duel-move-already-submitted", "duel-stale-round" }));
            var state = Ok(service.Get(id, Bart));
            Assert.Single(state.History);
            Assert.Equal((2, 3), (state.You.Prestige, state.Opponent!.Prestige));
        }

        [Fact]
        public async Task Training_DummyAnswersImmediately_DuelPlaysToTheEnd()
        {
            var service = Service();
            var duel = Ok((await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training)));
            var id = Guid.Parse(duel.DuelId);

            Assert.Equal("in-progress", duel.Status);
            Assert.Equal("training", duel.Mode);
            Assert.Equal(DuelMapper.TrainingAlias, duel.Opponent!.Alias);

            var round = 1;
            MoveResponse? last = null;
            while (last?.Snapshot.Status != "finished")
            {
                last = Ok((await service.SubmitMoveAsync(id, Anna, "taunt", round)));
                Assert.True(last.Resolved);
                round = last.Snapshot.Round;
                Assert.True(last.Snapshot.History.Count <= 12);
            }

            Assert.Contains(last.Snapshot.Result, new[] { "win", "loss", "draw" });
            Assert.Equal("reload", last.Snapshot.History[0].OpponentCard);   // scenariusz manekina
            Assert.All(last.Snapshot.AvailableActions, a => Assert.False(a.Enabled));
            Assert.Equal("duel-finished", (await service.SubmitMoveAsync(id, Anna, "dodge", round)).Code);
        }

        [Fact]
        public async Task OneActiveDuelPerUser_TrainingIsReplaced_HumanDuelIsKept()
        {
            var service = Service();
            var training = Ok((await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training)));
            var next = Ok((await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training)));
            Assert.Equal(ServiceError.NotFound, service.Get(Guid.Parse(training.DuelId), Anna).Error);
            Assert.Equal(next.DuelId, Ok(service.GetActive(Anna)).DuelId);

            var challenge = Ok((await service.CreateAsync(Bart, "KowbojBart", DuelMode.Challenge)));
            Ok((await service.JoinAsync(Guid.Parse(challenge.DuelId), Anna, "SzeryfAnna")));   // trening Anny ustępuje wyzwaniu
            Assert.Equal(1, service.SessionCount);
            Assert.Equal("duel-already-active", (await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training)).Code);
            Assert.Equal(challenge.DuelId, Ok(service.GetActive(Anna)).DuelId);
        }

        [Fact]
        public async Task IdleSessions_AreSweptOnCreate()
        {
            var time = new MutableTimeProvider();
            var service = Service(time);
            var old = Ok((await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Challenge)));
            time.Advance(GameSessionService.IdleTimeout + TimeSpan.FromMinutes(1));

            Ok((await service.CreateAsync(Bart, "KowbojBart", DuelMode.Training)));

            Assert.Equal(1, service.SessionCount);
            Assert.Equal(ServiceError.NotFound, service.Get(Guid.Parse(old.DuelId), Anna).Error);
        }

        [Fact]
        public async Task Snapshot_ContainsNoUserIds()
        {
            var (service, id) = await ChallengeAsync();
            var json = JsonSerializer.Serialize(Ok(service.Get(id, Anna)), SansPostFactory.Json);

            Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playerOneId", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"alias\":\"KowbojBart\"", json);
        }
            // ---- Sprint 19-FIX: polityka konta (Active gra, Suspended tylko przegląda) ------------------------------------

        [Fact]
        public async Task Suspended_CannotCreateChallengeOrTraining_NothingCreated()
        {
            var service = Service();
            SetStatus(Anna, AccountStatus.Suspended);

            foreach (var mode in new[] { DuelMode.Challenge, DuelMode.Training })
            {
                var result = await service.CreateAsync(Anna, "SzeryfAnna", mode);
                Assert.Equal(ServiceError.Forbidden, result.Error);
                Assert.Equal("account-suspended", result.Code);
            }
            Assert.Equal(0, service.SessionCount);
            Assert.Equal(ServiceError.NotFound, service.GetActive(Anna).Error);
        }

        [Fact]
        public async Task Suspended_CannotJoin_SeatStaysFree()
        {
            var service = Service();
            var duel = Ok(await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Challenge));
            SetStatus(Bart, AccountStatus.Suspended);

            var join = await service.JoinAsync(Guid.Parse(duel.DuelId), Bart, "KowbojBart");

            Assert.Equal("account-suspended", join.Code);
            var state = Ok(service.Get(Guid.Parse(duel.DuelId), Anna));
            Assert.Equal("waiting-for-opponent", state.Status);
            Assert.Null(state.Opponent);
            Assert.Equal(ServiceError.NotFound, service.Get(Guid.Parse(duel.DuelId), Bart).Error);
            Assert.True((await service.JoinAsync(Guid.Parse(duel.DuelId), Stranger, "Obcy")).Succeeded);   // miejsce nadal wolne
        }

        // Zawieszenie w trakcie pojedynku: A zagrał, B zawieszony próbuje zagrać — ruch odrzucony, runda nierozstrzygnięta,
        // ukryta karta A nadal ukryta, stan spójny (po odwieszeniu gra toczy się dalej od tego samego miejsca).
        [Fact]
        public async Task SuspendedMidDuel_MoveRejected_RoundNotResolved_HiddenMoveStaysHidden()
        {
            var (service, id) = await ChallengeAsync();
            Ok(await service.SubmitMoveAsync(id, Anna, "taunt", 1));
            SetStatus(Bart, AccountStatus.Suspended);

            var rejected = await service.SubmitMoveAsync(id, Bart, "dodge", 1);
            Assert.Equal(ServiceError.Forbidden, rejected.Error);
            Assert.Equal("account-suspended", rejected.Code);

            var anna = Ok(service.Get(id, Anna));
            Assert.Equal(("taunt", false, 1), (anna.YourMove, anna.OpponentReady, anna.Round));
            Assert.Empty(anna.History);

            // Odczyt własnego pojedynku zostaje; ukryta karta Anny nie wycieka.
            var bart = Ok(service.Get(id, Bart));
            var json = JsonSerializer.Serialize(bart, SansPostFactory.Json);
            Assert.True(bart.OpponentReady);
            Assert.Null(bart.YourMove);
            Assert.Empty(bart.History);
            Assert.DoesNotContain("taunt\"", json.Replace("\"card\":\"taunt\"", ""));
            Assert.Equal((3, 3), (bart.You.Prestige, bart.Opponent!.Prestige));

            SetStatus(Bart, AccountStatus.Active);
            var resumed = Ok(await service.SubmitMoveAsync(id, Bart, "dodge", 1));
            Assert.True(resumed.Resolved);
            Assert.Equal(("dodge", "taunt"), (resumed.RoundResult!.YourCard, resumed.RoundResult.OpponentCard));
            Assert.Equal(2, resumed.Snapshot.You.Prestige);
        }

        [Fact]
        public async Task SuspendedDuringTraining_MovesRejected_DuelKept()
        {
            var service = Service();
            var duel = Ok(await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training));
            var id = Guid.Parse(duel.DuelId);
            Ok(await service.SubmitMoveAsync(id, Anna, "dodge", 1));
            SetStatus(Anna, AccountStatus.Suspended);

            Assert.Equal("account-suspended", (await service.SubmitMoveAsync(id, Anna, "dodge", 2)).Code);
            var state = Ok(service.GetActive(Anna));
            Assert.Equal(duel.DuelId, state.DuelId);
            Assert.Equal(2, state.Round);
            Assert.Single(state.History);
        }

        [Fact]
        public async Task Banned_CannotAct_AndSuspendedStranger_LearnsNothing()
        {
            var (service, id) = await ChallengeAsync();
            SetStatus(Anna, AccountStatus.Banned);
            Assert.Equal("account-inactive", (await service.SubmitMoveAsync(id, Anna, "dodge", 1)).Code);
            Assert.Equal("account-inactive", (await service.CreateAsync(Anna, "SzeryfAnna", DuelMode.Training)).Code);

            SetStatus(Stranger, AccountStatus.Suspended);
            Assert.Equal(ServiceError.NotFound, (await service.SubmitMoveAsync(id, Stranger, "dodge", 1)).Error);
        }

        [Fact]
        public async Task ActiveAccounts_Unaffected()
        {
            var (service, id) = await ChallengeAsync();
            Ok(await service.SubmitMoveAsync(id, Anna, "reload", 1));
            Assert.True(Ok(await service.SubmitMoveAsync(id, Bart, "reload", 1)).Resolved);
            Assert.True((await service.CreateAsync(Stranger, "Obcy", DuelMode.Training)).Succeeded);
        }
    }
}
