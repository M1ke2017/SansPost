using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SansPost.Features.Duels;
using SansPost.Infrastructure.Security;

namespace SansPost.Hubs
{
    // Stół gry na żywo (Sprint 20) — cienka warstwa SignalR nad GameSessionService i DuelChallengeService.
    // Hub niczego nie liczy (obrażenia, amunicja, wynik, zwycięzca — silnik F#) i nie sprawdza statusu konta (polityka
    // w serwisach): identyfikuje gracza, deleguje i zwraca wynik w kontrakcie błędów REST (kod + bezpieczny komunikat).
    // Tożsamość wyłącznie z uwierzytelnionego połączenia (cookie przeglądarki albo JWT) — nigdy z argumentów metod.
    // Połączenia: jeden gracz może mieć kilka kart; zdarzenia idą do wszystkich jego połączeń (Clients.User).
    [Authorize(Policy = AuthPolicies.DuelPlayer)]
    public sealed class DuelHub : Hub
    {
        public const string Path = "/hubs/duel";

        private readonly GameSessionService _sessions;
        private readonly DuelChallengeService _challenges;
        private readonly DuelConnectionRegistry _registry;
        private readonly IDuelNotifier _notifier;
        private readonly DuelStandingsService _standings;
        private readonly ILogger<DuelHub> _logger;

        public DuelHub(GameSessionService sessions, DuelChallengeService challenges, DuelConnectionRegistry registry, IDuelNotifier notifier,
            DuelStandingsService standings, ILogger<DuelHub> logger)
        {
            _sessions = sessions;
            _challenges = challenges;
            _registry = registry;
            _notifier = notifier;
            _standings = standings;
            _logger = logger;
        }

        private int UserId => Context.User!.GetUserId()!.Value;
        private string Alias => Context.User?.Identity?.Name ?? "Rewolwerowiec";

