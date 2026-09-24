using System.Net;
using System.Net.Http.Json;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Security
{
    // Blazor UI: logowanie/wylogowanie zwykłym HTTP POST, stan oparty o cookie.
    public class CookieAuthenticationTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public CookieAuthenticationTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private async Task<(HttpClient Client, HttpResponseMessage Login)> LoginWithCookieAsync(string username, string returnUrl = "/user-panel")
        {
            var client = _factory.CreateHttpsClient();
            var token = SansPostFactory.ExtractAntiforgeryToken(await client.GetStringAsync("/login"));

            var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = $"{username}@example.com",
                ["Password"] = TestUsers.Password,
                ["ReturnUrl"] = returnUrl
            }));

            return (client, login);
        }

        [Fact]
        public async Task ProtectedPage_RedirectsAnonymousUserToLogin()
        {
            var response = await _factory.CreateHttpsClient().GetAsync("/user-panel");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login?returnUrl=", response.Headers.Location!.OriginalString.Replace("https://localhost", ""));
        }

        [Fact]
        public async Task CookieLogin_SetsHardenedCookie_AndUnlocksProtectedPage()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);

            var (client, login) = await LoginWithCookieAsync(username);

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal("/user-panel", login.Headers.Location!.OriginalString);

            var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("SansPost.Auth="));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

            var panel = await client.GetAsync("/user-panel");
            Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
            Assert.Contains("Twoje konto", await panel.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task CookieLogin_WithWrongPassword_RedirectsBackWithGenericError()
        {
            var client = _factory.CreateHttpsClient();
            var token = SansPostFactory.ExtractAntiforgeryToken(await client.GetStringAsync("/login"));

            var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = "nobody@example.com",
                ["Password"] = "wrong-password-1"
            }));

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.StartsWith("/login?error=invalid", login.Headers.Location!.OriginalString);
            var cookies = login.Headers.TryGetValues("Set-Cookie", out var values) ? values : Array.Empty<string>();
            Assert.DoesNotContain(cookies, c => c.StartsWith("SansPost.Auth="));
        }

        [Fact]
        public async Task CookieLogin_WithoutAntiforgeryToken_IsRejected()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);

            var response = await _factory.CreateHttpsClient().PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = $"{username}@example.com",
                ["Password"] = TestUsers.Password
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task CookieLogin_IgnoresExternalReturnUrl()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);

            var (_, login) = await LoginWithCookieAsync(username, returnUrl: "https://evil.example.com/phish");

            Assert.Equal("/posts", login.Headers.Location!.OriginalString);
        }

        [Fact]
        public async Task Logout_RemovesCookie()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);
            var (client, _) = await LoginWithCookieAsync(username);

            var token = SansPostFactory.ExtractAntiforgeryToken(await client.GetStringAsync("/user-panel"));
            var logout = await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));

            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
            Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("SansPost.Auth=;") && c.Contains("expires=Thu, 01 Jan 1970"));
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/user-panel")).StatusCode);
        }

        [Fact]
        public async Task UiCookie_DoesNotAuthenticateRestApi()
        {
            var username = TestUsers.UniqueName();
            await _factory.CreateUserAsync(username);
            var (client, _) = await LoginWithCookieAsync(username);

            var response = await client.PostAsJsonAsync("/api/posts", new { title = "t", content = "c", category = "General" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
