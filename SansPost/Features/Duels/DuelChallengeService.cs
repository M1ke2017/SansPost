using SansPost.Features.Identity;

namespace SansPost.Features.Duels
{
    // Wyzwania przy stole gry (Sprint 20): zaproszenie → przyjęcie / odrzucenie / wycofanie / wygaśnięcie.
    // Wyzwanie nie tworzy stanu gry — dopiero przyjęcie zakłada pojedynek w GameSessionService (jedyne źródło sesji).
    //   • polityka konta: ta sama co w pojedynkach (GameSessionService.CheckParticipationAsync) — wyzwać i przyjąć może tylko
    //     aktywne konto; odrzucić i wycofać — każdy (to niczego nie tworzy);
    //   • bez spamu: jedno wysłane wyzwanie na gracza naraz, a po odrzuceniu / wycofaniu / wygaśnięciu ta sama para czeka Cooldown;
    //   • wygaśnięcie po ChallengeLifetime — zwykły timer serwera (TimeProvider), bez Hangfire;
    //   • adresat wskazany nieprzezroczystym uchwytem (DuelConnectionRegistry), nie UserId.
    // W pamięci procesu (jedna instancja); jedna blokada — operacje krótkie, zdarzenia wysyłane po jej zwolnieniu.
    public sealed class DuelChallengeService : IDisposable
    {
        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
        public const int MaxListedPlayers = 50;

        private sealed class Challenge
        {
            public required Guid Id { get; init; }
            public required int ChallengerId { get; init; }
            public required string ChallengerAlias { get; init; }
            public required int TargetId { get; init; }
            public required string TargetAlias { get; init; }
            public required DateTimeOffset ExpiresAt { get; init; }
            public ITimer? Timer { get; set; }
        }

        private readonly object _gate = new();
        private readonly Dictionary<Guid, Challenge> _pending = new();
        private readonly Dictionary<(int Challenger, int Target), DateTimeOffset> _cooldowns = new();
        private readonly TimeProvider _time;
        private readonly GameSessionService _sessions;
        private readonly DuelConnectionRegistry _registry;
        private readonly IServiceScopeFactory _scopes;
        private readonly IDuelNotifier _notifier;
        private readonly TimeSpan _lifetime;
        private readonly ILogger<DuelChallengeService> _logger;

        public DuelChallengeService(TimeProvider time, GameSessionService sessions, DuelConnectionRegistry registry, IServiceScopeFactory scopes,
            IDuelNotifier notifier, Microsoft.Extensions.Options.IOptions<DuelOptions> options, ILogger<DuelChallengeService> logger)
        {
            _time = time;
            _sessions = sessions;
            _registry = registry;
            _scopes = scopes;
            _notifier = notifier;
            _lifetime = options.Value.ChallengeLifetime;
            _logger = logger;
        }

        public int PendingCount
        {
            get { lock (_gate) return _pending.Count; }
        }

        // Gracze dostępni do pojedynku: przy stole (online), aktywne konto, nie w trakcie pojedynku, bez samego pytającego.
        public async Task<IReadOnlyList<OnlinePlayer>> AvailablePlayersAsync(int userId, CancellationToken cancellationToken = default)
        {
            var online = _registry.Online().Where(p => p.UserId != userId && !_sessions.IsBusy(p.UserId)).ToList();
            if (online.Count == 0)
                return Array.Empty<OnlinePlayer>();

            await using var scope = _scopes.CreateAsyncScope();
            var active = await scope.ServiceProvider.GetRequiredService<WriteGuard>()
                .ActiveAmongAsync(online.Select(p => p.UserId).ToList(), cancellationToken);
            return online
                .Where(p => active.Contains(p.UserId))
                .OrderBy(p => p.Alias, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxListedPlayers)
                .Select(p => new OnlinePlayer(p.Handle, p.Alias))
                .ToList();
        }

