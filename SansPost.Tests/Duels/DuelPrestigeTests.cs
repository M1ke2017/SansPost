using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SansPost.Features;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Duels
{
    // Sprint 21 — trwałe znaczenie pojedynków: wynik PvP zapisany dokładnie raz (także przez oddanie i walkower),
    // trening pomijany, gwiazdki = zwycięstwa, Top 10 z deterministyczną kolejnością, Mistrz Stołu, prywatność
    // (bez UserId i e-maili) i widoczność (ranking tylko z kont aktywnych, historia zostaje). Prawdziwy silnik F#,
    // prawdziwa baza (SQLite), ręczny zegar.
    [Trait("Category", "Duels")]
    public sealed class DuelPrestigeTests : IDisposable
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private readonly DuelAccounts _accounts = new();
        private readonly ManualTimeProvider _time = new();
        private readonly RecordingNotifier _events = new();
        private readonly GameSessionService _sessions;
        private readonly int Anna, Bart, Cole;

        public DuelPrestigeTests()
        {
            Anna = _accounts.Add("AnnaDuel");
            Bart = _accounts.Add("BartDuel");
            Cole = _accounts.Add("ColeDuel");
            _sessions = _accounts.Sessions(_time, _events);
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _accounts.Dispose();
        }

        private static T Ok<T>(ServiceResult<T> result)
        {
            Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
            return result.Value!;
        }

        private async Task<Guid> LiveAsync(int one, int two)
        {
            var duel = Ok(await _sessions.StartLiveAsync(one, $"P{one}", two, $"P{two}"));
            var id = Guid.Parse(duel.DuelId);
            Ok(await _sessions.SetReadyAsync(id, one));
            Ok(await _sessions.SetReadyAsync(id, two));
            return id;
        }

        private async Task PlayAsync(Guid id, int one, string oneCard, int two, string twoCard)
        {
            var round = Ok(_sessions.Get(id, one)).Round;
            Ok(await _sessions.SubmitMoveAsync(id, one, oneCard, round));
            Ok(await _sessions.SubmitMoveAsync(id, two, twoCard, round));
        }

        // Pierwszy gracz nokautuje drugiego w 5 rundach (strzał — prowokacja przeciwnika wystawia go na strzał).
        private async Task KnockoutAsync(Guid id, int winner, int loser)
        {
            await PlayAsync(id, winner, "shoot", loser, "taunt");
            await PlayAsync(id, winner, "reload", loser, "taunt");
            await PlayAsync(id, winner, "shoot", loser, "taunt");
            await PlayAsync(id, winner, "reload", loser, "taunt");
            await PlayAsync(id, winner, "shoot", loser, "taunt");
        }

        private List<DuelResultRecord> Results()
        {
            using var context = _accounts.CreateContext();
            return context.DuelResults.AsNoTracking().OrderBy(r => r.Id).ToList();
        }

        private Task<DuelStats> StatsAsync(int userId) =>
            _accounts.WithScopeAsync(p => p.GetRequiredService<DuelStandingsService>().GetStatsAsync(userId));

        private Task<DuelStandings> StandingsAsync() =>
            _accounts.WithScopeAsync(p => p.GetRequiredService<DuelStandingsService>().GetStandingsAsync());

        [Fact]
        public async Task KnockoutWin_PersistedOnce_WinnerGetsStar_StandingsEventAfterSave()
        {
            var id = await LiveAsync(Anna, Bart);
            await KnockoutAsync(id, Anna, Bart);

            var record = Assert.Single(Results());
            Assert.Equal((id, Anna, Bart, (int?)Anna, DuelResultType.Win, DuelFinishReason.Knockout, 5),
                (record.DuelId, record.PlayerOneId, record.PlayerTwoId, record.WinnerId, record.ResultType, record.FinishReason, record.RoundCount));
            Assert.Equal(_time.GetUtcNow().UtcDateTime, record.FinishedAt);
            Assert.Equal(new DuelStats(1, 1, 0, 0), await StatsAsync(Anna));
            Assert.Equal(1, (await StatsAsync(Anna)).PrestigeStars);
            Assert.Equal(new DuelStats(1, 0, 1, 0), await StatsAsync(Bart));

            // Zdarzenie rankingu po zapisie, w tej samej paczce co koniec gry (klienci odświeżają już zapisany wynik).
            var names = _events.Events.Select(e => e.Name).ToList();
            Assert.Contains(DuelEvents.StandingsChanged, names);
            Assert.True(names.LastIndexOf(DuelEvents.StandingsChanged) > names.LastIndexOf(DuelEvents.RoundResolved));
        }

        [Fact]
        public async Task Draw_ByTripleDoubleTimeout_NoWinnerNoStar()
        {
            await LiveAsync(Anna, Bart);
            for (var i = 0; i < 3; i++)
                _time.Advance(new DuelOptions().RoundDuration);

            var record = Assert.Single(Results());
            Assert.Equal((DuelResultType.Draw, (int?)null, DuelFinishReason.Knockout, 3), (record.ResultType, record.WinnerId, record.FinishReason, record.RoundCount));
            Assert.Equal(new DuelStats(1, 0, 0, 1), await StatsAsync(Anna));
            Assert.Equal(0, (await StatsAsync(Bart)).PrestigeStars);
        }

        [Fact]
        public async Task Surrender_And_Disconnect_OpponentWinsStar_ReasonRecorded()
        {
            var first = await LiveAsync(Anna, Bart);
            Ok(await _sessions.SurrenderAsync(first, Bart));
            var second = await LiveAsync(Anna, Bart);
            await _sessions.PlayerOfflineAsync(Anna);
            _time.Advance(new DuelOptions().DisconnectGrace);

            var results = Results();
            Assert.Equal(2, results.Count);
            Assert.Equal(((int?)Anna, DuelFinishReason.Surrender, 0), (results[0].WinnerId, results[0].FinishReason, results[0].RoundCount));
            Assert.Equal((second, (int?)Bart, DuelFinishReason.Disconnect), (results[1].DuelId, results[1].WinnerId, results[1].FinishReason));
            Assert.Equal((1, 1), ((await StatsAsync(Anna)).PrestigeStars, (await StatsAsync(Bart)).PrestigeStars));
        }

        [Fact]
        public async Task Training_IsNeverRecorded()
        {
            var training = Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Training));
            var id = Guid.Parse(training.DuelId);
            for (var round = 1; round <= 12 && Ok(_sessions.Get(id, Anna)).Status != "finished"; round++)
                Ok(await _sessions.SubmitMoveAsync(id, Anna, "taunt", round));
            Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Training));   // nowy trening po zakończonym

            Assert.Equal("finished", Ok(_sessions.Get(id, Anna)).Status);
            Assert.Empty(Results());
            Assert.Equal(DuelStats.None, await StatsAsync(Anna));
            Assert.DoesNotContain(_events.Events, e => e.Name == DuelEvents.StandingsChanged);
        }

        [Fact]
        public async Task RestChallenge_BetweenHumans_IsRecordedToo()
        {
            var created = Ok(await _sessions.CreateAsync(Anna, "Anna", DuelMode.Challenge));
            var id = Guid.Parse(created.DuelId);
            Ok(await _sessions.JoinAsync(id, Bart, "Bart"));
            await KnockoutAsync(id, Anna, Bart);

            Assert.Equal((int?)Anna, Assert.Single(Results()).WinnerId);
        }

        [Fact]
        public async Task DuplicateAndRepeatedFinish_SingleRow()
        {
            var id = await LiveAsync(Anna, Bart);
            Ok(await _sessions.SurrenderAsync(id, Bart));
            Assert.Equal("duel-finished", (await _sessions.SurrenderAsync(id, Anna)).Code);   // drugi "koniec" — bez zapisu

            // Ten sam wynik zapisany jeszcze raz wprost (np. drugi callback) — idempotentnie.
            var again = new DuelResultRecord
            {
                DuelId = id, PlayerOneId = Anna, PlayerTwoId = Bart, WinnerId = Bart, ResultType = DuelResultType.Win,
                RoundCount = 0, FinishedAt = DateTime.UtcNow, FinishReason = DuelFinishReason.Surrender
            };
            var inserted = await _accounts.WithScopeAsync(p => p.GetRequiredService<DuelStandingsService>().RecordAsync(again));

            Assert.False(inserted);
            var record = Assert.Single(Results());
            Assert.Equal((int?)Anna, record.WinnerId);   // pierwszy zapis zostaje, drugi niczego nie nadpisuje
            Assert.Equal(1, (await StatsAsync(Anna)).PrestigeStars);
        }

        [Fact]
        public async Task Rematch_IsSeparateDuel_SeparateRecord_StarsAccumulate()
        {
            var id = await LiveAsync(Anna, Bart);
            await KnockoutAsync(id, Anna, Bart);
            Ok(await _sessions.RequestRematchAsync(id, Anna));
            var rematch = Guid.Parse(Ok(await _sessions.RequestRematchAsync(id, Bart)).DuelId);
            Ok(await _sessions.SetReadyAsync(rematch, Anna));
            Ok(await _sessions.SetReadyAsync(rematch, Bart));
            Ok(await _sessions.SurrenderAsync(rematch, Bart));

            var results = Results();
            Assert.Equal(2, results.Count);
            Assert.NotEqual(results[0].DuelId, results[1].DuelId);
            Assert.Equal(new DuelStats(2, 2, 0, 0), await StatsAsync(Anna));
            Assert.Equal(new DuelStats(2, 0, 2, 0), await StatsAsync(Bart));
        }

        [Fact]
        public async Task Leaderboard_DeterministicOrder_Champion_NoPrivateData()
        {
            Assert.Null((await StandingsAsync()).Champion);   // stół czeka na pierwszego mistrza

            // Anna: 2 wygrane w 2 grach, Cole: 2 wygrane w 3 grach, Bart: 0 wygranych.
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Anna, Bart), Bart));
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Cole, Bart), Bart));
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Cole, Anna), Cole));   // Anna wygrywa z Cole'em
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Cole, Bart), Bart));

            var standings = await StandingsAsync();
            Assert.Equal(new[] { "AnnaDuel", "ColeDuel", "BartDuel" }, standings.Top.Select(e => e.Alias));
            Assert.Equal(new[] { 1, 2, 3 }, standings.Top.Select(e => e.Rank));
            var anna = standings.Top[0];
            Assert.Equal((2, 2, 2, 0, 0), (anna.Stars, anna.GamesPlayed, anna.Wins, anna.Losses, anna.Draws));
            Assert.Equal((2, 3, 1), (standings.Top[1].Stars, standings.Top[1].GamesPlayed, standings.Top[1].Losses));
            Assert.Equal(new TableChampion("AnnaDuel", 2), standings.Champion);

            var json = JsonSerializer.Serialize(standings, Web);
            Assert.DoesNotContain("@", json);
            Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SuspendedOrBanned_HiddenFromLeaderboardAndChampion_HistoryKept()
        {
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Anna, Bart), Bart));
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Anna, Cole), Cole));
            Assert.Equal("AnnaDuel", (await StandingsAsync()).Champion!.Alias);

            _accounts.SetStatus(Anna, AccountStatus.Suspended);
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Bart, Cole), Cole));   // nowy wynik unieważnia pamięć podręczną

            var standings = await StandingsAsync();
            Assert.DoesNotContain(standings.Top, e => e.Alias == "AnnaDuel");
            Assert.Equal("BartDuel", standings.Champion!.Alias);
            Assert.Equal(3, Results().Count);                                      // historia nietknięta
            Assert.Equal(2, (await StatsAsync(Anna)).PrestigeStars);               // własne statystyki zostają

            _accounts.SetStatus(Bart, AccountStatus.Banned);
            var dave = _accounts.Add("DaveDuel");
            Ok(await _sessions.SurrenderAsync(await LiveAsync(Cole, dave), Cole));   // kolejny wynik — świeży ranking
            Assert.DoesNotContain((await StandingsAsync()).Top, e => e.Alias == "BartDuel");
        }
    }
}
