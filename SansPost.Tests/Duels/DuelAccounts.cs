using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SansPost.Features.Duels;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Duels
{
    // Konta graczy dla testów serwisów gry: SQLite w pamięci ze wspólną pamięcią podręczną — każdy kontekst ma własne
    // połączenie (jak pula w produkcji), więc równoległe akcje mogą równocześnie czytać stan konta. Prawdziwa brama WriteGuard.
    public sealed class DuelAccounts : IDisposable
    {
        private readonly string _connectionString = $"DataSource=duels-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;
        private readonly ServiceProvider _services;

        public DuelAccounts()
        {
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
            using (var schema = CreateContext())
                schema.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddScoped(_ => CreateContext());
            services.AddScoped(provider => TestServices.Guard(provider.GetRequiredService<ApplicationDbContext>()));
            _services = services.BuildServiceProvider();
        }

        public IServiceScopeFactory Scopes => _services.GetRequiredService<IServiceScopeFactory>();

        public ApplicationDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connectionString).Options);

        public int Add()
        {
            using var context = CreateContext();
            var name = TestUsers.RawName("gs");
            var user = new User
            {
                Username = name,
                NormalizedUsername = IdentityNormalizer.Normalize(name),
                Email = $"{name}@example.com",
                NormalizedEmail = IdentityNormalizer.Normalize($"{name}@example.com"),
                PasswordHash = "test-data-no-login"
            };
            context.Users.Add(user);
            context.SaveChanges();
            return user.Id;
        }

        // Zmiana statusu jak w moderacji (bezpośrednio w bazie — polityka czyta aktualny stan przy każdej akcji).
        public void SetStatus(int userId, AccountStatus status)
        {
            using var context = CreateContext();
            context.Users.Single(u => u.Id == userId).Status = status;
            context.SaveChanges();
        }

        public GameSessionService Sessions(TimeProvider time, IDuelNotifier notifier, DuelOptions? options = null) =>
            new(time, Scopes, notifier, Options.Create(options ?? new DuelOptions()), NullLogger<GameSessionService>.Instance);

        public void Dispose()
        {
            _services.Dispose();
            _keepAlive.Dispose();
        }
    }
}
