using Docker.DotNet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;
using Testcontainers.PostgreSql;

namespace SansPost.Tests.Postgres
{
    // Prawdziwy PostgreSQL (Testcontainers) dla testów zależnych od zachowania bazy: blokady, transakcje,
    // concurrency tokeny, migracje. Schemat tworzony przez MigrateAsync — te same migracje co produkcja.
    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .Build();

        public string ConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }

        public Task DisposeAsync() => _container.DisposeAsync().AsTask();

        public ApplicationDbContext CreateContext(string? connectionString = null, params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString ?? ConnectionString)
                .AddInterceptors(interceptors)
                .Options);

        public static PostService CreatePostService(ApplicationDbContext context) => new(context, TimeProvider.System);

        // Osobna, pusta baza w tym samym kontenerze (np. do testów migracji od zera).
        public async Task<string> CreateDatabaseAsync(string name)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();

            return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        }

        public async Task<int> CreateUserAsync(string prefix = "pg")
        {
            var username = TestUsers.UniqueName(prefix);
            await using var context = CreateContext();
            var result = await new AuthService(context).RegisterAsync(new RegisterRequest
            {
                Username = username,
                Email = $"{username}@example.com",
                Password = TestUsers.Password
            });
            Assert.True(result.Succeeded, result.Message);
            return result.Value!.Id;
        }

        public async Task SeedPostsAsync(int userId, int count)
        {
            await using var context = CreateContext();
            var now = DateTime.UtcNow;
            for (var i = 0; i < count; i++)
            {
                context.Posts.Add(new Post
                {
                    UserId = userId,
                    Title = $"Seed {i}",
                    Content = "Treść",
                    Category = PostCategory.General,
                    CreatedAt = now.AddSeconds(-count + i)
                });
            }

            await context.SaveChangesAsync();
        }

        public async Task<int> CountPostsAsync(int userId)
        {
            await using var context = CreateContext();
            return await context.Posts.CountAsync(p => p.UserId == userId);
        }
    }

    [CollectionDefinition(Name)]
    public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
    {
        public const string Name = "PostgreSQL";
    }

    // Test wymagający Dockera. Gdy Docker nie odpowiada, test jest jawnie oznaczany jako Skipped (nie "passed").
    public sealed class DockerFactAttribute : FactAttribute
    {
        public DockerFactAttribute()
        {
            if (!DockerAvailability.IsAvailable)
                Skip = DockerAvailability.SkipReason;
        }
    }

    public sealed class DockerTheoryAttribute : TheoryAttribute
    {
        public DockerTheoryAttribute()
        {
            if (!DockerAvailability.IsAvailable)
                Skip = DockerAvailability.SkipReason;
        }
    }

    internal static class DockerAvailability
    {
        public const string SkipReason = "Wymaga Dockera (Testcontainers PostgreSQL) — Docker niedostępny.";

        private static readonly Lazy<bool> Available = new(() =>
        {
            try
            {
                using var client = new DockerClientConfiguration().CreateClient();
                return client.System.PingAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                return false;
            }
        });

        public static bool IsAvailable => Available.Value;
    }
}
