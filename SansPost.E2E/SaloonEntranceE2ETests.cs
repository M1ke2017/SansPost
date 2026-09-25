using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 13 — "/" to świadome wejście, "/saloon" to główny hub. Gość czyta bez konta; logo prowadzi do Saloonu;
    // logowanie (także zaczęte na wejściu) kończy się w Saloonie; wylogowanie wraca do wejścia.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Critical")]
    public class SaloonEntranceE2ETests
    {
        private readonly E2EEnvironment _env;

        public SaloonEntranceE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        [Fact]
        public async Task Guest_EntersSaloonFromEntrance_ReadsFeed_LogoAndRefreshStayInSaloon()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator("h1#entrance-title")).ToHaveTextAsync("SansPost");
            await Expect(page.Locator(".post-card")).ToHaveCountAsync(0);

            await page.GetByRole(AriaRole.Link, new() { Name = "Wejdź do Saloonu" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();

            // Odświeżenie huba działa (bezpośredni adres, prerender + circuit).
            await page.ReloadAsync();
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();

            // Logo z dowolnego miejsca aplikacji → Saloon, nie wejście.
            await Ui.GotoAsync(page, "/categories");
            await page.Locator("a.brand").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
        }

        [Fact]
        public async Task LoginStartedAtEntrance_LandsInSaloon_LogoutReturnsToEntrance()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            await Ui.GotoAsync(page, "/");
            await page.Locator(".app-header").GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
            await Ui.WaitInteractiveAsync(page);
            await page.GetByLabel("Email").FillAsync(user.Email);
            await page.Locator("#password").FillAsync(E2EEnvironment.UserPassword);
            await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);

            await page.Locator(".menu-trigger").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Wyloguj się" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));
            await Expect(page.Locator("h1#entrance-title")).ToBeVisibleAsync();
            await Expect(page.Locator(".menu-trigger")).ToHaveCountAsync(0);
        }
    }
}
