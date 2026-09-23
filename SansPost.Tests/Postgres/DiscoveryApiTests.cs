using System.Net;
using System.Text.Json;
using SansPost.Features;
using SansPost.Features.Posts;
using SansPost.Features.Search;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Postgres
{
    // Publiczne endpointy discovery przez pełny pipeline HTTP (JWT, polityki) na prawdziwym PostgreSQL.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Discovery")]
    public sealed class DiscoveryApiTests : IDisposable
    {
        private readonly PostgresApiFactory _factory;

        public DiscoveryApiTests(PostgresFixture pg)
        {
            _factory = new PostgresApiFactory(pg.ConnectionString);
        }

        public void Dispose() => _factory.Dispose();

        private static string NewToken() => "api" + Guid.NewGuid().ToString("N")[..10];

        [DockerFact]
        public async Task Search_IsAnonymous_AndViewerLikeStateComesOnlyFromJwt()
        {
            var token = NewToken();
            var username = TestUsers.UniqueName("d");
            var author = await _factory.CreateAuthenticatedApiClientAsync(username);
            var post = await author.CreatePostAsync($"Szukany {token}");
            await author.SendAsync(Request(HttpMethod.Put, $"/api/posts/{post.Id}/like"));

            var anonymous = await (await _factory.CreateHttpsClient().GetAsync($"/api/search/posts?q={token}")).ReadAsync<KeysetPage<PostSearchResult>>();
            var spoofed = await (await _factory.CreateHttpsClient().GetAsync($"/api/search/posts?q={token}&viewerUserId={post.AuthorId}&userId={post.AuthorId}")).ReadAsync<KeysetPage<PostSearchResult>>();
            var authenticated = await (await author.GetAsync($"/api/search/posts?q={token}")).ReadAsync<KeysetPage<PostSearchResult>>();

            Assert.False(Assert.Single(anonymous.Items).LikedByCurrentUser);
            Assert.False(Assert.Single(spoofed.Items).LikedByCurrentUser);
            var own = Assert.Single(authenticated.Items);
            Assert.True(own.LikedByCurrentUser);
            Assert.Equal((1, username), (own.LikeCount, own.AuthorUsername));
        }

        [DockerFact]
        public async Task SearchResult_ExposesNoAuthOrFtsInternals()
        {
            var token = NewToken();
            var author = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("d"));
            await author.CreatePostAsync($"Kontrakt {token}");

            var json = await (await _factory.CreateHttpsClient().GetAsync($"/api/search/posts?q={token}")).Content.ReadAsStringAsync();

            foreach (var forbidden in new[] { "searchVector", "tsvector", "rank", "score", "email", "passwordHash", "content\"" })
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        [DockerFact]
        public async Task UserSearch_And_Categories_ArePublicAndSafe()
        {
            var username = TestUsers.UniqueName("Dir");
            await _factory.CreateUserAsync(username);

            var usersResponse = await _factory.CreateHttpsClient().GetAsync($"/api/search/users?q={username[..6].ToLowerInvariant()}");
            using var users = JsonDocument.Parse(await usersResponse.Content.ReadAsStringAsync());
            var match = users.RootElement.EnumerateArray().Single(u => u.GetProperty("username").GetString() == username);

            Assert.Equal(new[] { "userId", "username", "joinedAt", "postCount" }, match.EnumerateObject().Select(p => p.Name));

            var categories = await (await _factory.CreateHttpsClient().GetAsync("/api/categories")).ReadAsync<List<CategorySummaryResponse>>();
            Assert.Equal(Enum.GetValues<PostCategory>(), categories.Select(c => c.Category));
        }

        [DockerFact]
        public async Task PopularFeed_OverHttp_PaginatesWithCursor()
        {
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("d"));
            for (var i = 0; i < 3; i++)
                await client.CreatePostAsync($"Popularny {i}");
            var authorId = (await (await client.GetAsync("/api/auth/me")).ReadAsync<SansPost.Features.Identity.UserResponse>()).Id;

            var first = await (await _factory.CreateHttpsClient().GetAsync($"/api/users/{authorId}/posts?sort=Popular&limit=2")).ReadAsync<KeysetPage<PostSummaryResponse>>();
            var second = await (await _factory.CreateHttpsClient().GetAsync($"/api/users/{authorId}/posts?sort=Popular&limit=2&cursor={first.NextCursor}")).ReadAsync<KeysetPage<PostSummaryResponse>>();

            Assert.Equal(2, first.Items.Count);
            Assert.Single(second.Items);
            Assert.False(second.HasMore);
        }
    }
}
