using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Ui
{
    // Rejestracja z przydomkiem przez pełny pipeline (Blazor prerender + natywny POST /auth/register + REST).
    [Trait("Category", "Ui")]
    [Trait("Category", "Aliases")]
    public class RegistrationAliasUiTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public RegistrationAliasUiTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private static string HiddenAlias(string html)
        {
            var match = Regex.Match(html, "<input type=\"hidden\" name=\"Username\" value=\"([^\"]*)\"");
            Assert.True(match.Success, "Brak ukrytego pola z przydomkiem.");
            return match.Groups[1].Value;
        }

        private async Task<HttpResponseMessage> PostRegisterAsync(HttpClient client, string html, string? username, string email) =>
            await client.PostAsync("/auth/register", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = SansPostFactory.ExtractAntiforgeryToken(html),
                ["Username"] = username ?? string.Empty,
                ["Email"] = email,
                ["Password"] = TestUsers.Password,
                ["ConfirmPassword"] = TestUsers.Password
            }));

        // G16, G18 — brak edytowalnego pola nazwy; widoczny przydomek z generatora (ten sam w polu ukrytym).
        [Fact]
        public async Task RegisterPage_ShowsGeneratedAlias_WithoutEditableUsernameInput()
        {
            var html = await _factory.CreateHttpsClient().GetStringAsync("/register");

            Assert.DoesNotMatch("<input[^>]*name=\"Username\"[^>]*type=\"text\"", html);
            Assert.DoesNotMatch("<input[^>]*type=\"text\"[^>]*name=\"Username\"", html);
            var alias = HiddenAlias(html);
            Assert.True(WesternAliases.IsCurated(alias));
            Assert.Contains($">{alias}</output>", html);
            Assert.Contains("Losuj inny", html);
        }

        // G6 — podmieniony ukryty input (dowolna nazwa) nie tworzy konta.
        [Fact]
        public async Task TamperedAlias_InFormPost_IsRejected()
        {
            var client = _factory.CreateHttpsClient();
            var html = await client.GetStringAsync("/register");

            var response = await PostRegisterAsync(client, html, "Administrator", "tamper@example.com");

            Assert.Equal("/register?error=invalid", response.Headers.Location!.OriginalString);
            Assert.False(_factory.WithScope(db => db.Users.Any(u => u.NormalizedEmail == "TAMPER@EXAMPLE.COM")));
        }

        // D6 — przydomek zajęty między wyświetleniem a wysłaniem: przekierowanie z komunikatem i nowym przydomkiem, bez 500.
        [Fact]
        public async Task AliasTakenBeforeSubmit_RedirectsWithFreshSuggestion()
        {
            var client = _factory.CreateHttpsClient();
            var html = await client.GetStringAsync("/register");
            var alias = HiddenAlias(html);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register",
                new { username = alias, email = "first-taker@example.com", password = TestUsers.Password })).StatusCode);

            var response = await PostRegisterAsync(client, html, alias, "second-taker@example.com");

            Assert.Equal("/register?error=alias-taken", response.Headers.Location!.OriginalString);
            var retry = await client.GetStringAsync("/register?error=alias-taken");
            Assert.Contains("Ten przydomek zosta", retry);   // polskie znaki są w HTML kodowane encjami
            Assert.NotEqual(alias, HiddenAlias(retry));
        }

        [Fact]
        public async Task RegisterWithAlias_CreatesAccountWithThatAlias()
        {
            var client = _factory.CreateHttpsClient();
            var html = await client.GetStringAsync("/register");
            var alias = HiddenAlias(html);

            var response = await PostRegisterAsync(client, html, alias, "alias-owner@example.com");

            Assert.Equal("/login?registered=1", response.Headers.Location!.OriginalString);
            Assert.Equal(alias, _factory.WithScope(db => db.Users.Single(u => u.NormalizedEmail == "ALIAS-OWNER@EXAMPLE.COM").Username));
        }

        // G7 — propozycja przez REST: poprawny przydomek, bez tworzenia konta.
        [Fact]
        public async Task AliasSuggestionEndpoint_ReturnsCuratedAlias_AndCreatesNothing()
        {
            var before = _factory.WithScope(db => db.Users.Count());
            var client = _factory.CreateHttpsClient();

            for (var i = 0; i < 3; i++)
            {
                var suggestion = await client.GetFromJsonAsync<AliasSuggestionResponse>("/api/auth/alias-suggestion");
                Assert.True(WesternAliases.IsCurated(suggestion!.Alias));
            }

            Assert.Equal(before, _factory.WithScope(db => db.Users.Count()));
        }

        // REST: rejestracja bez przydomka dostaje przydział serwera; dowolna nazwa → 400.
        [Fact]
        public async Task ApiRegister_AssignsAliasWhenMissing_AndRejectsArbitraryName()
        {
            var client = _factory.CreateHttpsClient();

            var assigned = await client.PostAsJsonAsync("/api/auth/register", new { email = "no-alias@example.com", password = TestUsers.Password });
            var arbitrary = await client.PostAsJsonAsync("/api/auth/register", new { username = "xX_edgelord_Xx", email = "edge@example.com", password = TestUsers.Password });

            Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
            Assert.True(WesternAliases.IsCurated((await assigned.Content.ReadFromJsonAsync<UserResponse>(SansPostFactory.Json))!.Username));
            Assert.Equal(HttpStatusCode.BadRequest, arbitrary.StatusCode);
        }

        // G19 — w nagłówku jedno wejście do wyszukiwania (pole); brak osobnego linku "Szukaj" w nawigacji desktopowej.
        [Fact]
        public async Task Header_HasSingleDesktopSearchEntry()
        {
            var html = await _factory.CreateHttpsClient().GetStringAsync("/saloon");
            var header = html[html.IndexOf("<header class=\"app-header\"", StringComparison.Ordinal)..html.IndexOf("</header>", StringComparison.Ordinal)];
            var primaryNav = Regex.Match(header, "<nav class=\"primary-nav\".*?</nav>", RegexOptions.Singleline).Value;

            Assert.Single(Regex.Matches(header, "type=\"search\""));
            Assert.DoesNotContain("href=\"/search\"", primaryNav);
        }
    }
}
