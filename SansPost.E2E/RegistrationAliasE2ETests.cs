using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using SansPost.Features.Identity;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Rejestracja z przydomkiem z zatwierdzonego słownika — w prawdziwej przeglądarce.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class RegistrationAliasE2ETests
    {
        private readonly E2EEnvironment _env;

        public RegistrationAliasE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Alias(IPage page) => page.Locator("output#alias-value");
        private static ILocator Reroll(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Losuj inny" });

        private static async Task FillAndSubmitAsync(IPage page, string email)
        {
            await page.Locator("#email").FillAsync(email);
            await page.Locator("#password").FillAsync(E2EEnvironment.UserPassword);
            await page.Locator("#confirmPassword").FillAsync(E2EEnvironment.UserPassword);
            await page.Locator("main form button[type=submit]").ClickAsync();
        }

        [Fact]
        public async Task Register_WithGeneratedAlias_RerollByMouseAndKeyboard_CreatesAccountWithThatAlias()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/register");

            // Brak edytowalnej nazwy; przydomek widoczny, ogłaszany (aria-live), zgodny z polem ukrytym.
            await Expect(page.Locator("input[name=Username]:not([type=hidden])")).ToHaveCountAsync(0);
            await Expect(Alias(page)).ToHaveAttributeAsync("aria-live", "polite");
            var first = await Alias(page).InnerTextAsync();
            Assert.True(WesternAliases.IsCurated(first), first);
            Assert.Equal(first, await page.Locator("input[type=hidden][name=Username]").InputValueAsync());

            // Losuj inny — myszą…
            await Reroll(page).ClickAsync();
            await Expect(Alias(page)).Not.ToHaveTextAsync(first);
            var second = await Alias(page).InnerTextAsync();

            // …i z klawiatury (Enter, potem Spacja).
            await Reroll(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Alias(page)).Not.ToHaveTextAsync(second);
            var third = await Alias(page).InnerTextAsync();
            await Reroll(page).FocusAsync();   // patrz KnownIssue_RerollByKeyboard_LosesFocus
            await page.Keyboard.PressAsync("Space");
            await Expect(Alias(page)).Not.ToHaveTextAsync(third);
            var chosen = await Alias(page).InnerTextAsync();
            Assert.True(WesternAliases.IsCurated(chosen));
            Assert.Equal(chosen, await page.Locator("input[type=hidden][name=Username]").InputValueAsync());

            var email = $"reg-{Guid.NewGuid():N}"[..16] + "@example.test";
            await FillAndSubmitAsync(page, email);
            await Expect(page.GetByText("Konto utworzone")).ToBeVisibleAsync();

            await Ui.LoginAsync(page, email, E2EEnvironment.UserPassword);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(chosen);
        }

        // KNOWN ISSUE (P2, a11y): "Losuj inny" jest disabled na czas żądania — przycisk traci fokus na <body>,
        // więc kolejne Enter/Spacja nie losują ponownie. Test diagnostyczny: opisuje OBECNE zachowanie; po naprawie
        // (aria-busy zamiast disabled) zacznie padać i należy go odwrócić.
        [Fact]
        [Trait("KnownIssue", "P2")]
        public async Task KnownIssue_RerollByKeyboard_LosesFocus()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/register");
            var before = await Alias(page).InnerTextAsync();

            await Reroll(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Alias(page)).Not.ToHaveTextAsync(before);

            await Expect(Reroll(page)).Not.ToBeFocusedAsync();
            Assert.Equal("BODY", await page.EvaluateAsync<string>("() => document.activeElement.tagName"));
        }

        // Kolizja: przydomek wyświetlony w przeglądarce zajmuje ktoś inny → bez 500, komunikat, nowy przydomek, ponowienie działa.
        [Fact]
        public async Task AliasTakenBeforeSubmit_ShowsMessageAndFreshAlias_RetrySucceeds()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/register");
            var shown = await Alias(page).InnerTextAsync();

            await _env.Main.RegisterUserAsync(shown);   // "druga operacja" zajmuje przydomek

            var email = $"col-{Guid.NewGuid():N}"[..16] + "@example.test";
            var response = await page.RunAndWaitForResponseAsync(() => FillAndSubmitAsync(page, email), r => r.Url.Contains("/auth/register"));
            Assert.True(response.Status < 500);
            await Ui.WaitInteractiveAsync(page);

            await Expect(page.GetByText("Ten przydomek został właśnie zajęty. Wylosowaliśmy dla Ciebie nowy.")).ToBeVisibleAsync();
            var fresh = await Alias(page).InnerTextAsync();
            Assert.NotEqual(shown, fresh);

            await FillAndSubmitAsync(page, email);
            await Expect(page.GetByText("Konto utworzone")).ToBeVisibleAsync();
        }

        // Bezpieczeństwo: podmiana ukrytego pola na dowolną nazwę nie tworzy konta.
        [Fact]
        public async Task TamperedHiddenAlias_IsRejected()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/register");
            await page.EvalOnSelectorAsync("input[type=hidden][name=Username]", "el => el.value = 'Administrator'");

            await FillAndSubmitAsync(page, $"tmp-{Guid.NewGuid():N}"[..16] + "@example.test");

            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/register[?]error=invalid"));
            await Expect(page.GetByText("Sprawdź dane")).ToBeVisibleAsync();
        }
    }
}
