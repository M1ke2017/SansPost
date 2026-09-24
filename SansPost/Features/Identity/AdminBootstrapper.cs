using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    // Jednorazowe utworzenie pierwszego administratora z konfiguracji (User Secrets / zmienne środowiskowe).
    // Brak domyślnych danych logowania; nic nie robi, gdy Enabled = false lub istnieje już jakikolwiek Admin.
    // To NIE jest seed w migracji — sekrety nigdy nie trafiają do repozytorium ani historii migracji.
    public class AdminBootstrapper
    {
        private readonly ApplicationDbContext _context;
        private readonly BootstrapAdminOptions _options;
        private readonly ILogger<AdminBootstrapper> _logger;

        public AdminBootstrapper(ApplicationDbContext context, IOptions<BootstrapAdminOptions> options, ILogger<AdminBootstrapper> logger)
        {
            _context = context;
            _options = options.Value;
            _logger = logger;
        }

        // true = administrator został utworzony w tym wywołaniu.
        public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
                return false;

            // Ta sama brama co rejestracja: równoległy start kilku instancji nie utworzy dwóch adminów.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.LockRegistrationGateAsync(cancellationToken);

            if (await _context.Users.AnyAsync(u => u.Role == UserRole.Admin, cancellationToken))
            {
                _logger.LogInformation("BootstrapAdmin: administrator już istnieje — pomijam. Wyłącz BootstrapAdmin:Enabled.");
                return false;
            }

            var admin = new User
            {
                Username = _options.Username.Trim(),
                NormalizedUsername = IdentityNormalizer.Normalize(_options.Username),
                Email = _options.Email.Trim(),
                NormalizedEmail = IdentityNormalizer.Normalize(_options.Email),
                PasswordHash = PasswordHasher.Hash(_options.Password),
                Role = UserRole.Admin,
                Status = AccountStatus.Active,
                CreatedAt = DateTime.UtcNow,
                Subscription = new Subscription { Type = SubscriptionType.Free }
            };

            _context.Users.Add(admin);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Nigdy nie logujemy hasła ani emaila.
            _logger.LogWarning("BootstrapAdmin: utworzono administratora '{Username}'. Wyłącz BootstrapAdmin:Enabled po pierwszym wdrożeniu.",
                admin.Username);
            return true;
        }
    }

    public sealed class AdminBootstrapHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public AdminBootstrapHostedService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>().RunAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
