using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Pojemność publicznego demo i bootstrap admina na prawdziwym PostgreSQL. Każdy test ma własną, zmigrowaną bazę,
    // bo limit liczy wszystkie konta w bazie.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "PublicDemo")]
    public class PublicDemoPostgresTests
    {
        private readonly PostgresFixture _pg;

        public PublicDemoPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        private static RegisterRequest Registration(string username) => new()
        {
            Username = username,
            Email = $"{username}@example.com",
            Password = TestUsers.Password
        };

        private async Task<ServiceResult<UserResponse>> RegisterAsync(string connectionString, int max, string username)
        {
            await using var context = _pg.CreateContext(connectionString);
            return await TestServices.Auth(context, new PublicDemoOptions { MaxPublicAccounts = max }).RegisterAsync(Registration(username));
        }

        private async Task SeedAccountsAsync(string connectionString, int count, UserRole role = UserRole.User, AccountStatus status = AccountStatus.Active)
        {
            await using var context = _pg.CreateContext(connectionString);
            for (var i = 0; i < count; i++)
            {
                var name = TestUsers.UniqueName("seed");
                context.Users.Add(new User
                {
                    Username = name,
                    NormalizedUsername = IdentityNormalizer.Normalize(name),
                    Email = $"{name}@example.com",
                    NormalizedEmail = IdentityNormalizer.Normalize($"{name}@example.com"),
                    PasswordHash = "seed",
                    Role = role,
                    Status = status
                });
            }

            await context.SaveChangesAsync();
        }

        private async Task<int> PublicAccountsAsync(string connectionString)
        {
            await using var context = _pg.CreateContext(connectionString);
            return await context.Users.CountAsync(u => u.Role == UserRole.User);
        }

        // A, B
        [DockerFact]
        public async Task Registration_BelowLimitSucceeds_AtLimitReturnsCapacityConflict()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"cap_{Guid.NewGuid():N}");

            Assert.True((await RegisterAsync(db, 2, TestUsers.UniqueName())).Succeeded);
            Assert.True((await RegisterAsync(db, 2, TestUsers.UniqueName())).Succeeded);
            var full = await RegisterAsync(db, 2, TestUsers.UniqueName());

            Assert.Equal(ServiceError.Conflict, full.Error);
            Assert.Equal(AuthService.CapacityReachedCode, full.Code);
            Assert.Equal(2, await PublicAccountsAsync(db));
        }

        // D — obowiązkowy test współbieżności: 9/10 + 20 równoległych rejestracji → dokładnie 10.
        [DockerFact]
        public async Task CapacityRace_TwentyParallelRegistrations_NeverExceedLimit()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"caprace_{Guid.NewGuid():N}");
            await SeedAccountsAsync(db, 9);

            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => RegisterAsync(db, 10, TestUsers.UniqueName("race")))));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r =>
            {
                Assert.Equal(ServiceError.Conflict, r.Error);
                Assert.Equal(AuthService.CapacityReachedCode, r.Code);
            });
            Assert.Equal(10, await PublicAccountsAsync(db));
        }

        // E — administratorzy nie zajmują publicznych slotów.
        [DockerFact]
        public async Task Admins_DoNotOccupyPublicSlots()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"capadm_{Guid.NewGuid():N}");
            await SeedAccountsAsync(db, 3, UserRole.Admin);

            Assert.True((await RegisterAsync(db, 2, TestUsers.UniqueName())).Succeeded);
            Assert.True((await RegisterAsync(db, 2, TestUsers.UniqueName())).Succeeded);
            Assert.Equal(AuthService.CapacityReachedCode, (await RegisterAsync(db, 2, TestUsers.UniqueName())).Code);
        }

        // F — zawieszone i zbanowane konta nadal zajmują slot (ban nie pozwala obejść limitu rotacją kont).
        [DockerFact]
        public async Task SuspendedAndBannedAccounts_StillOccupySlots()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"capban_{Guid.NewGuid():N}");
            await SeedAccountsAsync(db, 1, status: AccountStatus.Suspended);
            await SeedAccountsAsync(db, 1, status: AccountStatus.Banned);

            var result = await RegisterAsync(db, 2, TestUsers.UniqueName());

            Assert.Equal(AuthService.CapacityReachedCode, result.Code);
        }

        [DockerFact]
        public async Task RegistrationGate_IsASingleRow_EnforcedByCheckConstraint()
        {
            await using var connection = new NpgsqlConnection(_pg.ConnectionString);
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO registrationgates (id) VALUES (2)", connection);

            var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        [DockerFact]
        public async Task BootstrapAdmin_CreatesAdminOnce_WithoutLoggingPassword()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"boot_{Guid.NewGuid():N}");
            var options = Options.Create(new BootstrapAdminOptions
            {
                Enabled = true,
                Username = "bootadmin",
                Email = "boot@example.com",
                Password = "very-secret-bootstrap-pass"
            });
            var logs = new ListLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));

            bool first, second;
            await using (var context = _pg.CreateContext(db))
                first = await new AdminBootstrapper(context, options, loggerFactory.CreateLogger<AdminBootstrapper>()).RunAsync();
            await using (var context = _pg.CreateContext(db))
                second = await new AdminBootstrapper(context, options, loggerFactory.CreateLogger<AdminBootstrapper>()).RunAsync();

            Assert.True(first);
            Assert.False(second);

            await using var check = _pg.CreateContext(db);
            var admin = await check.Users.SingleAsync(u => u.Role == UserRole.Admin);
            Assert.True(BCrypt.Net.BCrypt.Verify("very-secret-bootstrap-pass", admin.PasswordHash));
            Assert.Equal(0, await check.Users.CountAsync(u => u.Role == UserRole.User)); // nie zajmuje slotu
            Assert.DoesNotContain(logs.Messages, m => m.Contains("very-secret-bootstrap-pass") || m.Contains("boot@example.com"));
            Assert.Contains(logs.Messages, m => m.Contains("bootadmin"));
        }

        [DockerFact]
        public async Task BootstrapAdmin_Disabled_DoesNothing()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"bootoff_{Guid.NewGuid():N}");
            await using var context = _pg.CreateContext(db);

            var created = await new AdminBootstrapper(context, Options.Create(new BootstrapAdminOptions { Enabled = false }),
                LoggerFactory.Create(_ => { }).CreateLogger<AdminBootstrapper>()).RunAsync();

            Assert.False(created);
            Assert.False(await context.Users.AnyAsync());
        }

        private sealed class ListLoggerProvider : ILoggerProvider
        {
            public List<string> Messages { get; } = new();

            public ILogger CreateLogger(string categoryName) => new ListLogger(Messages);

            public void Dispose()
            {
            }

            private sealed class ListLogger(List<string> messages) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (messages)
                        messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
