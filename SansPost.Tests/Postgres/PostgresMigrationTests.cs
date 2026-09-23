using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SansPost.Features.Identity;
using SansPost.Features.Posts;

namespace SansPost.Tests.Postgres
{
    // Migracje (także data-migrations ze Sprintów 1–3) wykonane na prawdziwym PostgreSQL.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    public class PostgresMigrationTests
    {
        private const string Sprint0Schema = "20250326110652_InitialSubscriptionFix";

        private readonly PostgresFixture _pg;

        public PostgresMigrationTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        [DockerFact]
        public async Task AllMigrations_ApplyCleanly_AndNoModelChangesArePending()
        {
            await using var context = _pg.CreateContext();

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        }

        [DockerFact]
        public async Task LegacySprint0Data_IsMigratedToCurrentModel()
        {
            var connectionString = await _pg.CreateDatabaseAsync($"legacy_{Guid.NewGuid():N}");
            await using var context = _pg.CreateContext(connectionString);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(Sprint0Schema);
            await ExecuteAsync(connectionString, """
                INSERT INTO users (id, username, email, passwordhash, role, createdat) VALUES
                  (1, 'Admin', 'Admin@Example.com', 'plaintext', 2, now()),
                  (2, 'Premka', 'premka@example.com', 'plaintext', 1, now());
                INSERT INTO posts (userid, title, content, category, createdat) VALUES
                  (1, 'Stary tech', 'x', 'Technologia', now()),
                  (1, 'Stary sport', 'x', 'Sport', now()),
                  (2, 'Bez kategorii', 'x', NULL, now());
                """);

            await migrator.MigrateAsync();

            var users = await context.Users.Include(u => u.Subscription).OrderBy(u => u.Id).ToListAsync();
            Assert.Equal(UserRole.Admin, users[0].Role);
            Assert.Equal("ADMIN@EXAMPLE.COM", users[0].NormalizedEmail);
            Assert.Equal(UserRole.User, users[1].Role);
            Assert.Equal(SubscriptionType.Premium, users[1].Subscription!.Type);

            var posts = await context.Posts.OrderBy(p => p.Title).ToListAsync();
            Assert.Equal(new[] { PostCategory.General, PostCategory.General, PostCategory.Technology }, posts.Select(p => p.Category));
            Assert.All(posts, p => Assert.Equal(1, p.Version));
            Assert.All(posts, p => Assert.Null(p.UpdatedAt));
        }

        [DockerFact]
        public async Task DuplicateEmailsDifferingOnlyByCase_AbortMigrationWithInstructions()
        {
            var connectionString = await _pg.CreateDatabaseAsync($"dupes_{Guid.NewGuid():N}");
            await using var context = _pg.CreateContext(connectionString);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(Sprint0Schema);
            await ExecuteAsync(connectionString, """
                INSERT INTO users (username, email, passwordhash, role, createdat) VALUES
                  ('a', 'Same@Example.com', 'x', 0, now()),
                  ('b', 'same@example.com', 'x', 0, now());
                """);

            var exception = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());

            Assert.Contains("duplicate e-mails", exception.MessageText);
            Assert.DoesNotContain("20260923194313_AddAuthenticationFoundation", await context.Database.GetAppliedMigrationsAsync());
        }

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
