using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class RoutingAndSecurityE2ETests
    {
        private readonly E2EEnvironment _env;

        public RoutingAndSecurityE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        // Każda trasa: bezpośredni URL (HTTP 200 + nagłówek), odświeżenie, nawigacja po stronie klienta (Blazor.navigateTo).
        [Fact]
        public async Task Routes_WorkByDirectUrl_Refresh_AndClientNavigation()
        {
            await using var user = await Session.UserAsync(_env);
            var postId = await Api.CreatePostAsync(_env.Main, user.User!, "Post do tras");
            var routes = new (string Path, string Heading)[]
            {
                ("/", "SansPost"),
                ("/saloon", "Odkrywaj"),
                ("/posts", "Odkrywaj"),
                ("/categories", "Kategorie"),
                ("/c/travel", "Podróże"),
                ("/search", "Szukaj"),
                ($"/post-view/{postId}", "Post do tras"),
                ("/new", "Nowy post"),
                ($"/edit-post/{postId}", "Edytuj post"),
                ($"/u/{user.User!.Alias}", user.User.Alias),
                ("/me", "Twoje konto")
            };

            await AssertRoutesAsync(user.Page, routes);

            await using var admin = await Session.AdminAsync(_env);
            await AssertRoutesAsync(admin.Page, new[] { ("/admin/moderation", "Moderacja") });

            await using var guestContext = await _env.NewContextAsync();
            var guest = await guestContext.NewPageAsync();
            await AssertRoutesAsync(guest, new[] { ("/login", "Zaloguj się"), ("/register", "Załóż konto") });
        }

        private static async Task AssertRoutesAsync(IPage page, IEnumerable<(string Path, string Heading)> routes)
        {
            foreach (var (path, heading) in routes)
            {
                var response = await page.GotoAsync(path);
                Assert.Equal(200, response!.Status);
                await Ui.WaitInteractiveAsync(page);
                await Expect(page.Locator("h1").First).ToHaveTextAsync(heading);

                await page.ReloadAsync();
                await Ui.WaitInteractiveAsync(page);
                await Expect(page.Locator("h1").First).ToHaveTextAsync(heading);

                // Nawigacja wewnątrz aplikacji: najpierw inna strona, potem powrót przez router Blazora.
                await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
                await Expect(page.Locator("h1").First).ToHaveTextAsync("Kategorie");
                await page.EvaluateAsync("path => Blazor.navigateTo(path)", path);
                await Expect(page.Locator("h1").First).ToHaveTextAsync(heading);
                Assert.Equal(0, await page.Locator("#blazor-error-ui:visible").CountAsync());
            }
        }

        // Regresja (Sprint 9 P2, naprawione w Sprincie 10): nieistniejący adres = HTTP 404 + produktowa strona "nie znaleziono"
        // (bezpośrednie żądanie, nawigacja Playwright, odświeżenie). Nawigacja po stronie klienta nadal pokazuje tę samą stronę.
        [Fact]
        public async Task UnknownRoute_Returns404_WithNotFoundPage()
        {
            using (var http = _env.Main.CreateApiClient())
            {
                var direct = await http.GetAsync("/to-nie-istnieje");
                Assert.Equal(System.Net.HttpStatusCode.NotFound, direct.StatusCode);
                Assert.Contains("Nie ma tu nic do czytania", await direct.Content.ReadAsStringAsync());
            }

            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            var response = await page.GotoAsync("/to-nie-istnieje");
            Assert.Equal(404, response!.Status);
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator("h1")).ToHaveTextAsync("Nie ma tu nic do czytania");

            var reload = await page.ReloadAsync();
            Assert.Equal(404, reload!.Status);
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator("h1")).ToHaveTextAsync("Nie ma tu nic do czytania");

            // Router klienta: znana strona → nieznany adres → znowu znana (bez przeładowania i bez błędu Blazora).
            await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
            await Expect(page.Locator("h1").First).ToHaveTextAsync("Kategorie");
            await page.EvaluateAsync("() => Blazor.navigateTo('/jeszcze-inny-brak')");
            await Expect(page.Locator("h1").First).ToHaveTextAsync("Nie ma tu nic do czytania");
            await page.EvaluateAsync("() => Blazor.navigateTo('/search')");
            await Expect(page.Locator("h1").First).ToHaveTextAsync("Szukaj");
            Assert.Equal(0, await page.Locator("#blazor-error-ui:visible").CountAsync());
        }

        [Fact]
        public async Task Security_UiNeverShowsSecrets_CookieDoesNotAuthorizeRest_HiddenContentDoesNotLeak()
        {
            await using var user = await Session.UserAsync(_env);
            var page = user.Page;

            // Cookie UI nie autoryzuje REST (REST wymaga JWT).
            var status = await page.EvaluateAsync<int>("async () => (await fetch('/api/notifications')).status");
            Assert.Equal(401, status);

            // Brak emaili, hashy haseł i danych uwierzytelnienia w widokach.
            foreach (var path in new[] { "/me", $"/u/{user.User!.Alias}", "/saloon", "/categories" })
            {
                await Ui.GotoAsync(page, path);
                var html = await page.ContentAsync();
                Assert.DoesNotContain(user.User.Email, html);
                Assert.DoesNotMatch(new Regex(@"\$2[aby]\$\d\d\$"), html);   // BCrypt
                Assert.DoesNotMatch(new Regex("passwordhash|authversion", RegexOptions.IgnoreCase), html);
            }

            // Ukryty post: ani tytuł, ani treść nie trafiają do HTML (także w feedzie i wyszukiwarce).
            var secret = $"Sekretny tytul {Guid.NewGuid():N}"[..28];
            var postId = await Api.CreatePostAsync(_env.Main, user.User, secret, content: "Sekretna treść " + secret);
            await Api.AdminAsync(_env.Main, $"/api/moderation/posts/{postId}/hide");
            // Wyszukiwanie po słowie z TREŚCI (nie z tytułu) — strona wyników sama wyświetla zapytanie, więc echo frazy to nie wyciek.
            foreach (var path in new[] { $"/post-view/{postId}", "/saloon", "/search?q=Sekretna", $"/u/{user.User.Alias}" })
            {
                await Ui.GotoAsync(page, path);
                var html = await page.ContentAsync();
                Assert.False(html.Contains(secret, StringComparison.Ordinal), $"Ukryta treść widoczna na {path}");
            }
        }
    }
}
