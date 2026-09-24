using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Moderation
{
    public sealed class TightLimitsFactory : ConfiguredFactory
    {
        public TightLimitsFactory() : base(new Dictionary<string, string?>
        {
            ["RateLimiting:Search:PermitLimit"] = "3",
            ["RateLimiting:Writes:PermitLimit"] = "2"
        })
        {
        }
    }

    // TestServer nie ma adresu IP klienta — wszyscy klienci trafiają do tej samej partycji IP ("unknown"),
    // co symuluje wielu użytkowników za jednym NAT.
    [Trait("Category", "RateLimiting")]
    public class PublicDemoRateLimitTests : IClassFixture<TightLimitsFactory>
    {
        private readonly TightLimitsFactory _factory;

        public PublicDemoRateLimitTests(TightLimitsFactory factory)
        {
            _factory = factory;
        }

        // AC — wyszukiwanie ma własny limit per IP: 429 + Retry-After + ProblemDetails.
        [Fact]
        public async Task Search_OverLimit_Returns429WithRetryAfter()
        {
            var client = _factory.CreateHttpsClient();

            for (var i = 0; i < 3; i++)
                Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/search/users?q=ab")).StatusCode);

            var limited = await client.GetAsync("/api/search/users?q=ab");

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
            Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        }

        // AD, AE — zapisy zalogowanych: partycja per UserId. Użytkownik A wyczerpuje limit (429 + Retry-After),
        // użytkownik B za "tym samym IP" nadal może pisać.
        [Fact]
        public async Task AuthenticatedWrites_ArePartitionedByUser_NotByIp()
        {
            var alice = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("a"));
            var bob = await _factory.CreateAuthenticatedApiClientAsync(TestUsers.UniqueName("b"));
            object Post(int i) => new { title = $"Post numer {i}", content = "x", category = "General" };

            Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/posts", Post(1))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/posts", Post(2))).StatusCode);
            var limited = await alice.PostAsJsonAsync("/api/posts", Post(3));

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
            var problem = await limited.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal("write-rate-limited", problem!.Extensions["code"]!.ToString());

            Assert.Equal(HttpStatusCode.Created, (await bob.PostAsJsonAsync("/api/posts", Post(1))).StatusCode);
        }

        [Fact]
        public async Task PublicFeedReads_AreNotLimitedByWriteOrSearchQuota()
        {
            var client = _factory.CreateHttpsClient();

            for (var i = 0; i < 10; i++)
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/posts")).StatusCode);
        }
    }
}
