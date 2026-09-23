using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SansPost.Features.Posts;
using Xunit.Abstractions;

namespace SansPost.Tests.Postgres
{
    // Performance baseline: liczba zapytań (brak N+1) i to, czy kształt zapytań EF pasuje do indeksów.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    public class PostgresFeedQueryTests
    {
        private readonly PostgresFixture _pg;
        private readonly ITestOutputHelper _output;

        public PostgresFeedQueryTests(PostgresFixture pg, ITestOutputHelper output)
        {
            _pg = pg;
            _output = output;
        }

        private sealed class CommandCapture : DbCommandInterceptor
        {
            public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = new();

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }
        }

        private async Task<(PostFeedResponse Feed, CommandCapture Capture)> RunFeedAsync(PostFeedQuery query)
        {
            var capture = new CommandCapture();
            await using var context = _pg.CreateContext(interceptors: capture);
            var result = await PostgresFixture.CreatePostService(context).GetFeedAsync(query);
            Assert.True(result.Succeeded, result.Message);
            return (result.Value!, capture);
        }

        [DockerFact]
        public async Task Feed_WithManyAuthors_ExecutesSingleQuery_AndPagesWithoutDuplicates()
        {
            for (var i = 0; i < 5; i++)
                await _pg.SeedPostsAsync(await _pg.CreateUserAsync("feed"), 5);

            var seen = new HashSet<int>();
            string? cursor = null;
            do
            {
                var (feed, capture) = await RunFeedAsync(new PostFeedQuery { Limit = 7, Cursor = cursor });

                Assert.Single(capture.Commands); // autor przez JOIN w projekcji — brak N+1
                Assert.All(feed.Items, item => Assert.True(seen.Add(item.Id), $"Duplikat {item.Id} między stronami."));
                cursor = feed.NextCursor;
            }
            while (cursor is not null);

            Assert.True(seen.Count >= 25);
        }

        [DockerTheory]
        [InlineData("newest", "IX_posts_feed")]
        [InlineData("author", "IX_posts_author_feed")]
        [InlineData("category", "IX_posts_category_feed")]
        public async Task FeedQueries_CanUseTheirDedicatedIndex(string feed, string expectedIndex)
        {
            var userId = await _pg.CreateUserAsync("plan");
            await _pg.SeedPostsAsync(userId, 3);

            var query = feed switch
            {
                "author" => new PostFeedQuery { AuthorId = userId },
                "category" => new PostFeedQuery { Category = PostCategory.General },
                _ => new PostFeedQuery()
            };

            // Druga strona — zapytanie z warunkiem keyset (kursor).
            var (firstPage, _) = await RunFeedAsync(new PostFeedQuery { AuthorId = query.AuthorId, Category = query.Category, Limit = 1 });
            query.Limit = 1;
            query.Cursor = firstPage.NextCursor;
            var (_, capture) = await RunFeedAsync(query);
            var (sql, parameters) = capture.Commands.Single();

            // Mała tabela → planner i tak wybrałby seq scan; wyłączamy go, żeby sprawdzić czy indeks PASUJE do zapytania.
            await using var connection = new NpgsqlConnection(_pg.ConnectionString);
            await connection.OpenAsync();
            await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
                await off.ExecuteNonQueryAsync();

            await using var explain = new NpgsqlCommand("EXPLAIN " + sql, connection);
            explain.Parameters.AddRange(parameters);
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    plan.Add(reader.GetString(0));

            _output.WriteLine(sql);
            _output.WriteLine(string.Join(Environment.NewLine, plan));
            Assert.Contains(plan, line => line.Contains(expectedIndex, StringComparison.Ordinal));

            // Kursor musi zawężać zakres indeksu (Index Cond), a nie tylko filtrować od początku (koszt jak OFFSET).
            Assert.Contains(plan, line => line.Contains("Index Cond", StringComparison.Ordinal) && line.Contains("createdat <=", StringComparison.Ordinal));
        }
    }
}
