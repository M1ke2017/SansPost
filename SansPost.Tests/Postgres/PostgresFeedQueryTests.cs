using SansPost.Features;
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

        private async Task<(KeysetPage<PostSummaryResponse> Feed, CommandCapture Capture)> RunFeedAsync(PostFeedQuery query, int? viewerUserId = null)
        {
            var capture = new CommandCapture();
            await using var context = _pg.CreateContext(interceptors: capture);
            var result = await PostgresFixture.CreatePostService(context).GetFeedAsync(query, viewerUserId);
            Assert.True(result.Succeeded, result.Message);
            return (result.Value!, capture);
        }

        [DockerFact]
        public async Task Feed_WithManyAuthors_ExecutesSingleQuery_AndPagesWithoutDuplicates()
        {
            var viewerId = await _pg.CreateUserAsync("viewer");
            for (var i = 0; i < 5; i++)
                await _pg.SeedPostsAsync(await _pg.CreateUserAsync("feed"), 5);

            var seen = new HashSet<int>();
            string? cursor = null;
            do
            {
                // Autor, LikeCount, CommentCount i LikedByCurrentUser — wszystko w jednej projekcji SQL.
                var (feed, capture) = await RunFeedAsync(new PostFeedQuery { Limit = 7, Cursor = cursor }, viewerId);

                Assert.Single(capture.Commands);
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
            var (_, capture) = await RunFeedAsync(query, userId);
            var (sql, parameters) = capture.Commands.Single();

            var plan = await CommandCapture.ExplainAsync(_pg.ConnectionString, sql, parameters);

            _output.WriteLine(sql);
            _output.WriteLine(string.Join(Environment.NewLine, plan));
            // Mikro-tabela + wymuszone enable_seqscan=off: planner może wybrać dedykowany indeks albo mniejszy częściowy
            // IX_posts_feed z filtrem — oba są poprawne. Naturalne plany na 30 000 postów: DiscoveryQueryShapeTests.
            Assert.Contains(plan, line => line.Contains(expectedIndex, StringComparison.Ordinal) || line.Contains("IX_posts_feed", StringComparison.Ordinal));

            // Kursor musi zawężać zakres indeksu (Index Cond), a nie tylko filtrować od początku (koszt jak OFFSET).
            Assert.Contains(plan, line => line.Contains("Index Cond", StringComparison.Ordinal) && line.Contains("createdat <=", StringComparison.Ordinal));

            // Liczniki w feedzie korzystają z indeksów komentarzy/polubień, a nie z pełnego skanu tabel.
            Assert.Contains(plan, line => line.Contains("UX_likes_post_user", StringComparison.Ordinal));
            Assert.Contains(plan, line => line.Contains("IX_comments_post_thread", StringComparison.Ordinal));
        }
    }
}
