using SansPost.Features.Comments;
using SansPost.Features.Posts;
using SansPost.Features.Profiles;
using Xunit.Abstractions;

using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Kształt zapytań warstwy social: stała liczba poleceń SQL niezależnie od ilości danych + użycie indeksów.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    public class SocialQueryShapeTests
    {
        private readonly PostgresFixture _pg;
        private readonly ITestOutputHelper _output;

        public SocialQueryShapeTests(PostgresFixture pg, ITestOutputHelper output)
        {
            _pg = pg;
            _output = output;
        }

        private async Task<int> CreatePostWithCommentsAsync(int authorId, int commenterId, int comments)
        {
            await using var context = _pg.CreateContext();
            var post = (await PostgresFixture.CreatePostService(context).CreateAsync(authorId,
                new PostRequest { Title = "Post z komentarzami", Content = "Treść", Category = PostCategory.General })).Value!;

            var service = SansPost.Tests.TestInfrastructure.TestServices.Comments(context);
            for (var i = 0; i < comments; i++)
                Assert.True((await service.AddAsync(i % 2 == 0 ? authorId : commenterId, post.Id, new CommentRequest { Content = $"K{i}" })).Succeeded);

            return post.Id;
        }

        [DockerFact]
        public async Task CommentsPage_IsTwoFixedQueries_AndSeeksByThreadIndex()
        {
            var authorId = await _pg.CreateUserAsync("qa");
            var commenterId = await _pg.CreateUserAsync("qb");
            var postId = await CreatePostWithCommentsAsync(authorId, commenterId, 6);

            var capture = new CommandCapture();
            await using var context = _pg.CreateContext(interceptors: capture);
            var service = SansPost.Tests.TestInfrastructure.TestServices.Comments(context);

            var first = await service.GetByPostAsync(postId, new CommentPageQuery { Limit = 2 });
            capture.Commands.Clear();
            var second = await service.GetByPostAsync(postId, new CommentPageQuery { Limit = 2, Cursor = first.Value!.NextCursor });

            // 1× EXISTS posta (404 vs pusta lista) + 1× strona z autorem przez JOIN — niezależnie od liczby komentarzy/autorów.
            Assert.Equal(2, capture.Commands.Count);
            Assert.Equal(2, second.Value!.Items.Count);

            var (sql, parameters) = capture.Commands[^1];
            var plan = await CommandCapture.ExplainAsync(_pg.ConnectionString, sql, parameters);
            _output.WriteLine(sql);
            _output.WriteLine(string.Join(Environment.NewLine, plan));

            Assert.Contains(plan, line => line.Contains("IX_comments_post_thread", StringComparison.Ordinal));
            Assert.Contains(plan, line => line.Contains("Index Cond", StringComparison.Ordinal) && line.Contains("createdat >=", StringComparison.Ordinal));
        }

        [DockerFact]
        public async Task PublicProfile_UsesFixedNumberOfQueries_RegardlessOfActivity()
        {
            async Task<int> ProfileQueriesAsync(string username)
            {
                var capture = new CommandCapture();
                await using var context = _pg.CreateContext(interceptors: capture);
                var posts = PostgresFixture.CreatePostService(context);
                var comments = SansPost.Tests.TestInfrastructure.TestServices.Comments(context);
                var profile = await new ProfileService(context, posts, comments).GetByUsernameAsync(username, null);
                Assert.NotNull(profile);
                return capture.Commands.Count;
            }

            var quietId = await _pg.CreateUserAsync("quiet");
            var busyId = await _pg.CreateUserAsync("busy");
            await CreatePostWithCommentsAsync(busyId, quietId, 10);
            await _pg.SeedPostsAsync(busyId, 6);

            await using var lookup = _pg.CreateContext();
            var quietName = lookup.Users.Single(u => u.Id == quietId).Username;
            var busyName = lookup.Users.Single(u => u.Id == busyId).Username;

            // użytkownik + liczniki (1), ostatnie posty z licznikami (1), ostatnie komentarze z tytułem posta (1)
            Assert.Equal(3, await ProfileQueriesAsync(quietName));
            Assert.Equal(3, await ProfileQueriesAsync(busyName));
        }
    }
}
