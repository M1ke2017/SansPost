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

    // Serwerowa brama aktywnych działań — dla REST, Blazor (i przyszłego huba) jednakowo:
    //   CheckAsync — zapis treści (post, komentarz, reakcja, zgłoszenie): limit zapisów per użytkownik (partycja = UserId,
    //                nie IP), potem polityka konta;
    //   CheckActiveAccountAsync — sama polityka konta, jedno źródło prawdy dla każdej aktywnej funkcji społecznościowej
    //                (także pojedynków): działa tylko konto Active; Suspended może przeglądać, Banned w ogóle nie ma sesji.
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

            return await CheckActiveAccountAsync(actorUserId, cancellationToken);
        }

        // null = konto może wykonywać aktywne działania (Active). Zawsze aktualny stan z bazy (jeden odczyt po PK) —
        // zawieszenie działa od następnej operacji, bez czekania na wygaśnięcie sesji.
        public async Task<ServiceResult?> CheckActiveAccountAsync(int actorUserId, CancellationToken cancellationToken = default)
        {
            var status = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == actorUserId)
                .Select(u => (AccountStatus?)u.Status)
                .FirstOrDefaultAsync(cancellationToken);

            return status switch
            {
                AccountStatus.Active => null,
                AccountStatus.Suspended => ServiceResult.Fail(ServiceError.Forbidden,
                    "Twoje konto jest zawieszone — możesz przeglądać, ale nie możesz publikować ani brać udziału w pojedynkach.", "account-suspended"),
                _ => ServiceResult.Fail(ServiceError.Forbidden, "Konto nie może wykonywać tej operacji.", "account-inactive")
            };
        }

        // Ta sama reguła dla wielu kont naraz (jedno zapytanie): które z podanych mogą działać — np. lista graczy
        // dostępnych przy stole gry, żeby nie proponować pojedynku komuś, kto i tak nie może go przyjąć.
        public async Task<IReadOnlySet<int>> ActiveAmongAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
        {
            if (userIds.Count == 0)
                return new HashSet<int>();
            var active = await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && u.Status == AccountStatus.Active)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            return active.ToHashSet();
        }
    }
}
