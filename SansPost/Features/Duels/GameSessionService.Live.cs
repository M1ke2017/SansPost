using SansPost.Game.Core;

namespace SansPost.Features.Duels
{
    // Pojedynek na żywo (Sprint 20) — cykl życia sesji po stronie C#. Reguły i wynik zostają w silniku F#:
    //   • gotowość: pojedynek powstaje z przyjętego wyzwania, runda 1 startuje dopiero, gdy obaj klikną "GOTOWY";
    //   • zegar rundy: serwer ustala termin (RoundDeadline); po nim silnik liczy karę (Duel.timeoutRound) — bez losowania;
    //   • rozłączenie: gracz bez żadnego połączenia dostaje okno powrotu (DisconnectGrace), zegar rundy stoi, karty zostają;
    //     po powrocie runda startuje od nowa z pełnym czasem, bez powrotu — oddanie pojedynku (Duel.forfeit);
    //   • poddanie: to samo Duel.forfeit (powód "surrender" zna tylko serwer);
    //   • rewanż: nowy pojedynek tych samych graczy, gdy obaj go chcą — stary GameState zostaje nietknięty.
    // Timery (TimeProvider) niosą token — spóźniony zegar starej rundy albo anulowanego okna powrotu nic nie zmienia.
    // Zdarzenia powstają pod blokadą pojedynku (każdy snapshot z widoku swojego gracza), wysyłane są po jej zwolnieniu.
    public sealed partial class GameSessionService
    {
        // Zajęty = trwający pojedynek z człowiekiem (trening nie blokuje wyzwań).
        public bool IsBusy(int userId) =>
            _duels.Values.Any(s => s.HumanOpponent && !s.Finished && s.SeatOf(userId) is not null);

