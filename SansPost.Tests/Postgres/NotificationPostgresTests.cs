using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Moderation;
using SansPost.Features.Notifications;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Notifications PostgreSQL: współbieżność, atomowość komentarz+powiadomienie, miękkie usuwanie, indeksy.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Notifications")]
    public sealed class NotificationPostgresTests
    {
        private readonly PostgresFixture _pg;

        public NotificationPostgresTests(PostgresFixture pg)
        {
            _pg = pg;
        }

        // Prawdziwa awaria zapisu: polecenie z INSERT do wskazanej tabeli rzuca przed wykonaniem.
        private sealed class FailInsertInterceptor(string table) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
                InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
                command.CommandText.Contains($"INSERT INTO {table}", StringComparison.Ordinal)
                    ? throw new InvalidOperationException($"Symulowana awaria zapisu do {table}.")
                    : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // 10 równoległych, różnych komentarzy B pod postem A → 10 komentarzy i dokładnie 10 powiadomień.
        [DockerFact]
        public async Task TenParallelComments_CreateTenNotifications_NoneLostOrDuplicated()
        {
            var authorId = await _pg.CreateUserAsync("na");
            var commenterId = await _pg.CreateUserAsync("nb");
            var postId = await _pg.SeedPostAsync(authorId, "Post pod ostrzałem", "treść");

            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            {
                await using var context = _pg.CreateContext();
                return await TestServices.Comments(context).AddAsync(commenterId, postId, new CommentRequest { Content = $"Równoległy komentarz {i}" });
            }));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Message));
            await using var check = _pg.CreateContext();
            var commentIds = await check.Comments.Where(c => c.PostId == postId).Select(c => c.Id).ToListAsync();
            var notifications = await check.Notifications.Where(n => n.PostId == postId).ToListAsync();
            Assert.Equal(10, commentIds.Count);
            Assert.Equal(10, notifications.Count);
            Assert.True(commentIds.ToHashSet().SetEquals(notifications.Select(n => n.CommentId!.Value)));
            Assert.All(notifications, n => Assert.Equal((authorId, commenterId), (n.UserId, n.ActorUserId)));
        }

        // Atomowość w obie strony: awaria zapisu powiadomienia lub komentarza = brak obu (jedno SaveChanges).
        [DockerTheory]
        [InlineData("notifications")]
        [InlineData("comments")]
        public async Task FailedSave_LeavesNeitherCommentNorNotification(string failingTable)
        {
            var authorId = await _pg.CreateUserAsync("na");
            var commenterId = await _pg.CreateUserAsync("nb");
            var postId = await _pg.SeedPostAsync(authorId, "Post atomowy", "treść");

            await using (var context = _pg.CreateContext(null, new FailInsertInterceptor(failingTable)))
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    TestServices.Comments(context).AddAsync(commenterId, postId, new CommentRequest { Content = "Nie zapisze się" }));
            }

            await using var check = _pg.CreateContext();
            Assert.Equal(0, await check.Comments.CountAsync(c => c.PostId == postId));
            Assert.Equal(0, await check.Notifications.CountAsync(n => n.PostId == postId));
        }

        // Miękkie usunięcie posta i ukrycie komentarza nie kasują historii (CASCADE działa tylko przy fizycznym DELETE).
        [DockerFact]
        public async Task SoftDeleteAndModeration_KeepNotificationHistory_WithoutLeakingContent()
        {
            var authorId = await _pg.CreateUserAsync("na");
            var commenterId = await _pg.CreateUserAsync("nb");
            var postId = await _pg.SeedPostAsync(authorId, "Post do ukrycia", "treść");
            await using (var context = _pg.CreateContext())
                Assert.True((await TestServices.Comments(context).AddAsync(commenterId, postId, new CommentRequest { Content = "komentarz" })).Succeeded);

            await using (var context = _pg.CreateContext())
            {
                var version = await context.Posts.Where(p => p.Id == postId).Select(p => p.Version).SingleAsync();
                Assert.True((await PostgresFixture.CreatePostService(context).DeleteAsync(authorId, postId, version)).Succeeded);
            }

            await using var check = _pg.CreateContext();
            var page = await new NotificationService(check, TimeProvider.System).GetAsync(authorId, new NotificationPageQuery());
            var notification = Assert.Single(page.Value!.Items);
            Assert.False(notification.TargetAvailable);
            Assert.Null(notification.PostTitle);
        }

        // Indeksy uzasadnione planem: lista odbiorcy i licznik nieprzeczytanych (dane: 30 000 powiadomień, 300 odbiorców, 90% przeczytanych).
        [DockerFact]
        public async Task NotificationQueries_UseTheirIndexes()
        {
            var db = await _pg.CreateMigratedDatabaseAsync($"notif_plan_{Guid.NewGuid():N}");
            await using (var connection = new NpgsqlConnection(db))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO users (username, normalizedusername, email, normalizedemail, passwordhash, role, createdat)
                    SELECT 'u' || g, 'U' || g, 'u' || g || '@example.com', 'U' || g || '@EXAMPLE.COM', 'x', 0, now() FROM generate_series(1, 300) g;
                    INSERT INTO posts (userid, title, content, category, createdat) VALUES (1, 'Post', 'x', 'General', now());
                    INSERT INTO notifications (userid, type, actoruserid, postid, createdat, readat)
                    SELECT 1 + g % 300, 'CommentOnPost', 1 + (g + 7) % 300, 1, now() - g * interval '1 minute',
                           CASE WHEN g % 10 = 0 THEN NULL ELSE now() END
                    FROM generate_series(1, 30000) g;
                    ANALYZE;
                    """, connection);
                await seed.ExecuteNonQueryAsync();
            }

            async Task<string> PlanAsync(string sql)
            {
                await using var connection = new NpgsqlConnection(db);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection);
                var lines = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    lines.Add(reader.GetString(0));
                return string.Join('\n', lines);
            }

            var list = await PlanAsync("SELECT id FROM notifications WHERE userid = 42 ORDER BY createdat DESC, id DESC LIMIT 21");
            var unread = await PlanAsync("SELECT count(*) FROM notifications WHERE userid = 42 AND readat IS NULL");

            Assert.Contains("IX_notifications_user_feed", list);
            Assert.DoesNotContain("Seq Scan", list);
            Assert.Contains("IX_notifications_unread", unread);
            Assert.DoesNotContain("Seq Scan", unread);
        }
    }
}
