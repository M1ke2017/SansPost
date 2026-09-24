using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    // Odczyt użytkownika. Zmiany roli/subskrypcji/statusu to akcje administracyjne z audytem — IModerationService.
    public interface IUserService
    {
        Task<UserResponse?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);

        // Stan własnego konta dla UI (np. komunikat o zawieszeniu). null = konto nie istnieje.
        Task<AccountStatus?> GetAccountStatusAsync(int userId, CancellationToken cancellationToken = default);
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

        public Task<AccountStatus?> GetAccountStatusAsync(int userId, CancellationToken cancellationToken = default) =>
            _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (AccountStatus?)u.Status)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
