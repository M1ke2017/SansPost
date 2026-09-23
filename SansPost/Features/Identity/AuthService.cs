using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    // Wspólna logika Identity dla obu klientów: Blazor (cookie) i REST (JWT).
    public interface IAuthService
    {
        Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

        // null = nieprawidłowe dane logowania (bez rozróżnienia przyczyny).
        Task<AuthenticatedUser?> AuthenticateAsync(string? email, string? password, CancellationToken cancellationToken = default);
    }

    public class AuthService : IAuthService
    {
        private const string DuplicateIdentityMessage = "Nie można utworzyć konta z podanym emailem lub nazwą użytkownika.";

        private readonly ApplicationDbContext _context;

        public AuthService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        {
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
                CreatedAt = DateTime.UtcNow,
                Subscription = new Subscription { Type = SubscriptionType.Free }
            };

            if (await IdentityTakenAsync(user, cancellationToken))
                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, DuplicateIdentityMessage);

            _context.Users.Add(user);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Równoległa rejestracja — rozstrzygnięta przez UNIQUE index w bazie.
                _context.ChangeTracker.Clear();
                if (!await IdentityTakenAsync(user, cancellationToken))
                    throw;

                return ServiceResult<UserResponse>.Fail(ServiceError.Conflict, DuplicateIdentityMessage);
            }

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

            return AuthenticatedUser.From(user);
        }

        private Task<bool> IdentityTakenAsync(User user, CancellationToken cancellationToken) =>
            _context.Users.AnyAsync(
                u => u.NormalizedEmail == user.NormalizedEmail || u.NormalizedUsername == user.NormalizedUsername,
                cancellationToken);
    }
}
