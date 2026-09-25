using System.Threading.RateLimiting;

namespace SansPost.Infrastructure.Security
{
    // Nazwane polityki i sekcje konfiguracji "RateLimiting:<Nazwa>".
    public static class RateLimitPolicies
    {
        // Anonimowe, per IP: login, rejestracja, refresh, logout.
        public const string Auth = "Auth";

        // Publiczne wyszukiwanie (kosztowny ranking FTS): SearchRateLimiter wspólny dla REST i Blazor.
        // Ta sama konfiguracja limituje też propozycje przydomków (middleware, per IP).
        public const string Search = "Search";

        // Zalogowane, per UserId: posty, komentarze, reakcje, zgłoszenia (egzekwowane w WriteGuard).
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

    // Limit wyszukiwania wspólny dla REST (/api/search/*) i wyszukiwarki Blazor — jedna logiczna ochrona, niezależna od
    // tego, czy żądanie przeszło przez middleware HTTP. Klucz: zalogowany → UserId, anonim → adres IP klienta.
    // In-memory per instancja (rozproszony limiter — decyzja w Sprincie 11).
    public sealed class SearchRateLimiter : IDisposable
    {
        private readonly PartitionedRateLimiter<string> _limiter;

        public SearchRateLimiter(RateLimitWindowOptions settings)
        {
            _limiter = PartitionedRateLimiter.Create<string, string>(key =>
                RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = settings.Window,
                    QueueLimit = 0
                }));
        }

        public static string ClientKey(int? userId, string? remoteAddress) =>
            userId is int id ? $"user:{id}" : $"ip:{(string.IsNullOrEmpty(remoteAddress) ? "unknown" : remoteAddress)}";

        public (bool Acquired, TimeSpan? RetryAfter) TryAcquire(string clientKey)
        {
            using var lease = _limiter.AttemptAcquire(clientKey);
            if (lease.IsAcquired)
                return (true, null);

            return (false, lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
        }

        public void Dispose() => _limiter.Dispose();
    }
}
