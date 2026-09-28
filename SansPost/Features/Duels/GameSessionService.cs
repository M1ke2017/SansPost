using System.Collections.Concurrent;
using SansPost.Features.Identity;
using SansPost.Game.Core;

namespace SansPost.Features.Duels
{
    // Sesje pojedynków po stronie serwera (w pamięci procesu — bez bazy na tym etapie). Źródłem prawdy jest silnik F#:
    // serwis trzyma niezmienny GameState, przyjmuje ruchy tylko od uczestników i podmienia stan wynikiem funkcji silnika.
    //   • współbieżność: blokada per pojedynek (nie globalna) — dwa równoczesne ruchy nie gubią się, runda rozstrzyga się
    //     dokładnie raz, starszy stan nie nadpisze nowszego (podmiana stanu tylko wewnątrz blokady);
    //   • ruch niesie numer rundy — powtórzone żądanie po rozstrzygnięciu dostaje konflikt, nie trafia do kolejnej rundy;
    //   • ukryte karty: klient dostaje DuelSnapshot z widoku gracza (Duel.viewFor), nigdy surowy GameState;
    //   • cudzy pojedynek wygląda jak nieistniejący (NotFound) — bez ujawniania, że istnieje;
    //   • aktywne działania (utworzenie, trening, dołączenie, ruch) tylko dla konta, które może działać — wspólna polityka
    //     WriteGuard.CheckActiveAccountAsync, sprawdzana tutaj, więc REST, Blazor i przyszły hub nie powtarzają warunku.
    //     Odczyt własnego pojedynku zostaje (zawieszony widzi stan, ale nie gra). Silnik F# nic nie wie o kontach.
    // Jeden trwający pojedynek na użytkownika; bezczynne i zakończone sesje są sprzątane przy tworzeniu nowych.
    public sealed class GameSessionService
    {
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(10);
        public const int MaxSessions = 500;

        private readonly ConcurrentDictionary<Guid, DuelSession> _duels = new();
        private readonly ConcurrentDictionary<int, object> _userGates = new();
        private readonly TimeProvider _time;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<GameSessionService> _logger;

        public GameSessionService(TimeProvider time, IServiceScopeFactory scopes, ILogger<GameSessionService> logger)
        {
            _time = time;
            _scopes = scopes;
            _logger = logger;
        }

        // Polityka konta (Active) — ten sam strażnik co zapisy treści, aktualny stan z bazy przy każdej aktywnej akcji.
        // Bez limitu zapisów: ruchy nie dotykają bazy, a liczba sesji ma własny limit (MaxSessions).
        private async Task<ServiceResult?> DeniedAsync(int userId, CancellationToken cancellationToken)
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<WriteGuard>().CheckActiveAccountAsync(userId, cancellationToken);
        }

        public int SessionCount => _duels.Count;

        private sealed class DuelSession
        {
            public required Guid Id { get; init; }
            public required DuelMode Mode { get; init; }
            public required int PlayerOneId { get; init; }
            public required string PlayerOneAlias { get; init; }
            public int? PlayerTwoId { get; set; }
            public string? PlayerTwoAlias { get; set; }
            public required GameState State { get; set; }
            public DateTimeOffset LastActivity { get; set; }
            public object Gate { get; } = new();

            public bool OpponentJoined => Mode == DuelMode.Training || PlayerTwoId is not null;
            public bool Finished => Duel.isFinished(State);

            public Player? SeatOf(int userId) =>
                userId == PlayerOneId ? Player.PlayerOne : Mode == DuelMode.Challenge && userId == PlayerTwoId ? Player.PlayerTwo : null;
        }

