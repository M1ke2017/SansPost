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
    }

    public class AuthService : IAuthService
    {
        public const string CapacityReachedCode = "registration-capacity-reached";
        public const string RegistrationDisabledCode = "registration-disabled";
        public const string IdentityTakenCode = "registration-identity-taken";

        private const string DuplicateIdentityMessage = "Nie można utworzyć konta z podanym emailem lub nazwą użytkownika.";

        private readonly ApplicationDbContext _context;
        private readonly PublicDemoOptions _demo;

        public AuthService(ApplicationDbContext context, IOptions<PublicDemoOptions> demo)
        {
            _context = context;
            _demo = demo.Value;
        }

        public async Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        {
            if (!_demo.RegistrationEnabled)
                return ServiceResult<UserResponse>.Fail(ServiceError.Forbidden, "Rejestracja jest obecnie wyłączona.", RegistrationDisabledCode);

            if ((RequestValidator.Validate(request) ?? PasswordPolicy.Validate(request.Password)) is { } error)
                return ServiceResult<UserResponse>.Fail(ServiceError.Validation, error);

            var user = new User
            {
                Username = request.Username.Trim(),
                NormalizedUsername = IdentityNormalizer.Normalize(request.Username),
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

            if (await IdentityTakenAsync(user, cancellationToken))
                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, DuplicateIdentityMessage, IdentityTakenCode);

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
                if (!await IdentityTakenAsync(user, cancellationToken))
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

        // Liczą się WSZYSTKIE zwykłe konta (Active, Suspended, Banned) — ban nie zwalnia slotu,
        // inaczej limit dałoby się obejść rotacją kont. Admini nie zajmują publicznych slotów.
        private Task<int> CountPublicAccountsAsync(CancellationToken cancellationToken) =>
            _context.Users.CountAsync(u => u.Role == UserRole.User, cancellationToken);

        private Task<bool> IdentityTakenAsync(User user, CancellationToken cancellationToken) =>
            _context.Users.AnyAsync(
                u => u.NormalizedEmail == user.NormalizedEmail || u.NormalizedUsername == user.NormalizedUsername,
                cancellationToken);
    }
}
