using Npgsql;
using SansPost.Features.Posts;
using SansPost.Features.Search;
using Xunit.Abstractions;

namespace SansPost.Tests.Postgres
{
    // Kształt zapytań discovery na KONTROLOWANYM zbiorze danych (osobna, zmigrowana baza, po ANALYZE):
    // 3000 użytkowników, 30 000 postów rozłożonych na ~875 dni (kategorie nierównomiernie: Feedback ≈ 1%),
    // ~10 000 polubień, ~6 000 komentarzy. Plany są NATURALNE (bez enable_seqscan=off) — planner sam wybiera
    // indeksy przy realistycznej selektywności. Przy 3 000 postów planner słusznie wybierał seq scan dla FTS
    // (mała tabela), więc zbiór jest na tyle duży, by koszt skanu był realny.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "QueryShape")]
    public class DiscoveryQueryShapeTests
    {
        private const string RareWord = "rzadkiehaslo";

        private static readonly SemaphoreSlim DatasetLock = new(1, 1);
        private static string? _datasetConnectionString;

        private readonly PostgresFixture _pg;
        private readonly ITestOutputHelper _output;

        public DiscoveryQueryShapeTests(PostgresFixture pg, ITestOutputHelper output)
        {
            _pg = pg;
            _output = output;
        }

