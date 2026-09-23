using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost.Tests.TestInfrastructure
{
    // SQLite in-memory: prawdziwe constrainty (UNIQUE, FK) bez PostgreSQL. Pełne testy na PostgreSQL — Sprint 7.
    public sealed class TestDatabase : IDisposable
    {
        public static readonly JwtOptions JwtOptions = new()
        {
            Key = "unit-tests-signing-key-0123456789abcdef",
            Issuer = "SansPost",
            Audience = "SansPostUser",
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            RefreshTokenLifetime = TimeSpan.FromDays(7)
        };

        private readonly SqliteConnection _connection;

        public TestDatabase()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public MutableTimeProvider Time { get; } = new();

        public ApplicationDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        public AuthService CreateAuthService(ApplicationDbContext context) => new(context);

        public ApiTokenService CreateTokenService(ApplicationDbContext context)
        {
            var options = Options.Create(JwtOptions);
            return new ApiTokenService(context, new JwtTokenService(options, Time), options, Time);
        }

        public async Task<AuthenticatedUser> RegisterAsync(string username, string password = TestUsers.Password)
        {
            using var context = CreateContext();
            var auth = CreateAuthService(context);

            var result = await auth.RegisterAsync(new RegisterRequest
            {
                Username = username,
                Email = $"{username}@example.com",
                Password = password
            });
            Assert.True(result.Succeeded, result.Message);

            return (await auth.AuthenticateAsync($"{username}@example.com", password))!;
        }

        public void Dispose() => _connection.Dispose();
    }

    public sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    public static class TestUsers
    {
        public const string Password = "correct-horse-battery";

        public static string UniqueName(string prefix = "u") => $"{prefix}{Guid.NewGuid():N}"[..20];
    }
}
