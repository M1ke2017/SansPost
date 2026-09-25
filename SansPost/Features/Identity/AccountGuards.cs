using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost.Features.Identity
{
    // Czy principal (cookie lub JWT) nadal odpowiada aktualnemu stanowi konta.
    public class AuthStateValidator
    {
        private readonly ApplicationDbContext _context;

        public AuthStateValidator(ApplicationDbContext context)
        {
            _context = context;
        }

        // Jedno zapytanie po PK. Odrzuca: brak konta, ban, nieaktualną wersję (zmiana roli/statusu).
        // Suspended pozostaje uwierzytelniony (może czytać) — zapisy blokuje WriteGuard.
        public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            if (principal.GetUserId() is not int userId
                || !int.TryParse(principal.FindFirstValue(SansPostClaimTypes.AuthVersion), out var version))
            {
                return false;
            }

            var state = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Status, u.AuthVersion })
                .FirstOrDefaultAsync(cancellationToken);

            return state is not null && state.Status != AccountStatus.Banned && state.AuthVersion == version;
        }
    }

    // Serwerowa brama każdej operacji zapisu (post, komentarz, reakcja, zgłoszenie) — dla REST i Blazor jednakowo.
    // 1) limit zapisów per użytkownik (partycja = UserId, nie IP), 2) konto musi być Active.
    public class WriteGuard
    {
        private readonly ApplicationDbContext _context;
        private readonly UserWriteRateLimiter _rateLimiter;

        public WriteGuard(ApplicationDbContext context, UserWriteRateLimiter rateLimiter)
        {
            _context = context;
            _rateLimiter = rateLimiter;
        }

        // null = zapis dozwolony.
        public async Task<ServiceResult?> CheckAsync(int actorUserId, CancellationToken cancellationToken = default)
        {
            // Najpierw tani limit w pamięci — zalew requestów nie trafia do bazy.
            var (acquired, retryAfter) = _rateLimiter.TryAcquire(actorUserId);
            if (!acquired)
            {
                SansPost.Infrastructure.Hosting.SansPostTelemetry.RateLimitRejections.Add(1, new KeyValuePair<string, object?>("policy", "Writes"));
                return ServiceResult.Fail(ServiceError.RateLimited,
                    "Zbyt wiele operacji w krótkim czasie. Spróbuj ponownie za chwilę.", "write-rate-limited", retryAfter);
            }

            var status = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == actorUserId)
                .Select(u => (AccountStatus?)u.Status)
                .FirstOrDefaultAsync(cancellationToken);

            return status switch
            {
                AccountStatus.Active => null,
                AccountStatus.Suspended => ServiceResult.Fail(ServiceError.Forbidden,
                    "Twoje konto jest zawieszone — możesz przeglądać treści, ale nie możesz publikować.", "account-suspended"),
                _ => ServiceResult.Fail(ServiceError.Forbidden, "Konto nie może wykonywać tej operacji.", "account-inactive")
            };
        }
    }
}