        public async Task<ServiceResult<ChallengeInfo>> ChallengeAsync(int userId, string alias, string? playerHandle, CancellationToken cancellationToken = default)
        {
            if (await _sessions.CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<ChallengeInfo>.From(denied);
            if (_registry.UserOf(playerHandle) is not { } targetId)
                return Unavailable();
            if (targetId == userId)
                return ServiceResult<ChallengeInfo>.Fail(ServiceError.Validation, "Nie możesz wyzwać samego siebie.", "challenge-self");
            if (await _sessions.CheckParticipationAsync(targetId, cancellationToken) is not null || _registry.AliasOf(targetId) is not { } targetAlias)
                return Unavailable();   // bez ujawniania, dlaczego gracz nie może grać

            var events = new List<DuelEvent>();
            ChallengeInfo info;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                if (_sessions.IsBusy(userId))
                    return ServiceResult<ChallengeInfo>.Fail(ServiceError.Conflict, "Masz trwający pojedynek.", "duel-already-active");
                if (_sessions.IsBusy(targetId))
                    return ServiceResult<ChallengeInfo>.Fail(ServiceError.Conflict, "Ten gracz jest w trakcie pojedynku.", "player-busy");
                if (_pending.Values.Any(c => c.ChallengerId == userId))
                    return ServiceResult<ChallengeInfo>.Fail(ServiceError.Conflict, "Masz już wysłane wyzwanie — poczekaj na odpowiedź albo je wycofaj.", "challenge-already-pending");
                if (_pending.Values.Any(c => c.ChallengerId == targetId && c.TargetId == userId))
                    return ServiceResult<ChallengeInfo>.Fail(ServiceError.Conflict, "Ten gracz już Cię wyzwał — możesz przyjąć jego wyzwanie.", "challenge-crossed");

                foreach (var expired in _cooldowns.Where(c => c.Value <= now).Select(c => c.Key).ToList())
                    _cooldowns.Remove(expired);
                if (_cooldowns.TryGetValue((userId, targetId), out var until))
                    return ServiceResult<ChallengeInfo>.Fail(ServiceError.RateLimited, "Ponowne wyzwanie tego gracza będzie możliwe za chwilę.", "challenge-cooldown", until - now);

                var challenge = new Challenge
                {
                    Id = Guid.NewGuid(),
                    ChallengerId = userId,
                    ChallengerAlias = alias,
                    TargetId = targetId,
                    TargetAlias = targetAlias,
                    ExpiresAt = now + _lifetime
                };
                challenge.Timer = _time.CreateTimer(_ => _ = ExpireAsync(challenge.Id), null, _lifetime, Timeout.InfiniteTimeSpan);
                _pending[challenge.Id] = challenge;

                info = Info(challenge, userId, "pending");
                events.Add(new DuelEvent(targetId, DuelEvents.ChallengeReceived,
                    new ChallengeReceived(challenge.Id.ToString("N"), alias, challenge.ExpiresAt, Remaining(challenge))));
                events.Add(new DuelEvent(userId, DuelEvents.ChallengeUpdated, info));
            }

            await PublishAsync(events);
            return ServiceResult<ChallengeInfo>.Success(info);
        }

        public async Task<ServiceResult<DuelSnapshot>> AcceptAsync(int userId, string? challengeId, CancellationToken cancellationToken = default)
        {
            if (Find(challengeId, c => c.TargetId == userId) is null)
                return NotFound<DuelSnapshot>();
            // Zawieszony nie przyjmie — wyzwanie zostaje nietknięte (może je odrzucić albo poczekać, aż wygaśnie).
            if (await _sessions.CheckParticipationAsync(userId, cancellationToken) is { } denied)
                return ServiceResult<DuelSnapshot>.From(denied);

            Challenge? challenge;
            lock (_gate)
            {
                challenge = Find(challengeId, c => c.TargetId == userId);
                if (challenge is null)
                    return NotFound<DuelSnapshot>();   // w międzyczasie wygasło albo zostało wycofane
                End(challenge, cooldown: false);
            }

            if (!_registry.IsOnline(challenge.ChallengerId))
            {
                await PublishAsync(Both(challenge, "unavailable"));
                return ServiceResult<DuelSnapshot>.Fail(ServiceError.Conflict, "Wyzywający odszedł od stołu.", "player-unavailable");
            }

            var started = await _sessions.StartLiveAsync(challenge.ChallengerId, challenge.ChallengerAlias, challenge.TargetId, challenge.TargetAlias, cancellationToken);
            var events = Both(challenge, started.Succeeded ? "accepted" : "unavailable");
            if (started.Succeeded)
            {
                // Pozostałe wyzwania obu graczy tracą sens — są w pojedynku.
                lock (_gate)
                {
                    foreach (var other in _pending.Values.Where(c => Involves(c, challenge.ChallengerId) || Involves(c, challenge.TargetId)).ToList())
                    {
                        End(other, cooldown: false);
                        events.AddRange(Both(other, "unavailable"));
                    }
                }
            }

            await PublishAsync(events);
            return started;
        }