        public override async Task OnConnectedAsync()
        {
            // Pierwsze połączenie gracza: pojawia się przy stole, a jeśli wraca w oknie powrotu — pojedynek trwa dalej.
            if (_registry.Add(UserId, Alias, Context.ConnectionId))
            {
                // Tylko pierwsze połączenie gracza i ostatnie rozłączenie (kolejne karty przeglądarki — bez szumu w logach).
                _logger.LogInformation("Duel table: user {UserId} joined.", UserId);
                await _sessions.PlayerOnlineAsync(UserId);
                await _notifier.PublishAsync(new[] { DuelEvent.Presence });
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Dopiero ostatnie połączenie gracza oznacza odejście od stołu (inne karty dalej grają).
            if (_registry.Remove(Context.ConnectionId) is { } userId)
            {
                _logger.LogInformation("Duel table: user {UserId} left ({Reason}).", userId, exception is null ? "closed" : "connection lost");
                await _sessions.PlayerOfflineAsync(userId);
                await _notifier.PublishAsync(new[] { DuelEvent.Presence });
            }
            await base.OnDisconnectedAsync(exception);
        }

        // Stan przy wejściu i po każdym ponownym połączeniu: wznowienie trwającego pojedynku bez tworzenia nowego.
        public async Task<LobbyState> RequestState()
        {
            var duel = _sessions.GetActive(UserId);
            return new LobbyState(
                Alias,
                await _challenges.AvailablePlayersAsync(UserId, Context.ConnectionAborted),
                _challenges.PendingFor(UserId),
                duel.Succeeded ? duel.Value : null,
                await GetStandings());
        }

        // Ranking stołu i własne statystyki — po wejściu i po zdarzeniu StandingsChanged (zapisany wynik PvP).
        public async Task<StandingsView> GetStandings()
        {
            var standings = await _standings.GetStandingsAsync(Context.ConnectionAborted);
            return new StandingsView(standings.Top, standings.Champion, await _standings.GetStatsAsync(UserId, Context.ConnectionAborted));
        }

        public Task<IReadOnlyList<OnlinePlayer>> GetPlayers() => _challenges.AvailablePlayersAsync(UserId, Context.ConnectionAborted);

        public async Task<HubResult<ChallengeInfo>> ChallengeUser(string playerHandle) =>
            HubResult<ChallengeInfo>.From(await _challenges.ChallengeAsync(UserId, Alias, playerHandle, Context.ConnectionAborted));

        public async Task<HubResult<DuelSnapshot>> AcceptChallenge(string challengeId) =>
            HubResult<DuelSnapshot>.From(await _challenges.AcceptAsync(UserId, challengeId, Context.ConnectionAborted));

        public async Task<HubResult<ChallengeInfo>> RejectChallenge(string challengeId) =>
            HubResult<ChallengeInfo>.From(await _challenges.RejectAsync(UserId, challengeId));

        public async Task<HubResult<ChallengeInfo>> CancelChallenge() =>
            HubResult<ChallengeInfo>.From(await _challenges.CancelAsync(UserId));

        public async Task<HubResult<DuelSnapshot>> StartTraining() =>
            HubResult<DuelSnapshot>.From(await _sessions.CreateAsync(UserId, Alias, DuelMode.Training, Context.ConnectionAborted));

        public async Task<HubResult<DuelSnapshot>> SetReady(string duelId) =>
            Guid.TryParse(duelId, out var id)
                ? HubResult<DuelSnapshot>.From(await _sessions.SetReadyAsync(id, UserId, Context.ConnectionAborted))
                : NotFound<DuelSnapshot>();

        // Ruch = pojedynek + numer rundy + karta. Numer rundy chroni przed spóźnionym pakietem i podwójnym kliknięciem.
        public async Task<HubResult<MoveResponse>> SubmitMove(string duelId, int round, string card) =>
            Guid.TryParse(duelId, out var id)
                ? HubResult<MoveResponse>.From(await _sessions.SubmitMoveAsync(id, UserId, card, round, Context.ConnectionAborted))
                : NotFound<MoveResponse>();

        public async Task<HubResult<DuelSnapshot>> Surrender(string duelId) =>
            Guid.TryParse(duelId, out var id) ? HubResult<DuelSnapshot>.From(await _sessions.SurrenderAsync(id, UserId)) : NotFound<DuelSnapshot>();

        public async Task<HubResult<DuelSnapshot>> RequestRematch(string duelId) =>
            Guid.TryParse(duelId, out var id)
                ? HubResult<DuelSnapshot>.From(await _sessions.RequestRematchAsync(id, UserId, Context.ConnectionAborted))
                : NotFound<DuelSnapshot>();

        public async Task<HubResult<DuelSnapshot>> LeaveDuel(string duelId) =>
            Guid.TryParse(duelId, out var id) ? HubResult<DuelSnapshot>.From(await _sessions.LeaveAsync(id, UserId)) : NotFound<DuelSnapshot>();

        private static HubResult<T> NotFound<T>() => new(false, "duel-not-found", "Nie znaleziono pojedynku.", default);
    }

    // Zdarzenia serwisów → klienci huba. Adresat po UserId (wszystkie połączenia gracza; SignalR mapuje użytkownika
    // z claimu NameIdentifier), zdarzenie ogólne (PresenceChanged) — do wszystkich przy stole.
    public sealed class HubDuelNotifier : IDuelNotifier
    {
        private readonly IHubContext<DuelHub> _hub;

        public HubDuelNotifier(IHubContext<DuelHub> hub)
        {
            _hub = hub;
        }

        public async Task PublishAsync(IReadOnlyList<DuelEvent> events)
        {
            foreach (var e in events)
            {
                var target = e.UserId is { } userId ? _hub.Clients.User(userId.ToString(CultureInfo.InvariantCulture)) : _hub.Clients.All;
                if (e.Payload is null)
                    await target.SendAsync(e.Name);
                else
                    await target.SendAsync(e.Name, e.Payload);
            }
        }
    }
}
