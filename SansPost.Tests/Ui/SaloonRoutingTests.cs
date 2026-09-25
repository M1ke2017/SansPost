using System.Net;
using System.Text.RegularExpressions;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Ui
{
    // Sprint 13 — "/" to świadome wejście (Entrance), "/saloon" to główny hub; logowanie i rejestracja prowadzą do Saloonu.
    [Trait("Category", "Ui")]
    public class SaloonRoutingTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public SaloonRoutingTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string? returnUrl)
        {
            var token = SansPostFactory.ExtractAntiforgeryToken(await client.GetStringAsync("/login"));
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = $"{username}@example.com",
                ["Password"] = TestUsers.Password
            };
            if (returnUrl is not null)
                form["ReturnUrl"] = returnUrl;
            return await client.PostAsync("/auth/login", new FormUrlEncodedContent(form));
        }

        [Fact]
        public async Task Root_IsEntrance_WithWayIntoSaloon_NotTheFeed()
        {
            var response = await _factory.CreateHttpsClient().GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("id=\"entrance-title\"", html);
            Assert.Matches("<a[^>]*href=\"/saloon\"[^>]*>Wejd", html);
            Assert.DoesNotContain("class=\"post-card", html);
        }

        // Gość czyta bez konta: hub i dawny alias /posts pokazują feed.
        [Theory]
        [InlineData("/saloon")]
        [InlineData("/posts")]
        [InlineData("/saloon?sort=popular")]
        public async Task Saloon_IsTheHub_ForGuests(string path)
        {
            var response = await _factory.CreateHttpsClient().GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("aria-label=\"Sortowanie post", html);   // polskie znaki są kodowane encjami
            Assert.DoesNotContain("id=\"entrance-title\"", html);
        }

        // Logo i "Odkrywaj" w aplikacji prowadzą do Saloonu, nie do wejścia.
        [Fact]
        public async Task Logo_AndPrimaryNavigation_PointToSaloon()
        {
            var html = await _factory.CreateHttpsClient().GetStringAsync("/categories");

            Assert.Matches("<a class=\"brand\" href=\"/saloon\"", html);
            var primaryNav = Regex.Match(html, "<nav class=\"primary-nav\".*?</nav>", RegexOptions.Singleline).Value;
            Assert.Contains("href=\"/saloon\"", primaryNav);
            Assert.DoesNotMatch("href=\"/\"", primaryNav);
        }

        [Theory]
        [InlineData(null, "/saloon")]                                   // domyślnie Saloon
        [InlineData("/", "/saloon")]                                    // powrót do samego wejścia → Saloon
        [InlineData("/me", "/me")]                                      // bezpieczny ReturnUrl zostaje
        [InlineData("/post-view/1?x=1", "/post-view/1?x=1")]
        [InlineData("https://evil.example.com/phish", "/saloon")]       // open redirect zablokowany
        [InlineData("//evil.example.com", "/saloon")]
        public async Task Login_RedirectsToSaloon_UnlessSafeReturnUrl(string? returnUrl, string expected)
        {
            var username = TestUsers.UniqueName("sl");
            await _factory.CreateUserAsync(username);

            var login = await LoginAsync(_factory.CreateHttpsClient(), username, returnUrl);

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal(expected, login.Headers.Location!.OriginalString);
        }

        // Rejestracja → logowanie (komunikat "Konto utworzone") → Saloon. Wylogowanie → wejście.
        [Fact]
        public async Task Register_ThenLogin_LandsInSaloon_Logout_ReturnsToEntrance()
        {
            var client = _factory.CreateHttpsClient();
            var registerPage = await client.GetStringAsync("/register");
            var alias = Regex.Match(registerPage, "name=\"Username\" value=\"([A-Za-z]+)\"").Groups[1].Value;
            Assert.NotEmpty(alias);

            var email = $"{TestUsers.UniqueName("rg")}@example.com";
            var register = await client.PostAsync("/auth/register", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = SansPostFactory.ExtractAntiforgeryToken(registerPage),
                ["Username"] = alias,
                ["Email"] = email,
                ["Password"] = TestUsers.Password,
                ["ConfirmPassword"] = TestUsers.Password
            }));
            Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
            Assert.Equal("/login?registered=1", register.Headers.Location!.OriginalString);

            var loginPage = await client.GetStringAsync("/login?registered=1");
            var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = SansPostFactory.ExtractAntiforgeryToken(loginPage),
                ["Email"] = email,
                ["Password"] = TestUsers.Password
            }));
            Assert.Equal("/saloon", login.Headers.Location!.OriginalString);

            var panel = await client.GetStringAsync("/me");   // formularz wylogowania w prerenderze (menu Saloonu jest zwinięte)
            var logout = await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = SansPostFactory.ExtractAntiforgeryToken(panel)
            }));
            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
            Assert.Equal("/", logout.Headers.Location!.OriginalString);
        }
    }
}
