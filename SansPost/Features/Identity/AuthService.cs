using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    // Wspólna logika Identity dla obu klientów: Blazor (cookie) i REST (JWT).
    public interface IAuthService
    {
        Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

        // null = nieprawidłowe dane logowania lub konto zablokowane (bez rozróżnienia przyczyny).
        Task<AuthenticatedUser?> AuthenticateAsync(string? email, string? password, CancellationToken cancellationToken = default);

        // Tylko informacja tak/nie — bez liczby kont.
        Task<bool> IsRegistrationAvailableAsync(CancellationToken cancellationToken = default);

        // Tabliczka "Zajęte miejsca" przy wejściu (Final Visual Polish): zajęte publiczne konta / limit wersji demo.
        // Tylko dla UI (Blazor, po stronie serwera) — bez endpointu REST.
        Task<RegistrationCapacity> GetCapacityAsync(CancellationToken cancellationToken = default);
    }

    public sealed record RegistrationCapacity(int Used, int Limit);

    public class AuthService : IAuthService
    {
        public const string CapacityReachedCode = "registration-capacity-reached";
        public const string RegistrationDisabledCode = "registration-disabled";
        public const string IdentityTakenCode = "registration-identity-taken";
        public const string AliasTakenCode = "alias-taken";
        public const string AliasNotCuratedCode = "alias-not-curated";
        public const string AliasPoolExhaustedCode = "alias-pool-exhausted";

        private const string DuplicateIdentityMessage = "Nie można utworzyć konta z podanym emailem lub nazwą użytkownika.";

        private const string AliasTakenMessage = "Ten przydomek został właśnie zajęty. Wylosuj inny.";

        private readonly ApplicationDbContext _context;
        private readonly PublicDemoOptions _demo;
        private readonly IAliasGenerator _aliases;

        public AuthService(ApplicationDbContext context, IOptions<PublicDemoOptions> demo, IAliasGenerator aliases)
        {
            _context = context;
            _demo = demo.Value;
            _aliases = aliases;
        }

        public async Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        {
            if (!_demo.RegistrationEnabled)
                return ServiceResult<UserResponse>.Fail(ServiceError.Forbidden, "Rejestracja jest obecnie wyłączona.", RegistrationDisabledCode);

            if ((RequestValidator.Validate(request) ?? PasswordPolicy.Validate(request.Password)) is { } error)
                return ServiceResult<UserResponse>.Fail(ServiceError.Validation, error);

            // Domena kont demo jest zarezerwowana (znacznik seeda) — publiczna rejestracja nie może jej użyć.
            if (Demo.DemoContent.IsDemoEmail(request.Email))
                return ServiceResult<UserResponse>.Fail(ServiceError.Validation, "Ten adres email nie może zostać użyty.");

            // Nazwa konta wyłącznie z zatwierdzonego słownika — także gdy klient zmodyfikuje request (ukryte pole, REST).
            var requestedAlias = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            if (requestedAlias is not null && !WesternAliases.IsCurated(requestedAlias))
            {
                return ServiceResult<UserResponse>.Fail(ServiceError.Validation,
                    "Przydomek musi pochodzić z generatora SansPost.", AliasNotCuratedCode);
            }

            var user = new User
            {
                Username = requestedAlias ?? string.Empty,
                NormalizedUsername = requestedAlias is null ? string.Empty : IdentityNormalizer.Normalize(requestedAlias),
                Email = request.Email.Trim(),
                NormalizedEmail = IdentityNormalizer.Normalize(request.Email),
                PasswordHash = PasswordHasher.Hash(request.Password),
                Role = UserRole.User,
                Status = AccountStatus.Active,
                CreatedAt = DateTime.UtcNow,
                Subscription = new Subscription { Type = SubscriptionType.Free }
            };

            // Jawna transakcja: COUNT → INSERT musi być atomowe względem WSZYSTKICH równoległych rejestracji
            // (limit jest globalny). Blokada jednego wiersza bramy serializuje je w PostgreSQL, także między instancjami.
            // Wyjście bez Commit (limit, duplikat, wyjątek, anulowanie) = rollback przy Dispose.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.LockRegistrationGateAsync(cancellationToken);

            if (await CountPublicAccountsAsync(cancellationToken) >= _demo.MaxPublicAccounts)
            {
                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict,
                    "Limit kont publicznej wersji demo został osiągnięty.", CapacityReachedCode);
            }

            if (await EmailTakenAsync(user, cancellationToken))
                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, DuplicateIdentityMessage, IdentityTakenCode);

            if (requestedAlias is null)
            {
                // Przydział pod blokadą bramy — równoległe rejestracje nie dostaną tego samego przydomka.
                if (await _aliases.SuggestAsync(cancellationToken) is not { } assigned)
                {
                    return ServiceResult<UserResponse>.Fail(ServiceError.Conflict,
                        "Brak wolnych przydomków. Rejestracja jest chwilowo niedostępna.", AliasPoolExhaustedCode);
                }

                user.Username = assigned;
                user.NormalizedUsername = IdentityNormalizer.Normalize(assigned);
            }
            else if (await AliasTakenAsync(user, cancellationToken))
            {
                // Przydomek wyświetlony w formularzu zajął w międzyczasie ktoś inny — 409, nie 500.
                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, AliasTakenMessage, AliasTakenCode);
            }

            _context.Users.Add(user);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Ostatnia linia obrony — UNIQUE index na znormalizowanych polach.
                _context.ChangeTracker.Clear();
                await transaction.RollbackAsync(cancellationToken);
                if (await AliasTakenAsync(user, cancellationToken))
                    return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, AliasTakenMessage, AliasTakenCode);
                if (!await EmailTakenAsync(user, cancellationToken))
                    throw;

                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, DuplicateIdentityMessage, IdentityTakenCode);
            }

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<UserResponse>.Success(UserResponse.From(user, DateTime.UtcNow));
        }

        public async Task<AuthenticatedUser?> AuthenticateAsync(string? email, string? password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return null;

            var normalizedEmail = IdentityNormalizer.Normalize(email);
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

            if (user is null)
            {
                PasswordHasher.SimulateVerification(password);
                return null;
            }

            // Stan konta: rekord bez poprawnego hasha BCrypt (legacy plaintext) jest zawsze odrzucany.
            if (!PasswordHasher.Verify(password, user.PasswordHash))
                return null;

            // Zbanowany: ta sama ogólna odpowiedź co przy złym haśle. Suspended może się zalogować (odczyt).
            if (user.Status == AccountStatus.Banned)
                return null;

            return AuthenticatedUser.From(user);
        }

        public async Task<bool> IsRegistrationAvailableAsync(CancellationToken cancellationToken = default) =>
            _demo.RegistrationEnabled && await CountPublicAccountsAsync(cancellationToken) < _demo.MaxPublicAccounts;

        // Tablica "Zajęte miejsca" przy wejściu: ta sama reguła liczenia co limit rejestracji (konta publiczne poniżej).
        public async Task<RegistrationCapacity> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            new(await CountPublicAccountsAsync(cancellationToken), _demo.MaxPublicAccounts);

        // Konto publiczne = zwykłe konto założone rejestracją. Liczą się WSZYSTKIE takie konta (Active, Suspended, Banned) —
        // ban nie zwalnia slotu, inaczej limit dałoby się obejść rotacją kont. Nie zajmują slotów: admini (rola) ani konta
        // demo z seeda (zarezerwowana domena, której rejestracja nie przyjmuje) — po świeżym wdrożeniu z treściami startowymi 0 / limit.
        private static readonly string DemoEmailSuffix = IdentityNormalizer.Normalize("@" + Demo.DemoContent.EmailDomain);

        private Task<int> CountPublicAccountsAsync(CancellationToken cancellationToken) =>
            _context.Users.CountAsync(u => u.Role == UserRole.User && !u.NormalizedEmail.EndsWith(DemoEmailSuffix), cancellationToken);

        private Task<bool> EmailTakenAsync(User user, CancellationToken cancellationToken) =>
            _context.Users.AnyAsync(u => u.NormalizedEmail == user.NormalizedEmail, cancellationToken);

        // Przydomki są publiczne (profile), więc informacja "zajęty" niczego nie ujawnia — w odróżnieniu od emaila.
        private Task<bool> AliasTakenAsync(User user, CancellationToken cancellationToken) =>
            _context.Users.AnyAsync(u => u.NormalizedUsername == user.NormalizedUsername, cancellationToken);
    }
}
