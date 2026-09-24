using System.Threading.RateLimiting;

namespace SansPost.Infrastructure.Security
{
    // Nazwane polityki i sekcje konfiguracji "RateLimiting:<Nazwa>".
    public static class RateLimitPolicies
    {
        // Anonimowe, per IP: login, rejestracja, refresh, logout.
        public const string Auth = "Auth";

        // Anonimowe, per IP: publiczne wyszukiwanie (kosztowny ranking FTS).
        public const string Search = "Search";

        // Zalogowane, per UserId: posty, komentarze, reakcje, zgłoszenia (egzekwowane w IWriteGuard).
        public const string Writes = "Writes";
    }

    public sealed class RateLimitWindowOptions
    {
        public int PermitLimit { get; set; } = 10;
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    }

    // Limit zapisów per użytkownik, wspólny dla REST i Blazor (Blazor woła serwisy bezpośrednio, z pominięciem
    // middleware HTTP). Partycja = UserId — użytkownicy za tym samym NAT nie dzielą limitu.
    // In-memory per instancja: przy wielu instancjach limit jest per instancja (rozproszony limiter — poza zakresem).
    public sealed class UserWriteRateLimiter : IDisposable
    {
        private readonly PartitionedRateLimiter<int> _limiter;

        public UserWriteRateLimiter(RateLimitWindowOptions settings)
        {
            _limiter = PartitionedRateLimiter.Create<int, int>(userId =>
                RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = settings.Window,
                    QueueLimit = 0
                }));
        }

        public (bool Acquired, TimeSpan? RetryAfter) TryAcquire(int userId)
        {
            using var lease = _limiter.AttemptAcquire(userId);
            if (lease.IsAcquired)
                return (true, null);

            return (false, lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
        }

        public void Dispose() => _limiter.Dispose();
    }
}
