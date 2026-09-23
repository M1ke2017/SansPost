using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Social
{
    // Szybkie testy kontraktu HTTP komentarzy (pełny pipeline, SQLite).
    public class CommentsApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public CommentsApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Anonymous_CannotCreateComment()
        {
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await author.CreatePostAsync();

            var response = await _factory.CreateHttpsClient().PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content = "x" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Create_ReturnsCreatedWithETag_AuthorFromToken_IgnoringSpoofedFields()
        {
            var victimId = await _factory.CreateUserAsync(TestUsers.UniqueName("v"));
            var username = TestUsers.UniqueName("a");
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);
            var post = await client.CreatePostAsync();
            var otherPost = await client.CreatePostAsync("Inny post");

            var response = await client.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new
            {
                content = "  Mój komentarz  ",
                userId = victimId,
                postId = otherPost.Id,
                version = 99,
                createdAt = "2000-01-01T00:00:00Z"
            });
            var comment = await response.ReadAsync<CommentResponse>();

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
            Assert.Equal(username, comment.AuthorUsername);
            Assert.NotEqual(victimId, comment.AuthorId);
            Assert.Equal(post.Id, comment.PostId);
            Assert.Equal("Mój komentarz", comment.Content);
            Assert.Equal(1, comment.Version);
            Assert.Null(comment.UpdatedAt);
            Assert.True(comment.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        }

        [Fact]
        public async Task Create_OnMissingPost_Returns404()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            var response = await client.PostAsJsonAsync("/api/posts/999999/comments", new { content = "x" });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("     ")]
        public async Task Create_RejectsEmptyOrWhitespaceContent(string content)
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();

            var response = await client.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Create_EnforcesMaxLength()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();

            var atLimit = await client.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content = new string('a', CommentLimits.ContentMaxLength) });
            var overLimit = await client.PostAsJsonAsync($"/api/posts/{post.Id}/comments", new { content = new string('a', CommentLimits.ContentMaxLength + 1) });

            Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        }

        [Fact]
        public async Task Owner_CanEditWithCurrentETag_ThenStaleETagReturns412()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            var (comment, etag) = await client.CreateCommentAsync(post.Id, "Przed");

            var edit = await client.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", etag, new { content = "Po" }));
            var edited = await edit.ReadAsync<CommentResponse>();

            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Equal("\"2\"", edit.Headers.ETag!.Tag);
            Assert.Equal("Po", edited.Content);
            Assert.NotNull(edited.UpdatedAt);
            Assert.Equal(comment.CreatedAt, edited.CreatedAt);

            var stale = await client.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", etag, new { content = "Stara wersja" }));

            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal("Po", (await (await client.GetAsync($"/api/comments/{comment.Id}")).ReadAsync<CommentResponse>()).Content);
        }

        [Fact]
        public async Task EditWithoutRealChange_KeepsVersionAndUpdatedAtNull()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            var (comment, etag) = await client.CreateCommentAsync(post.Id, "Bez zmian");

            var edit = await client.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", etag, new { content = "Bez zmian " }));
            var result = await edit.ReadAsync<CommentResponse>();

            Assert.Equal(1, result.Version);
            Assert.Null(result.UpdatedAt);
        }

        [Fact]
        public async Task EditAndDelete_WithoutIfMatch_Return428()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            var (comment, _) = await client.CreateCommentAsync(post.Id);

            var edit = await client.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", body: new { content = "x" }));
            var delete = await client.SendAsync(Request(HttpMethod.Delete, $"/api/comments/{comment.Id}"));

            Assert.Equal((HttpStatusCode)428, edit.StatusCode);
            Assert.Equal((HttpStatusCode)428, delete.StatusCode);
        }

        [Fact]
        public async Task OtherUser_CannotEditOrDeleteComment_EvenWithCurrentETag()
        {
            var alice = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("a"));
            var bob = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("b"));
            var post = await alice.CreatePostAsync();
            var (comment, etag) = await alice.CreateCommentAsync(post.Id, "Komentarz Alicji");

            var edit = await bob.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", etag, new { content = "Przejęty" }));
            var delete = await bob.SendAsync(Request(HttpMethod.Delete, $"/api/comments/{comment.Id}", etag));

            Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            Assert.Equal("Komentarz Alicji", (await (await alice.GetAsync($"/api/comments/{comment.Id}")).ReadAsync<CommentResponse>()).Content);
        }

        [Fact]
        public async Task DeleteThenStaleEdit_Returns404_WithoutResurrection()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            var (comment, etag) = await client.CreateCommentAsync(post.Id);

            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Delete, $"/api/comments/{comment.Id}", etag))).StatusCode);

            var edit = await client.SendAsync(Request(HttpMethod.Put, $"/api/comments/{comment.Id}", etag, new { content = "Wskrzeszenie" }));

            Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/comments/{comment.Id}")).StatusCode);
        }

        [Fact]
        public async Task CommentsPage_IsOrderedOldestFirst_AndPaginatesWithCursor()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();
            var ids = new List<int>();
            for (var i = 0; i < 5; i++)
                ids.Add((await client.CreateCommentAsync(post.Id, $"Komentarz {i}")).Comment.Id);

            var collected = new List<int>();
            string? cursor = null;
            do
            {
                var page = await (await _factory.CreateHttpsClient()
                    .GetAsync($"/api/posts/{post.Id}/comments?limit=2" + (cursor is null ? "" : $"&cursor={cursor}")))
                    .ReadAsync<KeysetPage<CommentResponse>>();
                collected.AddRange(page.Items.Select(c => c.Id));
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            Assert.Equal(ids, collected);
        }

        [Theory]
        [InlineData("?limit=0")]
        [InlineData("?limit=51")]
        [InlineData("?cursor=garbage")]
        public async Task CommentsPage_RejectsInvalidParameters(string query)
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());
            var post = await client.CreatePostAsync();

            var response = await client.GetAsync($"/api/posts/{post.Id}/comments{query}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CommentsOfMissingPost_Return404()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateHttpsClient().GetAsync("/api/posts/999999/comments")).StatusCode);
        }
    }
}