        public async Task<ServiceResult<DuelSnapshot>> CreateAsync(int userId, string alias, DuelMode mode, CancellationToken cancellationToken = default)
        {
            if (await DeniedAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            lock (UserGate(userId))
            {
                Sweep();
                if (ActiveFor(userId) is { } current)
                {
                    // Trwający pojedynek z człowiekiem nie znika po cichu; trening albo niepodjęte wyzwanie — tak.
                    if (current.Mode == DuelMode.Challenge && current.PlayerTwoId is not null)
                        return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
                    _duels.TryRemove(current.Id, out _);
                }

                if (_duels.Count >= MaxSessions)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Przy stole gry nie ma teraz miejsca. Spróbuj za chwilę.", "duel-capacity");

                var session = new DuelSession
                {
                    Id = Guid.NewGuid(),
                    Mode = mode,
                    PlayerOneId = userId,
                    PlayerOneAlias = alias,
                    State = Duel.createStandard(),
                    LastActivity = _time.GetUtcNow()
                };
                _duels[session.Id] = session;
                return ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerOne));
            }
        }

        public async Task<ServiceResult<DuelSnapshot>> JoinAsync(Guid duelId, int userId, string alias, CancellationToken cancellationToken = default)
        {
            if (await DeniedAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            lock (UserGate(userId))
            {
                if (!_duels.TryGetValue(duelId, out var session) || session.Mode != DuelMode.Challenge)
                    return NotFound<DuelSnapshot>();

                lock (session.Gate)
                {
                    if (session.PlayerOneId == userId)
                        return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Nie możesz dołączyć do własnego pojedynku.", "duel-own");
                    if (session.PlayerTwoId == userId)
                        return ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerTwo));   // powtórzone dołączenie
                    if (session.PlayerTwoId is not null || session.Finished)
                        return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Ten pojedynek ma już dwóch graczy.", "duel-full");

                    if (ActiveFor(userId) is { } current && current.Id != duelId)
                    {
                        if (current.Mode == DuelMode.Challenge && current.PlayerTwoId is not null)
                            return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
                        _duels.TryRemove(current.Id, out _);
                    }

                    session.PlayerTwoId = userId;
                    session.PlayerTwoAlias = alias;
                    session.LastActivity = _time.GetUtcNow();
                    return ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerTwo));
                }
            }
        }

        public ServiceResult<DuelSnapshot> Get(Guid duelId, int userId)
        {
            if (!_duels.TryGetValue(duelId, out var session))
                return NotFound<DuelSnapshot>();
            lock (session.Gate)
            {
                return session.SeatOf(userId) is { } seat
                    ? ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, seat))
                    : NotFound<DuelSnapshot>();
            }
        }

        // Trwający (albo ostatnio zakończony) pojedynek użytkownika — powrót do stołu po odświeżeniu strony.
        public ServiceResult<DuelSnapshot> GetActive(int userId)
        {
            var session = _duels.Values
                .Where(s => s.SeatOf(userId) is not null)
                .OrderByDescending(s => s.LastActivity)
                .FirstOrDefault();
            return session is null ? NotFound<DuelSnapshot>() : Get(session.Id, userId);
        }

        public async Task<ServiceResult<MoveResponse>> SubmitMoveAsync(Guid duelId, int userId, string? cardId, int round, CancellationToken cancellationToken = default)
        {
            if (DuelMapper.ParseCard(cardId) is not { } card)
                return ServiceResult<MoveResponse>.Fail(ServiceError.Validation, "Nieznana karta.", "duel-unknown-card");
            if (!_duels.TryGetValue(duelId, out var session) || session.SeatOf(userId) is null)
                return NotFound<MoveResponse>();   // cudzy pojedynek — bez ujawniania, że istnieje (także przed polityką konta)

            // Zawieszenie w trakcie pojedynku: ruch odrzucony, stan (w tym ukryta karta przeciwnika) nietknięty.
            // Poddanie / rozłączenie — Sprint 20.
            if (await DeniedAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<MoveResponse>.From(denied);

            lock (session.Gate)
            {
                if (session.SeatOf(userId) is not { } seat)
                    return NotFound<MoveResponse>();
                if (!session.OpponentJoined)
                    return ServiceResult<MoveResponse>.Fail(ServiceError.Conflict, "Czekamy na przeciwnika.", "duel-waiting-for-opponent");

                var submitted = Duel.submitAndResolveIfReady(round, seat, card, session.State);
                if (submitted.IsError)
                    return Failed(submitted.ErrorValue);

                var outcome = submitted.ResultValue;
                // Trening: manekin wybiera kartę z własnego widoku (bez dostępu do ukrytej karty gracza).
                if (outcome is Duel.MoveOutcome.Waiting waiting && session.Mode == DuelMode.Training)
                {
                    var dummyCard = TrainingDummy.chooseCard(Duel.viewFor(Player.PlayerTwo, waiting.Item));
                    var answered = Duel.submitAndResolveIfReady(round, Player.PlayerTwo, dummyCard, waiting.Item);
                    if (answered.IsError)
                        return Failed(answered.ErrorValue);
                    outcome = answered.ResultValue;
                }

                // Jedyna podmiana stanu — wewnątrz blokady pojedynku.
                RoundResult? roundResult = null;
                switch (outcome)
                {
                    case Duel.MoveOutcome.Resolved resolved:
                        session.State = resolved.Item1;
                        roundResult = DuelMapper.Round(seat, resolved.Item2);
                        break;
                    case Duel.MoveOutcome.Waiting waitingState:
                        session.State = waitingState.Item;
                        break;
                }
                session.LastActivity = _time.GetUtcNow();

                var snapshot = SnapshotFor(session, seat);
                return ServiceResult<MoveResponse>.Success(new MoveResponse(true, roundResult is not null, snapshot.OpponentReady, roundResult, snapshot));
            }
        }

        private ServiceResult<MoveResponse> Failed(GameError error)
        {
            var (kind, message, code) = DuelMapper.Error(error);
            if (error.IsInvalidState)
                _logger.LogError("Silnik pojedynku zgłosił nieprawidłowy stan: {Error}", error);
            return ServiceResult<MoveResponse>.Fail(kind, message, code);
        }

        private DuelSnapshot SnapshotFor(DuelSession session, Player seat)
        {
            var mine = seat.IsPlayerOne;
            var opponentAlias = session.Mode == DuelMode.Training ? DuelMapper.TrainingAlias : mine ? session.PlayerTwoAlias : session.PlayerOneAlias;
            return DuelMapper.Snapshot(
                session.Id.ToString("N"),
                session.Mode,
                session.OpponentJoined,
                mine ? session.PlayerOneAlias : session.PlayerTwoAlias!,
                opponentAlias,
                Duel.viewFor(seat, session.State));
        }

        private DuelSession? ActiveFor(int userId) =>
            _duels.Values.FirstOrDefault(s => s.SeatOf(userId) is not null && !s.Finished);

        private object UserGate(int userId) => _userGates.GetOrAdd(userId, _ => new object());

        // Sprzątanie przy tworzeniu: bezczynne ponad IdleTimeout albo zakończone dłużej niż FinishedRetention.
        private void Sweep()
        {
            var now = _time.GetUtcNow();
            foreach (var session in _duels.Values)
            {
                var idle = now - session.LastActivity;
                if (idle > IdleTimeout || (session.Finished && idle > FinishedRetention))
                    _duels.TryRemove(session.Id, out _);
            }
        }

        private static ServiceResult<T> NotFound<T>() =>
            ServiceResult<T>.Fail(ServiceError.NotFound, "Nie znaleziono pojedynku.", "duel-not-found");
    }
}
