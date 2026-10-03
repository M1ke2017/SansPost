using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SansPost.Features.Identity;
using SansPost.Features.Usage;
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
    //   • aktywne działania (utworzenie, trening, dołączenie, ruch, gotowość, rewanż, wyzwanie) tylko dla konta, które może
    //     działać — wspólna polityka WriteGuard.CheckActiveAccountAsync, sprawdzana tutaj (CheckParticipationAsync), więc
    //     REST, Blazor i DuelHub nie powtarzają warunku. Odczyt własnego pojedynku zostaje. Silnik F# nic nie wie o kontach.
    // Jeden trwający pojedynek na użytkownika; bezczynne i zakończone sesje są sprzątane przy tworzeniu nowych.
    // Sprint 20 — pojedynek na żywo (GameSessionService.Live.cs): ten sam serwis i ta sama blokada per pojedynek; dochodzi
    // cykl życia sesji (gotowość, zegar rundy, rozłączenia, oddanie, rewanż) i zdarzenia dla DuelHub (IDuelNotifier).
    public sealed partial class GameSessionService : IDisposable
    {
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(10);
        public const int MaxSessions = 500;

        private readonly ConcurrentDictionary<Guid, DuelSession> _duels = new();
        private readonly ConcurrentDictionary<int, object> _userGates = new();
        private readonly TimeProvider _time;
        private readonly IServiceScopeFactory _scopes;
        private readonly IDuelNotifier _notifier;
        private readonly DuelOptions _options;
        private readonly ILogger<GameSessionService> _logger;

        public GameSessionService(TimeProvider time, IServiceScopeFactory scopes, IDuelNotifier notifier, IOptions<DuelOptions> options,
            ILogger<GameSessionService> logger)
        {
            _time = time;
            _scopes = scopes;
            _notifier = notifier;
            _options = options.Value;
            _logger = logger;
        }

        // Polityka konta (Active) — ten sam strażnik co zapisy treści, aktualny stan z bazy przy każdej aktywnej akcji.
        // Bez limitu zapisów: ruchy nie dotykają bazy, a liczba sesji ma własny limit (MaxSessions).
        // Publiczne, bo wyzwania (DuelChallengeService) przechodzą przez tę samą politykę — bez drugiej kopii warunku.
        public async Task<ServiceResult?> CheckParticipationAsync(int userId, CancellationToken cancellationToken = default)
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<WriteGuard>().CheckActiveAccountAsync(userId, cancellationToken);
        }

        // Dzienny limit gier (v1.0): liczy się gra, która naprawdę się zaczęła (trening, dołączenie do wyzwania REST,
        // start pojedynku na żywo po gotowości obu — także rewanż). Rezerwacja w transakcji w osobnym scope: zużycie u
        // wszystkich uczestników (w kolejności UserId — bez zakleszczeń), start gry w pamięci pod blokadą, commit dopiero
        // po starcie. Brak limitu u któregokolwiek gracza = rollback, nikt nic nie traci (obaj albo żaden).
        private sealed class GameQuotaLease : IAsyncDisposable
        {
            public required AsyncServiceScope Scope { get; init; }
            public required Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction { get; init; }
            public int? ExceededUserId { get; set; }
            public bool Granted => ExceededUserId is null;

            public async ValueTask DisposeAsync()
            {
                await Transaction.DisposeAsync();   // bez Commit = rollback
                await Scope.DisposeAsync();
            }
        }

        private async Task<GameQuotaLease> ReserveGamesAsync(IEnumerable<int> userIds, CancellationToken cancellationToken)
        {
            var scope = _scopes.CreateAsyncScope();
            try
            {
                var context = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ApplicationDbContext>();
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                var lease = new GameQuotaLease { Scope = scope, Transaction = transaction };
                var now = _time.GetUtcNow();
                foreach (var id in userIds.Distinct().OrderBy(id => id))
                {
                    if (!await context.TryConsumeAsync(id, QuotaKind.Game, now, cancellationToken))
                    {
                        lease.ExceededUserId = id;
                        break;
                    }
                }
                return lease;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        // Commit po starcie gry. Błąd zapisu nie cofa rozpoczętej gry (gracze już grają) — tylko nie zostaje policzona.
        private async Task CommitGamesAsync(GameQuotaLease lease)
        {
            try
            {
                await lease.Transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zapisać dziennego limitu gier.");
            }
        }

        private static ServiceResult<T> GameLimit<T>(GameQuotaLease lease, int userId, DateTimeOffset now) =>
            ServiceResult<T>.From(lease.ExceededUserId == userId ? DailyQuota.LimitReached(QuotaKind.Game, now) : DailyQuota.OpponentLimitReached());

        // Wstępne sprawdzenie (bez zużycia) przed wyzwaniem i przyjęciem — żeby nie zaczynać czegoś, co i tak nie wystartuje.
        // Źródłem prawdy zostaje rezerwacja przy starcie gry.
        public async Task<bool> HasGamesLeftAsync(int userId, CancellationToken cancellationToken = default)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ApplicationDbContext>();
            return await context.UsedTodayAsync(userId, QuotaKind.Game, _time.GetUtcNow(), cancellationToken) < DailyQuota.Games;
        }

        public int SessionCount => _duels.Count;

        // Sesje z aktywnym zegarem rundy albo odliczaniem powrotu — pomiar sprzątania timerów.
        public int TimerCount => _duels.Values.Count(s => s.HasTimers);

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

            // Każda zmiana podnosi wersję — klient nie nadpisze nowszego stanu starszym zdarzeniem.
            public long Version { get; set; } = 1;

            // Cykl życia pojedynku na żywo (tylko Mode = Live). Indeks 0 = PlayerOne, 1 = PlayerTwo.
            public bool[] StartReady { get; } = new bool[2];
            public bool Started { get; set; }
            public DateTimeOffset? RoundDeadline { get; set; }
            public ITimer? RoundTimer { get; set; }
            public long RoundToken { get; set; }
            public DateTimeOffset?[] GraceUntil { get; } = new DateTimeOffset?[2];
            public ITimer?[] GraceTimers { get; } = new ITimer?[2];
            public long[] GraceTokens { get; } = new long[2];
            public string? Ending { get; set; }
            public bool[] RematchWanted { get; } = new bool[2];
            public bool RematchClaimed { get; set; }
            public Guid? RematchId { get; set; }
            public bool[] Left { get; } = new bool[2];

            // Wynik PvP do zapisania (Sprint 21) — przygotowany pod blokadą przy zakończeniu, zapisany po jej zwolnieniu.
            public DuelResultRecord? PendingResult;

            public bool OpponentJoined => Mode == DuelMode.Training || PlayerTwoId is not null;
            public bool Finished => Duel.isFinished(State);
            public bool Live => Mode == DuelMode.Live;
            public bool HumanOpponent => Mode != DuelMode.Training && PlayerTwoId is not null;
            public bool Paused => GraceUntil[0] is not null || GraceUntil[1] is not null;
            public bool HasTimers => RoundTimer is not null || GraceTimers[0] is not null || GraceTimers[1] is not null;

            public Player? SeatOf(int userId) =>
                userId == PlayerOneId ? Player.PlayerOne : Mode != DuelMode.Training && userId == PlayerTwoId ? Player.PlayerTwo : null;

            public int? UserAt(Player seat) => seat.IsPlayerOne ? PlayerOneId : PlayerTwoId;

            public void StopTimers()
            {
                RoundTimer?.Dispose();
                RoundTimer = null;
                RoundDeadline = null;
                for (var i = 0; i < 2; i++)
                {
                    GraceTimers[i]?.Dispose();
                    GraceTimers[i] = null;
                    GraceUntil[i] = null;
                }
            }
        }

        public async Task<ServiceResult<DuelSnapshot>> CreateAsync(int userId, string alias, DuelMode mode, CancellationToken cancellationToken = default)
        {
            if (mode == DuelMode.Live)
                throw new ArgumentOutOfRangeException(nameof(mode), "Pojedynek na żywo powstaje z przyjętego wyzwania (StartLiveAsync).");
            if (await CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            // Najpierw warunki bez skutków ubocznych (trwający pojedynek, miejsce) — odmowa nie dotyka limitu.
            lock (UserGate(userId))
            {
                Sweep();
                if (CreateBlocked(userId) is { } blocked)
                    return blocked;
            }

            // Trening startuje od razu (liczy się jako gra); wyzwanie REST — dopiero gdy dołączy drugi gracz (JoinAsync).
            await using var lease = mode == DuelMode.Training ? await ReserveGamesAsync(new[] { userId }, cancellationToken) : null;
            if (lease is { Granted: false })
                return GameLimit<DuelSnapshot>(lease, userId, _time.GetUtcNow());

            ServiceResult<DuelSnapshot> created;
            lock (UserGate(userId))
            {
                Sweep();
                if (CreateBlocked(userId) is { } blocked)
                    return blocked;
                if (ActiveFor(userId) is { } current)
                    Remove(current);   // trening albo niepodjęte wyzwanie ustępuje nowemu

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
                created = ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerOne));
            }
            if (lease is not null)
                await CommitGamesAsync(lease);
            return created;
        }

        // Trwający pojedynek z człowiekiem nie znika po cichu (trening albo niepodjęte wyzwanie — tak); brak miejsca przy stole.
        private ServiceResult<DuelSnapshot>? CreateBlocked(int userId)
        {
            var current = ActiveFor(userId);
            if (current is { HumanOpponent: true })
                return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
            return _duels.Count - (current is null ? 0 : 1) >= MaxSessions ? Capacity() : null;
        }

        public async Task<ServiceResult<DuelSnapshot>> JoinAsync(Guid duelId, int userId, string alias, CancellationToken cancellationToken = default)
        {
            if (await CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            // Odmowa albo powtórzone dołączenie nie dotyka limitu. Gra zaczyna się, gdy drugi gracz siada do stołu —
            // wtedy limit obu graczy razem (obaj albo żaden).
            int ownerId;
            lock (UserGate(userId))
            {
                if (!_duels.TryGetValue(duelId, out var session) || session.Mode != DuelMode.Challenge)
                    return NotFound<DuelSnapshot>();
                lock (session.Gate)
                {
                    if (JoinRefused(session, userId) is { } refused)
                        return refused;
                    ownerId = session.PlayerOneId;
                }
            }

            await using var lease = await ReserveGamesAsync(new[] { ownerId, userId }, cancellationToken);
            if (!lease.Granted)
                return GameLimit<DuelSnapshot>(lease, userId, _time.GetUtcNow());

            ServiceResult<DuelSnapshot> joined;
            var started = false;
            lock (UserGate(userId))
            {
                if (!_duels.TryGetValue(duelId, out var session) || session.Mode != DuelMode.Challenge)
                    return NotFound<DuelSnapshot>();
                lock (session.Gate)
                {
                    if (JoinRefused(session, userId) is { } refused)
                        return refused;
                    if (ActiveFor(userId) is { } current && current.Id != duelId)
                        Remove(current);   // trening albo niepodjęte wyzwanie ustępuje
                    session.PlayerTwoId = userId;
                    session.PlayerTwoAlias = alias;
                    Touch(session);
                    started = true;
                    joined = ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerTwo));
                }
            }
            if (started)
                await CommitGamesAsync(lease);
            return joined;
        }

        // Warunki dołączenia do wyzwania REST (pod blokadą pojedynku). Powtórzone dołączenie — sukces bez zmian stanu.
        private ServiceResult<DuelSnapshot>? JoinRefused(DuelSession session, int userId)
        {
            if (session.PlayerOneId == userId)
                return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Nie możesz dołączyć do własnego pojedynku.", "duel-own");
            if (session.PlayerTwoId == userId)
                return ServiceResult<DuelSnapshot>.Success(SnapshotFor(session, Player.PlayerTwo));   // powtórzone dołączenie
            if (session.PlayerTwoId is not null || session.Finished)
                return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Ten pojedynek ma już dwóch graczy.", "duel-full");
            if (ActiveFor(userId) is { HumanOpponent: true } current && current.Id != session.Id)
                return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
            return null;
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
        // Trwający ma pierwszeństwo (np. rewanż przed pojedynkiem, z którego powstał); zakończony pojedynek, od którego
        // gracz już odszedł ("Wróć do sali"), nie wraca.
        // Pojedynek do pokazania przy stole: trwający, a po zakończeniu — ten, którego gracz nie opuścił. Zakończony pojedynek
        // zastąpiony rewanżem (RematchId) nie jest już bieżący — inaczej po rewanżu stół wracałby do starego wyniku.
        public ServiceResult<DuelSnapshot> GetActive(int userId)
        {
            var session = _duels.Values
                .Where(s => s.SeatOf(userId) is { } seat && !(s.Finished && (s.Left[Index(seat)] || s.RematchId is not null)))
                .OrderBy(s => s.Finished)
                .ThenByDescending(s => s.LastActivity)
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
            // Zawieszony nie gra, więc jego rundy kończą się z czasem karą silnika (Duel.timeoutRound).
            if (await CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<MoveResponse>.From(denied);

            List<DuelEvent> outbox;
            ServiceResult<MoveResponse> response;
            lock (session.Gate)
            {
                if (session.SeatOf(userId) is not { } seat)
                    return NotFound<MoveResponse>();
                if (!session.OpponentJoined)
                    return ServiceResult<MoveResponse>.Fail(ServiceError.Conflict, "Czekamy na przeciwnika.", "duel-waiting-for-opponent");
                if (session.Live && !session.Started && !session.Finished)
                    return ServiceResult<MoveResponse>.Fail(ServiceError.Conflict, "Pojedynek zacznie się, gdy obaj gracze będą gotowi.", "duel-not-started");

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

                // Jedyna podmiana stanu — wewnątrz blokady pojedynku. Pojedynek z człowiekiem: zdarzenia dla obu stron
                // (przeciwnik dostaje tylko "gotowy", nigdy kartę — każdy snapshot powstaje z widoku swojego gracza).
                RoundResult? roundResult = null;
                switch (outcome)
                {
                    case Duel.MoveOutcome.Resolved resolved:
                        outbox = CloseRound(session, resolved.Item1, resolved.Item2);
                        roundResult = DuelMapper.Round(seat, resolved.Item2);
                        break;
                    case Duel.MoveOutcome.Waiting waitingState:
                        session.State = waitingState.Item;
                        Touch(session);
                        outbox = Updated(session);
                        break;
                    default:
                        outbox = new();
                        break;
                }

                var snapshot = SnapshotFor(session, seat);
                response = ServiceResult<MoveResponse>.Success(new MoveResponse(true, roundResult is not null, snapshot.OpponentReady, roundResult, snapshot));
            }

            await PersistResultAsync(session, outbox);
            await PublishAsync(outbox);
            return response;
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
                Duel.viewFor(seat, session.State),
                LiveInfoFor(session, seat));
        }

        private DuelSession? ActiveFor(int userId) =>
            _duels.Values.FirstOrDefault(s => s.SeatOf(userId) is not null && !s.Finished);

        private object UserGate(int userId) => _userGates.GetOrAdd(userId, _ => new object());

        private void Touch(DuelSession session)
        {
            session.LastActivity = _time.GetUtcNow();
            session.Version++;
        }

        // Timery mają tylko pojedynki na żywo, a te usuwa wyłącznie Sweep (bez innej blokady pojedynku) — trening i niepodjęte
        // wyzwanie REST (usuwane także spod blokady innego pojedynku w JoinAsync) znikają bez blokady: bez ryzyka zakleszczenia.
        private void Remove(DuelSession session)
        {
            _duels.TryRemove(session.Id, out _);
            if (session.Live)
                lock (session.Gate)
                    session.StopTimers();
        }

        private static int Index(Player seat) => seat.IsPlayerOne ? 0 : 1;

        // Sprzątanie przy tworzeniu: bezczynne ponad IdleTimeout albo zakończone dłużej niż FinishedRetention (z timerami).
        private void Sweep()
        {
            var now = _time.GetUtcNow();
            foreach (var session in _duels.Values)
            {
                var idle = now - session.LastActivity;
                if (idle > IdleTimeout || (session.Finished && idle > FinishedRetention))
                    Remove(session);
            }
        }

        public void Dispose()
        {
            foreach (var session in _duels.Values)
                lock (session.Gate)
                    session.StopTimers();
        }

        private static ServiceResult<T> NotFound<T>() =>
            ServiceResult<T>.Fail(ServiceError.NotFound, "Nie znaleziono pojedynku.", "duel-not-found");

        private static ServiceResult<DuelSnapshot> Capacity() =>
            ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Przy stole gry nie ma teraz miejsca. Spróbuj za chwilę.", "duel-capacity");
    }
}
