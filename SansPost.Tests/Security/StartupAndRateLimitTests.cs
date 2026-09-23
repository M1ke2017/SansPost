using System.Net;
using System.Net.Http.Json;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    public class RateLimitedFactory : SansPostFactory
    {
        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                settings["RateLimiting:Auth:PermitLimit"] = "3";
                return settings;
            }
        }
    }

    public class RateLimitingTests : IClassFixture<RateLimitedFactory>
    {
        private readonly RateLimitedFactory _factory;

        public RateLimitingTests(RateLimitedFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task LoginAttempts_AreLimitedPerClient()
        {
            var client = _factory.CreateHttpsClient();
            var body = new { email = "ghost@example.com", password = "wrong-password-1" };

            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", body)).StatusCode);

            var limited = await client.PostAsJsonAsync("/api/auth/login", body);

            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        }
    }

    public class StartupConfigurationTests
    {
        private sealed class MisconfiguredFactory : SansPostFactory
        {
            private readonly string _key;
            private readonly string _value;

            public MisconfiguredFactory(string key, string value)
            {
                _key = key;
                _value = value;
            }

            protected override IDictionary<string, string?> Settings
            {
                get
                {
                    var settings = base.Settings;
                    settings[_key] = _value;
                    return settings;
                }
            }
        }

        [Theory]
        [InlineData("Jwt:Key", "", "Jwt:Key")]
        [InlineData("Jwt:Key", "too-short", "Jwt:Key")]
        [InlineData("Jwt:AccessTokenLifetime", "08:00:00", "AccessTokenLifetime")]
        [InlineData("ConnectionStrings:DefaultConnection", "", "DefaultConnection")]
        public void InvalidConfiguration_FailsFastOnStartup(string key, string value, string expectedInMessage)
        {
            using var factory = new MisconfiguredFactory(key, value);

            var exception = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(exception);
            Assert.Contains(expectedInMessage, exception!.ToString());
        }
    }
}