        // Przyjęte wyzwanie → nowy pojedynek na żywo. Polityka konta dla obu: przyjmujący dostaje swój komunikat,
        // wyzywający (mógł zostać zawieszony po wysłaniu wyzwania) — "niedostępny", bez ujawniania powodu.
        public async Task<ServiceResult<DuelSnapshot>> StartLiveAsync(int challengerId, string challengerAlias, int accepterId, string accepterAlias,
            CancellationToken cancellationToken = default)
        {
            if (await CheckParticipationAsync(accepterId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);
            if (await CheckParticipationAsync(challengerId, cancellationToken) is not null)
                return Unavailable<DuelSnapshot>();

            var (first, second) = challengerId < accepterId ? (challengerId, accepterId) : (accepterId, challengerId);
            List<DuelEvent> outbox;
            DuelSnapshot snapshot;
            lock (UserGate(first))
            lock (UserGate(second))
            {
                Sweep();
                if (IsBusy(accepterId))
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
                if (IsBusy(challengerId))
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Przeciwnik jest w trakcie pojedynku.", "player-busy");
                if (_duels.Count >= MaxSessions)
                    return Capacity();

                // Trening albo niepodjęte wyzwanie REST któregoś z graczy ustępuje pojedynkowi na żywo.
                foreach (var old in _duels.Values.Where(s => !s.Finished && (s.SeatOf(challengerId) is not null || s.SeatOf(accepterId) is not null)).ToList())
                    Remove(old);

                var session = new DuelSession
                {
                    Id = Guid.NewGuid(),
                    Mode = DuelMode.Live,
                    PlayerOneId = challengerId,
                    PlayerOneAlias = challengerAlias,
                    PlayerTwoId = accepterId,
                    PlayerTwoAlias = accepterAlias,
                    State = Duel.createStandard(),
                    LastActivity = _time.GetUtcNow()
                };
                _duels[session.Id] = session;
                lock (session.Gate)
                {
                    outbox = Updated(session);
                    snapshot = SnapshotFor(session, Player.PlayerTwo);
                }
            }

            outbox.Add(DuelEvent.Presence);
            await PublishAsync(outbox);
            return ServiceResult<DuelSnapshot>.Success(snapshot);
        }

        // "GOTOWY": gdy obaj — runda 1 z zegarem. Powtórzone kliknięcie niczego nie zmienia.
        public async Task<ServiceResult<DuelSnapshot>> SetReadyAsync(Guid duelId, int userId, CancellationToken cancellationToken = default)
        {
            if (!_duels.TryGetValue(duelId, out var session) || session.SeatOf(userId) is null)
                return NotFound<DuelSnapshot>();
            if (await CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            List<DuelEvent> outbox = new();
            DuelSnapshot snapshot;
            lock (session.Gate)
            {
                var seat = session.SeatOf(userId)!;
                if (!session.Live)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Ten pojedynek nie ma fazy gotowości.", "duel-not-live");
                if (session.Finished)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Pojedynek jest już zakończony.", "duel-finished");

                if (!session.Started && !session.StartReady[Index(seat)])
                {
                    session.StartReady[Index(seat)] = true;
                    Touch(session);
                    if (session.StartReady[0] && session.StartReady[1])
                    {
                        session.Started = true;
                        StartRound(session);
                        outbox = RoundStartedEvents(session);
                    }
                    else
                    {
                        outbox = Updated(session);
                    }
                }

                snapshot = SnapshotFor(session, seat);
            }

            await PublishAsync(outbox);
            return ServiceResult<DuelSnapshot>.Success(snapshot);
        }

        // Świadome oddanie pojedynku. Bez polityki konta: oddanie tylko kończy własny udział (korzysta przeciwnik),
        // więc zawieszony też może się poddać zamiast przegrywać rundy na czas.
        public async Task<ServiceResult<DuelSnapshot>> SurrenderAsync(Guid duelId, int userId)
        {
            if (!_duels.TryGetValue(duelId, out var session) || session.SeatOf(userId) is null)
                return NotFound<DuelSnapshot>();

            List<DuelEvent> outbox;
            DuelSnapshot snapshot;
            lock (session.Gate)
            {
                var seat = session.SeatOf(userId)!;
                if (session.Finished)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Pojedynek jest już zakończony.", "duel-finished");
                outbox = Forfeit(session, seat, "surrender");
                snapshot = SnapshotFor(session, seat);
            }

            await PublishAsync(outbox);
            return ServiceResult<DuelSnapshot>.Success(snapshot);
        }

        // Rewanż: każdy z graczy prosi osobno; drugi głos tworzy nowy pojedynek (dokładnie raz — RematchClaimed pod blokadą).
        public async Task<ServiceResult<DuelSnapshot>> RequestRematchAsync(Guid duelId, int userId, CancellationToken cancellationToken = default)
        {
            if (!_duels.TryGetValue(duelId, out var session) || session.SeatOf(userId) is null)
                return NotFound<DuelSnapshot>();
            if (await CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            List<DuelEvent> outbox = new();
            DuelSnapshot snapshot;
            bool start = false;
            Guid? existing = null;
            lock (session.Gate)
            {
                var seat = session.SeatOf(userId)!;
                var (mine, theirs) = (Index(seat), 1 - Index(seat));
                if (!session.Live)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Rewanż dotyczy pojedynku przy stole gry.", "duel-not-live");
                if (!session.Finished)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Pojedynek jeszcze trwa.", "duel-in-progress");
                existing = session.RematchId;
                if (existing is null)
                {
                    if (session.Left[theirs])
                        return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Przeciwnik opuścił już stół.", "rematch-opponent-left");
                    if (!session.RematchWanted[mine])
                    {
                        session.RematchWanted[mine] = true;
                        Touch(session);
                    }

                    if (session.RematchWanted[theirs] && !session.RematchClaimed)
                    {
                        session.RematchClaimed = true;
                        start = true;
                    }
                    else
                    {
                        outbox = Updated(session);
                    }
                }

                snapshot = SnapshotFor(session, seat);
            }

            if (existing is { } rematchId)
                return Get(rematchId, userId);   // rewanż już trwa (np. druga karta przeglądarki)

            if (!start)
            {
                await PublishAsync(outbox);
                return ServiceResult<DuelSnapshot>.Success(snapshot);
            }

            var opponentSeat = session.SeatOf(userId)!.IsPlayerOne ? Player.PlayerTwo : Player.PlayerOne;
            var opponentId = session.UserAt(opponentSeat)!.Value;
            var opponentAlias = opponentSeat.IsPlayerOne ? session.PlayerOneAlias : session.PlayerTwoAlias!;
            var myAlias = opponentSeat.IsPlayerOne ? session.PlayerTwoAlias! : session.PlayerOneAlias;
            var created = await StartLiveAsync(opponentId, opponentAlias, userId, myAlias, cancellationToken);

            lock (session.Gate)
            {
                if (created.Succeeded)
                {
                    session.RematchId = Guid.Parse(created.Value!.DuelId);
                }
                else
                {
                    session.RematchClaimed = false;
                    Array.Clear(session.RematchWanted);
                }
                Touch(session);
                outbox = Updated(session);
            }

            await PublishAsync(outbox);
            return created;
        }

        // "Wróć do sali" po zakończeniu: gracz odchodzi od zakończonego pojedynku (koniec propozycji rewanżu).
        public async Task<ServiceResult<DuelSnapshot>> LeaveAsync(Guid duelId, int userId)
        {
            if (!_duels.TryGetValue(duelId, out var session) || session.SeatOf(userId) is null)
                return NotFound<DuelSnapshot>();

            List<DuelEvent> outbox = new();
            DuelSnapshot snapshot;
            lock (session.Gate)
            {
                var seat = session.SeatOf(userId)!;
                if (!session.Finished)
                    return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Pojedynek jeszcze trwa — możesz go oddać.", "duel-in-progress");
                if (!session.Left[Index(seat)])
                {
                    session.Left[Index(seat)] = true;
                    session.RematchWanted[Index(seat)] = false;
                    Touch(session);
                    outbox = Updated(session);
                }
                snapshot = SnapshotFor(session, seat);
            }

            await PublishAsync(outbox);
            return ServiceResult<DuelSnapshot>.Success(snapshot);
        }

        // Ostatnie połączenie gracza zamknięte (DuelHub): okno powrotu, zegar rundy stoi, ukryte karty zostają.
        public async Task PlayerOfflineAsync(int userId)
        {
            var outbox = new List<DuelEvent>();
            foreach (var session in LiveSessionsOf(userId))
            {
                lock (session.Gate)
                {
                    if (session.Finished || session.SeatOf(userId) is not { } seat || session.GraceUntil[Index(seat)] is not null)
                        continue;
                    var index = Index(seat);
                    var grace = _options.DisconnectGrace;
                    session.GraceUntil[index] = _time.GetUtcNow() + grace;
                    var token = ++session.GraceTokens[index];
                    var id = session.Id;
                    session.GraceTimers[index] = _time.CreateTimer(_ => _ = OnGraceExpiredAsync(id, seat, token), null, grace, Timeout.InfiniteTimeSpan);
                    StartRound(session);   // wstrzymanie: przy Paused zegar rundy znika
                    Touch(session);
                    outbox.AddRange(Updated(session));
                }
            }

            await PublishAsync(outbox);
        }

        // Pierwsze połączenie gracza (powrót, odświeżenie, druga karta): koniec okna powrotu, runda od nowa z pełnym czasem.
        public async Task PlayerOnlineAsync(int userId)
        {
            var outbox = new List<DuelEvent>();
            foreach (var session in LiveSessionsOf(userId))
            {
                lock (session.Gate)
                {
                    if (session.Finished || session.SeatOf(userId) is not { } seat || session.GraceUntil[Index(seat)] is null)
                        continue;
                    var index = Index(seat);
                    session.GraceTimers[index]?.Dispose();
                    session.GraceTimers[index] = null;
                    session.GraceUntil[index] = null;
                    session.GraceTokens[index]++;
                    Touch(session);
                    StartRound(session);
                    outbox.AddRange(session.RoundDeadline is not null ? RoundStartedEvents(session) : Updated(session));
                }
            }

            await PublishAsync(outbox);
        }

        private IEnumerable<DuelSession> LiveSessionsOf(int userId) =>
            _duels.Values.Where(s => s.Live && !s.Finished && s.SeatOf(userId) is not null).ToList();

        private async Task OnGraceExpiredAsync(Guid duelId, Player seat, long token)
        {
            try
            {
                if (!_duels.TryGetValue(duelId, out var session))
                    return;
                List<DuelEvent> outbox;
                lock (session.Gate)
                {
                    var index = Index(seat);
                    if (session.Finished || session.GraceUntil[index] is null || session.GraceTokens[index] != token)
                        return;
                    outbox = Forfeit(session, seat, "disconnect");
                }

                await PublishAsync(outbox);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zakończyć pojedynku po oknie powrotu.");
            }
        }

        private async Task OnRoundTimeoutAsync(Guid duelId, long token)
        {
            try
            {
                if (!_duels.TryGetValue(duelId, out var session))
                    return;
                List<DuelEvent> outbox;
                lock (session.Gate)
                {
                    if (session.Finished || session.RoundDeadline is null || session.RoundToken != token)
                        return;
                    var timedOut = Duel.timeoutRound(session.State.Round, session.State);
                    if (timedOut.IsError)
                    {
                        _logger.LogWarning("Koniec czasu rundy odrzucony przez silnik: {Error}", timedOut.ErrorValue);
                        return;
                    }
                    outbox = CloseRound(session, timedOut.ResultValue.Item1, timedOut.ResultValue.Item2);
                }

                await PublishAsync(outbox);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zamknąć rundy po końcu czasu.");
            }
        }

        // Wspólne zamknięcie rundy (druga karta albo koniec czasu): nowy stan, zegar kolejnej rundy albo koniec pojedynku.
        private List<DuelEvent> CloseRound(DuelSession session, GameState state, RoundRecord record)
        {
            session.State = state;
            Touch(session);
            if (session.Finished)
                session.StopTimers();
            else
                StartRound(session);

            var events = RoundResolvedEvents(session, record);
            if (session.Finished)
            {
                if (session.HumanOpponent)
                    events.Add(DuelEvent.Presence);
            }
            else
            {
                events.AddRange(RoundStartedEvents(session));
            }
            return events;
        }

        private List<DuelEvent> Forfeit(DuelSession session, Player seat, string ending)
        {
            var forfeited = Duel.forfeit(seat, session.State);
            if (forfeited.IsError)
                return new();
            session.State = forfeited.ResultValue;
            session.Ending = ending;
            session.StopTimers();
            Touch(session);
            var events = Updated(session);
            if (session.HumanOpponent)
                events.Add(DuelEvent.Presence);
            return events;
        }

        // Zegar bieżącej rundy (od nowa). Bez zegara: nie na żywo, przed startem, po końcu albo gdy ktoś jest poza stołem.
        private void StartRound(DuelSession session)
        {
            session.RoundTimer?.Dispose();
            session.RoundTimer = null;
            session.RoundDeadline = null;
            if (!session.Live || !session.Started || session.Finished || session.Paused)
                return;

            var duration = _options.RoundDuration;
            session.RoundDeadline = _time.GetUtcNow() + duration;
            var token = ++session.RoundToken;
            var id = session.Id;
            session.RoundTimer = _time.CreateTimer(_ => _ = OnRoundTimeoutAsync(id, token), null, duration, Timeout.InfiniteTimeSpan);
        }

        private LiveInfo LiveInfoFor(DuelSession session, Player seat)
        {
            if (!session.Live)
                return new LiveInfo(session.Version, Ending: session.Ending);

            var (mine, theirs) = (Index(seat), 1 - Index(seat));
            var now = _time.GetUtcNow();
            int? Remaining(DateTimeOffset? until) => until is { } at ? (int)Math.Max(0, (at - now).TotalMilliseconds) : null;
            var rematch = session.RematchId is not null ? "started" : session.RematchWanted[mine] ? "you" : session.RematchWanted[theirs] ? "opponent" : "none";

            return new LiveInfo(
                session.Version,
                ReadyCheck: !session.Started,
                YouStartReady: session.StartReady[mine],
                OpponentStartReady: session.StartReady[theirs],
                OpponentGraceMs: Remaining(session.GraceUntil[theirs]),
                RoundDeadline: session.RoundDeadline,
                RoundRemainingMs: Remaining(session.RoundDeadline),
                Ending: session.Ending,
                Rematch: rematch,
                RematchDuelId: session.RematchId?.ToString("N"),
                OpponentLeft: session.Left[theirs]);
        }

        // Adresaci zdarzeń: gracze-ludzie pojedynku (trening — tylko gracz, np. druga karta przeglądarki).
        private static IEnumerable<Player> Seats(DuelSession session) =>
            session.HumanOpponent ? new[] { Player.PlayerOne, Player.PlayerTwo } : new[] { Player.PlayerOne };

        private List<DuelEvent> Updated(DuelSession session) =>
            Seats(session).Select(seat => new DuelEvent(session.UserAt(seat), DuelEvents.DuelUpdated, SnapshotFor(session, seat))).ToList();

        private List<DuelEvent> RoundStartedEvents(DuelSession session)
        {
            if (session.RoundDeadline is not { } deadline)
                return Updated(session);
            return Seats(session).Select(seat =>
            {
                var snapshot = SnapshotFor(session, seat);
                return new DuelEvent(session.UserAt(seat), DuelEvents.RoundStarted,
                    new RoundStartedEvent(snapshot.DuelId, snapshot.Round, deadline, snapshot.RoundRemainingMs ?? 0, snapshot));
            }).ToList();
        }

        private List<DuelEvent> RoundResolvedEvents(DuelSession session, RoundRecord record) =>
            Seats(session).Select(seat =>
            {
                var snapshot = SnapshotFor(session, seat);
                return new DuelEvent(session.UserAt(seat), DuelEvents.RoundResolved,
                    new RoundResolvedEvent(snapshot.DuelId, record.Round, DuelMapper.Round(seat, record), snapshot.Result, snapshot));
            }).ToList();

        private async Task PublishAsync(List<DuelEvent> events)
        {
            if (events.Count == 0)
                return;
            try
            {
                await _notifier.PublishAsync(events);
            }
            catch (Exception ex)
            {
                // Stan jest już zapisany — klient i tak dostanie go przy RequestState po ponownym połączeniu.
                _logger.LogWarning(ex, "Nie udało się wysłać zdarzeń pojedynku.");
            }
        }

        private static ServiceResult<T> Unavailable<T>() =>
            ServiceResult<T>.Fail(ServiceError.Conflict, "Ten gracz nie może teraz zagrać.", "player-unavailable");
    }
}
