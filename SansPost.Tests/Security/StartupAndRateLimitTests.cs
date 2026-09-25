using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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

namespace SansPost.Tests.Security
{
    // Sprint 10 (A6): jeden limiter wyszukiwania dla REST i Blazor. Wyszukiwarka UI woła SearchRateLimiter bezpośrednio
    // (tę samą instancję singletonu) — wyczerpanie limitu w UI blokuje REST dla tego samego klienta i odwrotnie.
    [Trait("Category", "RateLimiting")]
    public class SharedSearchLimiterTests
    {
        [Fact]
        public async Task UiSearches_ConsumeTheSameLimit_AsRestSearch()
        {
            using var factory = new SansPost.Tests.Moderation.TightLimitsFactory();
            var client = factory.CreateHttpsClient();
            var limiter = factory.Services.GetRequiredService<SansPost.Infrastructure.Security.SearchRateLimiter>();

            // TestServer nie ma adresu IP klienta — REST trafia do klucza "ip:unknown", jak gość w UI bez adresu.
            var guestKey = SansPost.Infrastructure.Security.SearchRateLimiter.ClientKey(null, null);
            Assert.True(limiter.TryAcquire(guestKey).Acquired);
            Assert.True(limiter.TryAcquire(guestKey).Acquired);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/search/users?q=ab")).StatusCode);

            var restLimited = await client.GetAsync("/api/search/posts?q=ab");
            Assert.Equal(HttpStatusCode.TooManyRequests, restLimited.StatusCode);
            Assert.NotNull(restLimited.Headers.RetryAfter);

            var (uiAcquired, retryAfter) = limiter.TryAcquire(guestKey);
            Assert.False(uiAcquired);
            Assert.NotNull(retryAfter);
        }

        [Fact]
        public void Partitions_AreSeparatePerUserAndPerAddress()
        {
            using var limiter = new SansPost.Infrastructure.Security.SearchRateLimiter(
                new SansPost.Infrastructure.Security.RateLimitWindowOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });
            string Key(int? user, string? ip) => SansPost.Infrastructure.Security.SearchRateLimiter.ClientKey(user, ip);

            Assert.True(limiter.TryAcquire(Key(null, "10.0.0.1")).Acquired);
            Assert.False(limiter.TryAcquire(Key(null, "10.0.0.1")).Acquired);
            Assert.True(limiter.TryAcquire(Key(null, "10.0.0.2")).Acquired);
            Assert.True(limiter.TryAcquire(Key(7, "10.0.0.1")).Acquired);    // zalogowany: własna partycja (NAT)
            Assert.False(limiter.TryAcquire(Key(7, "10.0.0.9")).Acquired);   // …niezależna od adresu
            Assert.Equal("ip:unknown", Key(null, ""));
        }
    }

    // Sprint 10 (A7): nieznany adres — HTTP 404 z produktową stroną; znane trasy nadal 200.
    public class NotFoundStatusTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public NotFoundStatusTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task UnknownRoute_Returns404_WithProductNotFoundPage()
        {
            var client = _factory.CreateHttpsClient();

            var missing = await client.GetAsync("/this-route-does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("text/html", missing.Content.Headers.ContentType?.MediaType);
            var html = await missing.Content.ReadAsStringAsync();
            Assert.Contains("Nie ma tu nic do czytania", html);
            Assert.Contains("blazor.server.js", html);   // pełna strona aplikacji (circuit działa dalej)

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/categories")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);
        }
    }
}
