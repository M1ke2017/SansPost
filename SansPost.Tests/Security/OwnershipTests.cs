using System.Net;
using System.Net.Http.Json;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    public class OwnershipTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public OwnershipTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private static object PostBody(string title) => new { title, content = "Treść", category = "General" };

        [Fact]
        public async Task UserB_CannotEditOrDeletePostOfUserA()
        {
            var alice = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("a"));
            var bob = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("b"));

            var created = await alice.PostAsJsonAsync("/api/posts", PostBody("Post Alicji"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var post = (await created.Content.ReadFromJsonAsync<PostDetailsResponse>(SansPostFactory.Json))!;

            // Bob zna aktualny ETag — odrzucenie wynika z ownership, nie z wersji.
            var edit = await bob.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/posts/{post.Id}")
            {
                Content = JsonContent.Create(PostBody("Przejęty")),
                Headers = { IfMatch = { created.Headers.ETag! } }
            });
            var delete = await bob.DeleteAsync($"/api/posts/{post.Id}");

            Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
            Assert.Equal("Post Alicji", _factory.WithScope(db => db.Posts.Single(p => p.Id == post.Id).Title));

            Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/posts/{post.Id}")).StatusCode);
        }

        [Fact]
        public async Task OwnerComesFromToken_NotFromRequestBody()
        {
            var victimId = await _factory.CreateUserAsync(TestUsers.UniqueName("v"));
            var attackerName = TestUsers.UniqueName("x");
            var attacker = await _factory.CreateAuthenticatedApiClientAsync(attackerName);

            var created = await attacker.PostAsJsonAsync("/api/posts", new
            {
                title = "Podszywka",
                content = "Treść",
                category = "General",
                userId = victimId,
                authorId = victimId
            });

            var post = (await created.Content.ReadFromJsonAsync<PostDetailsResponse>(SansPostFactory.Json))!;
            Assert.Equal(attackerName, post.AuthorUsername);
            Assert.NotEqual(victimId, post.AuthorId);
        }
    }
}
