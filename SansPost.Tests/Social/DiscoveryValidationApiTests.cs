using System.Net;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Social
{
    // Walidacja wejścia discovery — odrzucana przed zapytaniem do bazy (szybkie testy, SQLite).
    [Trait("Category", "Discovery")]
    public class DiscoveryValidationApiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public DiscoveryValidationApiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        public static TheoryData<string> InvalidSearches => new()
        {
            "/api/search/posts",
            "/api/search/posts?q=",
            "/api/search/posts?q=%20%20%20",
            "/api/search/posts?q=a",
            "/api/search/posts?q=" + new string('x', 101),
            "/api/search/posts?q=ok&limit=0",
            "/api/search/posts?q=ok&limit=51",
            "/api/search/posts?q=ok&cursor=garbage",
            "/api/search/posts?q=ok&category=Sport",
            "/api/search/users?q=a",
            "/api/search/users?q=" + new string('x', 51),
            "/api/search/users?q=ab&limit=21",
            "/api/posts?sort=Popular&cursor=garbage",
            "/api/posts?sort=Trending"
        };

        [Theory]
        [MemberData(nameof(InvalidSearches))]
        public async Task InvalidDiscoveryInput_Returns400ProblemDetails(string url)
        {
            var response = await _factory.CreateHttpsClient().GetAsync(url);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }
}