        private async Task<string> DatasetAsync()
        {
            await DatasetLock.WaitAsync();
            try
            {
                if (_datasetConnectionString is not null)
                    return _datasetConnectionString;

                var connectionString = await _pg.CreateMigratedDatabaseAsync($"discovery_{Guid.NewGuid():N}");
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO users (username, normalizedusername, email, normalizedemail, passwordhash, role, createdat)
                    SELECT 'user' || g, 'USER' || g, 'user' || g || '@example.com', 'USER' || g || '@EXAMPLE.COM', 'x', 0, now() - g * interval '1 day'
                    FROM generate_series(1, 3000) g;

                    INSERT INTO posts (userid, title, content, category, createdat, version)
                    SELECT 1 + (g % 3000),
                           'Post numer ' || g || CASE WHEN g % 4000 = 0 THEN ' {RareWord}' ELSE '' END,
                           repeat('treść przykładowa o podróżach i grach ', 5) || g,
                           CASE WHEN g % 100 = 0 THEN 'Feedback'
                                ELSE (ARRAY['General','Technology','Games','Travel','Ideas','Projects'])[1 + g % 6] END,
                           now() - g * interval '42 minutes',
                           1
                    FROM generate_series(1, 30000) g;

                    INSERT INTO likes (postid, userid, createdat)
                    SELECT p.id, 1 + (p.id * 7 % 3000), now() FROM posts p WHERE p.id % 3 = 0;

                    INSERT INTO comments (postid, userid, content, createdat, version)
                    SELECT p.id, 1 + (p.id % 3000), 'komentarz', now(), 1 FROM posts p WHERE p.id % 5 = 0;

                    -- Sprint 6: ~10% treści poza publicznym obiegiem (5% ukryte, 5% usunięte), część komentarzy ukryta.
                    UPDATE posts SET status = 'Hidden' WHERE id % 20 = 1;
                    UPDATE posts SET status = 'Deleted', deletedat = now() WHERE id % 20 = 2;
                    UPDATE comments SET status = 'Hidden' WHERE id % 10 = 1;

                    -- Zgłoszenia (10% Pending) i historia audytu.
                    INSERT INTO reports (reporteruserid, targettype, targetid, reason, createdat, status)
                    SELECT 1 + g % 3000, 'Post', g, 'Spam', now() - g * interval '1 minute',
                           CASE WHEN g % 10 = 0 THEN 'Pending' ELSE 'Resolved' END
                    FROM generate_series(1, 5000) g;
                    INSERT INTO moderationactions (adminuserid, actiontype, targettype, targetid, createdat)
                    SELECT 1, 'HidePost', 'Post', g, now() - g * interval '1 minute' FROM generate_series(1, 3000) g;

                    ANALYZE;
                    """, connection);
                await command.ExecuteNonQueryAsync();

                return _datasetConnectionString = connectionString;
            }
            finally
            {
                DatasetLock.Release();
            }
        }

        private async Task<(T Result, CommandCapture Capture)> CaptureAsync<T>(string connectionString, Func<SansPost.Infrastructure.Persistence.ApplicationDbContext, Task<T>> action)
        {
            var capture = new CommandCapture();
            await using var context = _pg.CreateContext(connectionString, capture);
            return (await action(context), capture);
        }

        private async Task<List<string>> NaturalPlanAsync(string connectionString, (string Sql, NpgsqlParameter[] Parameters) command)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN " + command.Sql, connection);
            explain.Parameters.AddRange(command.Parameters.Select(p => p.Clone()).ToArray());

            var plan = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));

            _output.WriteLine(command.Sql);
            _output.WriteLine(string.Join(Environment.NewLine, plan));
            return plan;
        }

        private static void AssertPlanUses(List<string> plan, string fragment) =>
            Assert.Contains(plan, line => line.Contains(fragment, StringComparison.Ordinal));

        // L, M, N, O — search: FTS w SQL, jedna komenda z LIMIT (bez materializacji w C#), GIN w naturalnym planie.
        [DockerFact]
        public async Task Search_UsesFullTextOperator_SingleLimitedQuery_AndGinIndex()
        {
            var db = await DatasetAsync();

            var (result, capture) = await CaptureAsync(db, context => new PostgresSearchService(context)
                .SearchPostsAsync(new PostSearchQuery { Q = RareWord, Limit = 5 }, viewerUserId: 1));

            Assert.True(result.Succeeded);
            Assert.Equal(5, result.Value!.Items.Count);
            Assert.True(result.Value.HasMore);                                    // 7 trafień, strona 5

            var (sql, _) = Assert.Single(capture.Commands);                      // N: autor + liczniki + LikedByCurrentUser w 1 SQL
            Assert.Contains("@@ websearch_to_tsquery", sql);                       // L: natywny FTS
            Assert.Contains("ts_rank", sql);
            Assert.Contains("LIMIT", sql);                                          // M: strona liczona w bazie
            Assert.DoesNotContain("LIKE", sql);

            var plan = await NaturalPlanAsync(db, capture.Commands[0]);
            AssertPlanUses(plan, "Bitmap Index Scan on \"IX_posts_search\"");      // O
        }

        [DockerFact]
        public async Task NewestAndCategoryFeeds_SeekTheirIndexes()
        {
            var db = await DatasetAsync();

            var (_, newestCapture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { Limit = 20 }, null));
            AssertPlanUses(await NaturalPlanAsync(db, Assert.Single(newestCapture.Commands)), "Index Scan using \"IX_posts_feed\"");

            // Rzadka kategoria (≈1%): dedykowany indeks — przejście po feedzie odrzucałoby ~99% wierszy.
            var (_, rareCapture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { Category = PostCategory.Feedback, Limit = 20 }, null));
            AssertPlanUses(await NaturalPlanAsync(db, Assert.Single(rareCapture.Commands)), "IX_posts_category_feed");

            // Częsta kategoria (≈1/6): planner słusznie idzie po IX_posts_feed z filtrem — z LIMIT 21 czyta ~130 wierszy.
            var (_, commonCapture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { Category = PostCategory.Travel, Limit = 20 }, null));
            var commonPlan = await NaturalPlanAsync(db, Assert.Single(commonCapture.Commands));
            Assert.Contains(commonPlan, line => line.Contains("IX_posts_feed") || line.Contains("IX_posts_category_feed"));
            Assert.DoesNotContain(commonPlan, line => line.Contains("Seq Scan on posts"));
        }

        [DockerFact]
        public async Task PopularFeed_ScoresInSql_InSingleQuery_OverIndexedFreshnessWindow()
        {
            var db = await DatasetAsync();

            var (first, capture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { Sort = PostSort.Popular, Limit = 10 }, null));

            Assert.True(first.Succeeded);
            var (sql, _) = Assert.Single(capture.Commands);
            Assert.Contains("ln(", sql);                                            // score w SQL, nie w C#
            Assert.Contains("LIMIT", sql);
            AssertPlanUses(await NaturalPlanAsync(db, capture.Commands[0]), "IX_posts_feed");   // okno 30 dni = zakres indeksu

            // Druga strona z kursorem — nadal jedno zapytanie.
            var (second, secondCapture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { Sort = PostSort.Popular, Limit = 10, Cursor = first.Value!.NextCursor }, null));
            Assert.True(second.Succeeded);
            Assert.Single(secondCapture.Commands);
            Assert.Empty(first.Value!.Items.Select(p => p.Id).Intersect(second.Value!.Items.Select(p => p.Id)));
        }

        [DockerFact]
        public async Task UsernamePrefixLookup_UsesPatternOpsIndex()
        {
            var db = await DatasetAsync();

            var (result, capture) = await CaptureAsync(db, context => new PostgresSearchService(context).SearchUsersAsync("user299", 10));

            Assert.Equal(10, result.Value!.Count);
            var command = Assert.Single(capture.Commands);
            Assert.DoesNotContain("email", command.Sql, StringComparison.OrdinalIgnoreCase);
            AssertPlanUses(await NaturalPlanAsync(db, command), "IX_users_username_prefix");
        }

        [DockerFact]
        public async Task CategoryDiscovery_IsSingleGroupByQuery()
        {
            var db = await DatasetAsync();

            var (categories, capture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context).GetCategoriesAsync());

            Assert.Equal(27000, categories.Sum(c => c.PostCount));   // tylko opublikowane (10% ukrytych/usuniętych)
            Assert.Equal(300, categories.Single(c => c.Category == PostCategory.Feedback).PostCount);
            Assert.Contains("GROUP BY", Assert.Single(capture.Commands).Sql);
        }

        // Sprint 6: po dodaniu filtra status = 'Published' — feed autora, wątek komentarzy, kolejka i historia moderacji.
        [DockerFact]
        public async Task SoftStateFilteredQueries_UseTheirIndexes()
        {
            var db = await DatasetAsync();

            var (_, authorCapture) = await CaptureAsync(db, context => PostgresFixture.CreatePostService(context)
                .GetFeedAsync(new PostFeedQuery { AuthorId = 42, Limit = 20 }, null));
            AssertPlanUses(await NaturalPlanAsync(db, Assert.Single(authorCapture.Commands)), "IX_posts_author_feed");

            var (_, threadCapture) = await CaptureAsync(db, context =>
                new SansPost.Features.Comments.CommentService(context, TimeProvider.System, SansPost.Tests.TestInfrastructure.TestServices.Guard(context))
                    .GetByPostAsync(4000, new SansPost.Features.Comments.CommentPageQuery()));
            AssertPlanUses(await NaturalPlanAsync(db, threadCapture.Commands[^1]), "IX_comments_post_thread");

            var (_, queueCapture) = await CaptureAsync(db, context =>
                new SansPost.Features.Moderation.ReportService(context, TimeProvider.System, SansPost.Tests.TestInfrastructure.TestServices.Guard(context))
                    .GetPendingAsync(new SansPost.Features.Moderation.ModerationQueueQuery()));
            AssertPlanUses(await NaturalPlanAsync(db, Assert.Single(queueCapture.Commands)), "IX_reports_pending_queue");

            var (_, historyCapture) = await CaptureAsync(db, context =>
                new SansPost.Features.Moderation.ModerationService(context, TimeProvider.System)
                    .GetActionsAsync(new SansPost.Features.Moderation.ModerationQueueQuery()));
            AssertPlanUses(await NaturalPlanAsync(db, Assert.Single(historyCapture.Commands)), "IX_moderationactions_history");
        }

        [DockerFact]
        public async Task CreatingPost_DoesNotWriteOrReturnSearchVector()
        {
            var userId = await _pg.CreateUserAsync("sv");

            var (result, capture) = await CaptureAsync(_pg.ConnectionString, context => PostgresFixture.CreatePostService(context)
                .CreateAsync(userId, new PostRequest { Title = "Wektor", Content = "Treść", Category = PostCategory.General }));

            Assert.True(result.Succeeded);
            var insert = capture.Commands.Single(c => c.Sql.Contains("INSERT INTO posts")).Sql;
            Assert.DoesNotContain("searchvector", insert);
        }
    }
}
