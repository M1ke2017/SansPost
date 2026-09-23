using System.Net;
using System.Net.Http.Json;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    public class AuthorizationTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public AuthorizationTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        public static TheoryData<string, string> ProtectedEndpoints => new()
        {
            { "POST", "/api/posts" },
            { "PUT", "/api/posts/1" },
            { "DELETE", "/api/posts/1" },
            { "GET", "/api/posts/mine" },
            { "POST", "/api/posts/1/comments" },
            { "DELETE", "/api/comments/1" },
            { "POST", "/api/posts/1/likes/toggle" },
            { "GET", "/api/auth/me" },
            { "PUT", "/api/users/1/role" },
            { "PUT", "/api/users/1/subscription" }
        };

        [Theory]
        [MemberData(nameof(ProtectedEndpoints))]
        public async Task Anonymous_CannotAccessProtectedEndpoints(string method, string url)
        {
            var client = _factory.CreateHttpsClient();

            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = JsonContent.Create(new { })
            });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task User_CannotCallAdminEndpoints()
        {
            var targetId = await _factory.CreateUserAsync(TestUsers.UniqueName());
            var client = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName());

            var role = await client.PutAsJsonAsync($"/api/users/{targetId}/role", new { role = "Admin" });
            var subscription = await client.PutAsJsonAsync($"/api/users/{targetId}/subscription", new { type = "Premium" });

            Assert.Equal(HttpStatusCode.Forbidden, role.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, subscription.StatusCode);
            Assert.Equal(UserRole.User, _factory.WithScope(db => db.Users.Single(u => u.Id == targetId).Role));
        }

        [Fact]
        public async Task User_CannotPromoteThemselves()
        {
            var username = TestUsers.UniqueName();
            var client = await _factory.CreateAuthenticatedApiClientAsync(username);
            var ownId = _factory.WithScope(db => db.Users.Single(u => u.Username == username).Id);

            var response = await client.PutAsJsonAsync($"/api/users/{ownId}/role", new { role = "Admin" });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task Admin_CanChangeRoleAndSubscription()
        {
            var targetId = await _factory.CreateUserAsync(TestUsers.UniqueName());
            var admin = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName(), UserRole.Admin);

            var role = await admin.PutAsJsonAsync($"/api/users/{targetId}/role", new { role = "Admin" });
            var subscription = await admin.PutAsJsonAsync($"/api/users/{targetId}/subscription", new { type = "Premium" });

            Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, subscription.StatusCode);
            Assert.Equal(UserRole.Admin, _factory.WithScope(db => db.Users.Single(u => u.Id == targetId).Role));
        }

        [Fact]
        public async Task PublicReadEndpoints_RemainAnonymous()
        {
            var client = _factory.CreateHttpsClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/posts")).StatusCode);
        }
    }
}
