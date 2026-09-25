using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;
using static SansPost.Tests.TestInfrastructure.ApiTestHelpers;

namespace SansPost.Tests.Moderation
{
    public class ConfiguredFactory : SansPostFactory
    {
        private readonly IDictionary<string, string?> _overrides;

        public ConfiguredFactory(IDictionary<string, string?> overrides)
        {
            _overrides = overrides;
        }

        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                foreach (var (key, value) in _overrides)
                    settings[key] = value;
                return settings;
            }
        }
    }

    // Pojemność publicznego demo, bootstrap admina i status publiczny (szybkie testy; współbieżność — PostgreSQL).
    [Trait("Category", "PublicDemo")]
    public class PublicDemoSafetyApiTests
    {
        private static object Registration(string username) =>
            new { username, email = $"{username}@example.com", password = TestUsers.Password };

        private static string? CodeOf(ProblemDetails? problem) =>
            problem?.Extensions.TryGetValue("code", out var code) == true ? code?.ToString() : null;

        // C — rejestracja wyłączona: kontrolowany ProblemDetails, konto nie powstaje.
        [Fact]
        public async Task RegistrationDisabled_ReturnsProblem_AndCreatesNothing()
        {
            using var factory = new ConfiguredFactory(new Dictionary<string, string?> { ["PublicDemo:RegistrationEnabled"] = "false" });
            var client = factory.CreateHttpsClient();
            var username = TestUsers.UniqueName();

            var response = await client.PostAsJsonAsync("/api/auth/register", Registration(username));
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("registration-disabled", CodeOf(problem));
            Assert.Equal("urn:sanspost:problem:registration-disabled", problem!.Type);
            Assert.Equal(0, factory.WithScope(db => db.Users.Count(u => u.Username == username)));
            Assert.False((await (await client.GetAsync("/api/public/status")).ReadAsync<PublicStatus>()).RegistrationAvailable);
        }

        // A, B — poniżej limitu sukces, na limicie 409 registration-capacity-reached (nie 429).
        [Fact]
        public async Task Registration_AtCapacity_Returns409CapacityReached()
        {
            using var factory = new ConfiguredFactory(new Dictionary<string, string?> { ["PublicDemo:MaxPublicAccounts"] = "2" });
            var client = factory.CreateHttpsClient();

            Assert.True((await (await client.GetAsync("/api/public/status")).ReadAsync<PublicStatus>()).RegistrationAvailable);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Registration(TestUsers.UniqueName()))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Registration(TestUsers.UniqueName()))).StatusCode);

            var full = await client.PostAsJsonAsync("/api/auth/register", Registration(TestUsers.UniqueName()));

            Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            Assert.Equal("registration-capacity-reached", CodeOf(await full.Content.ReadFromJsonAsync<ProblemDetails>()));
            Assert.False((await (await client.GetAsync("/api/public/status")).ReadAsync<PublicStatus>()).RegistrationAvailable);
        }

        [Fact]
        public async Task PublicStatus_ExposesOnlyAvailabilityFlag()
        {
            using var factory = new SansPostFactory();

            var json = await factory.CreateHttpsClient().GetStringAsync("/api/public/status");
            using var document = JsonDocument.Parse(json);

            Assert.Equal(new[] { "registrationAvailable" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        }

        [Fact]
        public async Task BootstrapAdmin_CreatesFirstAdminFromConfiguration_WhoCanModerate()
        {
            using var factory = new ConfiguredFactory(new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Enabled"] = "true",
                ["BootstrapAdmin:Username"] = "rootadmin",
                ["BootstrapAdmin:Email"] = "root@example.com",
                ["BootstrapAdmin:Password"] = "bootstrap-secret-123"
            });
            var client = factory.CreateHttpsClient();

            var admin = factory.WithScope(db => db.Users.Single(u => u.Role == UserRole.Admin));
            Assert.Equal("rootadmin", admin.Username);
            Assert.True(PasswordHasher.IsSupportedHash(admin.PasswordHash));

            var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "root@example.com", password = "bootstrap-secret-123" });
            var tokens = await login.ReadAsync<TokenResponse>();
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/moderation/reports")).StatusCode);
        }

        [Theory]
        [InlineData("BootstrapAdmin:Password", "short")]
        [InlineData("BootstrapAdmin:Email", "not-an-email")]
        [InlineData("PublicDemo:MaxPublicAccounts", "0")]
        [InlineData("PublicDemo:MaxPublicAccounts", "100000")]   // ponad bezpieczną pojemność generatora przydomków
        public void InvalidSafetyConfiguration_FailsFastOnStartup(string key, string value)
        {
            var settings = new Dictionary<string, string?>
            {
                ["BootstrapAdmin:Enabled"] = "true",
                ["BootstrapAdmin:Username"] = "rootadmin",
                ["BootstrapAdmin:Email"] = "root@example.com",
                ["BootstrapAdmin:Password"] = "bootstrap-secret-123",
                [key] = value
            };
            using var factory = new ConfiguredFactory(settings);

            Assert.NotNull(Record.Exception(() => factory.CreateClient()));
        }

        private sealed record PublicStatus(bool RegistrationAvailable);
    }
}
