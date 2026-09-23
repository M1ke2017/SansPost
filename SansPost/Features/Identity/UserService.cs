using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    public interface IUserService
    {
        Task<UserResponse?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);

        Task<ServiceResult> ChangeRoleAsync(int userId, UserRole role, CancellationToken cancellationToken = default);

        Task<ServiceResult> SetSubscriptionAsync(int userId, SubscriptionType type, DateTime? expiresAt, CancellationToken cancellationToken = default);
    }

    public class UserService : IUserService
    {
        private readonly ApplicationDbContext _context;

        public UserService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<UserResponse?> GetByIdAsync(int userId, CancellationToken cancellationToken = default)
        {
            var user = await _context.Users
                .AsNoTracking()
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            return user is null ? null : UserResponse.From(user, DateTime.UtcNow);
        }

        public async Task<ServiceResult> ChangeRoleAsync(int userId, UserRole role, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(role))
                return ServiceResult.Fail(ServiceError.Validation, "Nieprawidłowa rola użytkownika.");

            var user = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (user is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");

            user.Role = role;
            await _context.SaveChangesAsync(cancellationToken);

            // Sesje API ze starą rolą nie mogą się odświeżać — wymagane ponowne logowanie.
            var now = DateTime.UtcNow;
            await _context.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

            return ServiceResult.Success();
        }

        public async Task<ServiceResult> SetSubscriptionAsync(int userId, SubscriptionType type, DateTime? expiresAt, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(type))
                return ServiceResult.Fail(ServiceError.Validation, "Nieprawidłowy typ subskrypcji.");

            var user = await _context.Users
                .Include(u => u.Subscription)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");

            var expiresAtUtc = expiresAt?.ToUniversalTime();

            if (user.Subscription is null)
            {
                user.Subscription = new Subscription { Type = type, ExpiresAt = expiresAtUtc };
            }
            else
            {
                user.Subscription.Type = type;
                user.Subscription.ExpiresAt = expiresAtUtc;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return ServiceResult.Success();
        }
    }
}