        public async Task<ServiceResult<ChallengeInfo>> RejectAsync(int userId, string? challengeId)
        {
            Challenge? challenge;
            lock (_gate)
            {
                challenge = Find(challengeId, c => c.TargetId == userId);
                if (challenge is null)
                    return NotFound<ChallengeInfo>();
                End(challenge, cooldown: true);
            }

            await PublishAsync(Both(challenge, "rejected"));
            return ServiceResult<ChallengeInfo>.Success(Info(challenge, userId, "rejected"));
        }

        public async Task<ServiceResult<ChallengeInfo>> CancelAsync(int userId)
        {
            Challenge? challenge;
            lock (_gate)
            {
                challenge = _pending.Values.FirstOrDefault(c => c.ChallengerId == userId);
                if (challenge is null)
                    return NotFound<ChallengeInfo>();
                End(challenge, cooldown: true);
            }

            await PublishAsync(Both(challenge, "cancelled"));
            return ServiceResult<ChallengeInfo>.Success(Info(challenge, userId, "cancelled"));
        }

        // Oczekujące wyzwania gracza (przychodzące i wysłane) — do stanu po wejściu / ponownym połączeniu.
        public IReadOnlyList<ChallengeInfo> PendingFor(int userId)
        {
            lock (_gate)
            {
                return _pending.Values
                    .Where(c => Involves(c, userId))
                    .OrderBy(c => c.ExpiresAt)
                    .Select(c => Info(c, userId, "pending"))
                    .ToList();
            }
        }

        private async Task ExpireAsync(Guid id)
        {
            try
            {
                Challenge? challenge;
                lock (_gate)
                {
                    if (!_pending.TryGetValue(id, out challenge))
                        return;
                    End(challenge, cooldown: true);
                }

                await PublishAsync(Both(challenge, "expired"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zakończyć wygasłego wyzwania.");
            }
        }

        // Pod _gate: wyzwanie znika z oczekujących, timer się zatrzymuje.
        private void End(Challenge challenge, bool cooldown)
        {
            _pending.Remove(challenge.Id);
            challenge.Timer?.Dispose();
            challenge.Timer = null;
            if (cooldown)
                _cooldowns[(challenge.ChallengerId, challenge.TargetId)] = _time.GetUtcNow() + Cooldown;
        }

        private Challenge? Find(string? challengeId, Func<Challenge, bool> belongs)
        {
            lock (_gate)
                return Guid.TryParse(challengeId, out var id) && _pending.TryGetValue(id, out var challenge) && belongs(challenge) ? challenge : null;
        }

        private static bool Involves(Challenge challenge, int userId) => challenge.ChallengerId == userId || challenge.TargetId == userId;

        private List<DuelEvent> Both(Challenge challenge, string status) => new()
        {
            new DuelEvent(challenge.ChallengerId, DuelEvents.ChallengeUpdated, Info(challenge, challenge.ChallengerId, status)),
            new DuelEvent(challenge.TargetId, DuelEvents.ChallengeUpdated, Info(challenge, challenge.TargetId, status))
        };

        private ChallengeInfo Info(Challenge challenge, int viewerId, string status)
        {
            var outgoing = challenge.ChallengerId == viewerId;
            return new ChallengeInfo(challenge.Id.ToString("N"), outgoing ? "outgoing" : "incoming",
                outgoing ? challenge.TargetAlias : challenge.ChallengerAlias, challenge.ExpiresAt, Remaining(challenge), status);
        }

        private int Remaining(Challenge challenge) => (int)Math.Max(0, (challenge.ExpiresAt - _time.GetUtcNow()).TotalMilliseconds);

        private async Task PublishAsync(List<DuelEvent> events)
        {
            try
            {
                await _notifier.PublishAsync(events);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udało się wysłać zdarzeń wyzwania.");
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var challenge in _pending.Values)
                    challenge.Timer?.Dispose();
                _pending.Clear();
            }
        }

        private static ServiceResult<ChallengeInfo> Unavailable() =>
            ServiceResult<ChallengeInfo>.Fail(ServiceError.Conflict, "Ten gracz nie jest teraz dostępny.", "player-unavailable");

        private static ServiceResult<T> NotFound<T>() =>
            ServiceResult<T>.Fail(ServiceError.NotFound, "Wyzwanie wygasło albo zostało wycofane.", "challenge-not-found");
    }
}
