using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Limity publicznego demo na osobnych instancjach (własna baza, ciasna konfiguracja) — bez czekania realnych minut.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class LimitsE2ETests
    {
        private readonly E2EEnvironment _env;

        public LimitsE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        [Fact]
        public async Task RegistrationCapacity_Reached_ShowsProductStateInsteadOfForm()
        {
            var server = await _env.StartServerAsync("capacity", new Dictionary<string, string?>
            {
                ["PublicDemo__SeedContent"] = "false",
                ["PublicDemo__MaxPublicAccounts"] = "2"
            });
            await server.RegisterUserAsync();
            await server.RegisterUserAsync();

            // REST: 409 z kodem (dla klientów API), UI: produktowy stan bez działającego formularza.
            using var api = server.CreateApiClient();
            var third = await api.PostAsJsonAsync("/api/auth/register", new { email = "third@example.test", password = E2EEnvironment.UserPassword });
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

            await using var context = await _env.NewContextAsync(server);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/register");
            await Expect(page.GetByText("Limit kont został osiągnięty")).ToBeVisibleAsync();
            await Expect(page.Locator("#email")).ToHaveCountAsync(0);
            await Expect(page.Locator("main form button[type=submit]")).ToHaveCountAsync(0);
            Assert.DoesNotMatch(new Regex("409|registration-capacity-reached"), await page.Locator("main").InnerTextAsync());
        }

        [Fact]
        public async Task LoginRateLimit_IsTranslatedToProductMessage()
        {
            var server = await _env.StartServerAsync("ratelimit-auth", new Dictionary<string, string?>
            {
                ["PublicDemo__SeedContent"] = "false",
                ["RateLimiting__Auth__PermitLimit"] = "3"
            });

            await using var context = await _env.NewContextAsync(server);
            var page = await context.NewPageAsync();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Ui.GotoAsync(page, "/login");
                await page.GetByLabel("Email").FillAsync("nobody@example.test");
                await page.Locator("#password").FillAsync("wrong-password-1");
                await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
                await Expect(page.GetByText("Nieprawidłowy email lub hasło.")).ToBeVisibleAsync();
            }

            await Ui.GotoAsync(page, "/login");
            await page.GetByLabel("Email").FillAsync("nobody@example.test");
            await page.Locator("#password").FillAsync("wrong-password-1");
            await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();

            await Expect(page.GetByText("Zbyt wiele prób logowania. Odczekaj chwilę i spróbuj ponownie.")).ToBeVisibleAsync();
            Assert.DoesNotMatch(new Regex("429|Too Many"), await page.Locator("body").InnerTextAsync());
        }

        [Fact]
        public async Task WriteRateLimit_IsTranslatedToProductMessage_WithRetryHint()
        {
            var server = await _env.StartServerAsync("ratelimit-writes", new Dictionary<string, string?>
            {
                ["PublicDemo__SeedContent"] = "false",
                ["RateLimiting__Writes__PermitLimit"] = "3"
            });
            var author = await server.CreateUserAsync();
            var postId = await Api.CreatePostAsync(server, author, "Post do komentowania");   // 1. zapis autora (inna partycja niż komentujący)

            await using var session = await Session.UserAsync(_env, server);
            var page = session.Page;
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            for (var i = 1; i <= 3; i++)
            {
                await page.Locator("#new-comment").FillAsync($"Komentarz numer {i}");
                await page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
                await Expect(page.Locator(".comment", new() { HasText = $"Komentarz numer {i}" })).ToBeVisibleAsync();
            }

            await page.Locator("#new-comment").FillAsync("Komentarz ponad limit");
            await page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();

            await Expect(page.GetByText(new Regex("Zbyt wiele operacji w krótkim czasie. Spróbuj ponownie za \\d+ s."))).ToBeVisibleAsync();
            await Expect(page.Locator(".comment", new() { HasText = "Komentarz ponad limit" })).ToHaveCountAsync(0);
            Assert.DoesNotMatch(new Regex("429|Too Many|RateLimited"), await page.Locator("main").InnerTextAsync());
        }

        // Regresja (Sprint 9 P2, naprawione w Sprincie 10): wyszukiwarka Blazor i REST (/api/search) korzystają z jednego
        // limitera (anonim: klucz = adres klienta). Wyszukiwania w UI zużywają limit, a po jego wyczerpaniu UI pokazuje
        // produktowy komunikat, a REST — 429 z Retry-After.
        [Fact]
        public async Task SearchLimit_IsSharedByBlazorSearchAndRest()
        {
            var server = await _env.StartServerAsync("ratelimit-search", new Dictionary<string, string?>
            {
                ["PublicDemo__SeedContent"] = "true",
                ["RateLimiting__Search__PermitLimit"] = "3"
            });

            await using var context = await _env.NewContextAsync(server);
            var page = await context.NewPageAsync();
            foreach (var query in new[] { "gry", "planszówka" })
            {
                await Ui.GotoAsync(page, $"/search?q={Uri.EscapeDataString(query)}");
                await Expect(page.Locator("#search-hint")).Not.ToContainTextAsync("Szukanie");
                await Expect(page.Locator(".post-list .post-card").First).ToBeVisibleAsync();
            }

            // Trzecie użycie limitu przez REST (ten sam klient, ten sam limiter) — jeszcze w limicie.
            using var api = server.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/search/posts?q=gry")).StatusCode);

            // Limit wyczerpany: UI → komunikat produktowy (bez kodu HTTP), REST → 429 + Retry-After.
            await Ui.GotoAsync(page, "/search?q=notatki");
            await Expect(page.GetByText(new Regex(@"Zbyt wiele wyszukiwań\. Spróbuj ponownie za \d+ s\."))).ToBeVisibleAsync();
            Assert.DoesNotMatch(new Regex("429|Too Many|RateLimited"), await page.Locator("main").InnerTextAsync());

            var limited = await api.GetAsync("/api/search/posts?q=gry");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
    }
}
