using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using SansPost.Features.Identity;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 14D — karty Saloonu na Entrance: "Karta nowego przybysza" (rejestracja z przydomkiem z generatora)
    // i "Karta powrotu do Saloonu" (logowanie). Zwykły HTML w natywnym <dialog>, ten sam backend (/auth/register,
    // /auth/login). Sukces → animacja wejścia → /saloon jako zalogowany. Headless = renderer CSS (to samo HTML co przy 3D).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Critical")]
    public class EntranceCardsE2ETests
    {
        private readonly E2EEnvironment _env;

        public EntranceCardsE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Signs(IPage page) => page.Locator("nav.entrance-signs");
        private static ILocator Card(IPage page) => page.Locator("dialog[open]");
        private static ILocator Alias(IPage page) => Card(page).Locator("output#alias-value");

        private static async Task OpenRegisterAsync(IPage page)
        {
            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta nowego przybysza" })).ToBeVisibleAsync();
            await Expect(Alias(page)).Not.ToBeEmptyAsync();
        }

        private static async Task OpenLoginAsync(IPage page)
        {
            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta powrotu do Saloonu" })).ToBeVisibleAsync();
        }

        private static async Task FillRegisterAsync(IPage page, string email, string password, string? confirm = null)
        {
            await Card(page).Locator("#card-email").FillAsync(email);
            await Card(page).Locator("#card-password").FillAsync(password);
            await Card(page).Locator("#card-confirm").FillAsync(confirm ?? password);
        }

        private static string NewEmail() => $"card-{Guid.NewGuid():N}"[..17] + "@example.test";

        // Rejestracja: przydomek z generatora (widoczny, bez pola na własną nazwę), "Losuj inny" myszą i klawiaturą,
        // po "Dołącz do Saloonu" — drzwi, potem /saloon jako zalogowany z wybranym przydomkiem.
        [Fact]
        public async Task RegisterCard_GeneratedAlias_Reroll_CreatesAccount_AndEntersSaloonSignedIn()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await OpenRegisterAsync(page);

            await Expect(Card(page).Locator("input[name=Username]:not([type=hidden])")).ToHaveCountAsync(0);
            await Expect(Card(page).GetByText("SansPost Saloon")).ToBeVisibleAsync();
            var first = await Alias(page).InnerTextAsync();
            Assert.True(WesternAliases.IsCurated(first), first);

            var reroll = Card(page).GetByRole(AriaRole.Button, new() { Name = "Losuj inny" });
            await reroll.ClickAsync();
            await Expect(Alias(page)).Not.ToHaveTextAsync(first);
            var second = await Alias(page).InnerTextAsync();
            await reroll.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Alias(page)).Not.ToHaveTextAsync(second);
            var chosen = await Alias(page).InnerTextAsync();
            Assert.True(WesternAliases.IsCurated(chosen), chosen);
            Assert.Equal(chosen, await Card(page).Locator("input[type=hidden][name=Username]").InputValueAsync());

            await FillRegisterAsync(page, NewEmail(), E2EEnvironment.UserPassword);
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();

            await Expect(page.Locator(".entrance.is-entering")).ToHaveCountAsync(1);
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(chosen);
        }

        // Błąd zostaje w karcie (strona się nie zmienia): hasła różne, potem zajęty email.
        [Fact]
        public async Task RegisterCard_Errors_StayInCard_OnEntrance()
        {
            var existing = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await OpenRegisterAsync(page);

            await FillRegisterAsync(page, NewEmail(), E2EEnvironment.UserPassword, E2EEnvironment.UserPassword + "x");
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Hasła nie są identyczne");
            await Expect(Card(page).Locator("#card-confirm")).ToHaveAttributeAsync("aria-invalid", "true");
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));

            await FillRegisterAsync(page, existing.Email, E2EEnvironment.UserPassword);
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Nie można utworzyć konta z tym adresem email");
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));
            await Expect(page.Locator(".entrance.is-entering")).ToHaveCountAsync(0);
        }

        // Logowanie: zły login → komunikat w karcie; poprawny → drzwi → /saloon jako zalogowany.
        [Fact]
        public async Task LoginCard_InvalidThenValid_EntersSaloonSignedIn()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await OpenLoginAsync(page);

            await Card(page).GetByLabel("Email").FillAsync(user.Email);
            await Card(page).Locator("#card-login-password").FillAsync("zle-haslo-123");
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Nieprawidłowy email lub hasło");
            await Expect(Card(page).GetByLabel("Email")).ToHaveAttributeAsync("aria-invalid", "true");
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));

            await Card(page).Locator("#card-login-password").FillAsync(E2EEnvironment.UserPassword);
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "1500");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);
        }

        // Podwójne zatwierdzenie karty: jedno logowanie, jedna nawigacja.
        [Fact]
        public async Task LoginCard_DoubleSubmit_NavigatesOnce()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await OpenLoginAsync(page);
            await Card(page).GetByLabel("Email").FillAsync(user.Email);
            await Card(page).Locator("#card-login-password").FillAsync(E2EEnvironment.UserPassword);

            var navigations = 0;
            page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };
            var submit = Card(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" });
            await submit.ClickAsync();
            await page.Keyboard.PressAsync("Enter");

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(1000);
            Assert.Equal(1, navigations);
        }

        // Klawiatura: Enter na tabliczce otwiera kartę (fokus w pierwszym polu), Escape zamyka i wraca na tabliczkę;
        // przejście między kartami; reduced motion — po zalogowaniu od razu /saloon.
        [Fact]
        public async Task Cards_KeyboardFlow_EscapeReturnsFocus_Switching_AndReducedMotionLogin()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync(reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            var loginSign = Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" });
            await loginSign.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Card(page).GetByLabel("Email")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(Card(page)).ToHaveCountAsync(0);
            await Expect(loginSign).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Enter");
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta nowego przybysza" })).ToBeVisibleAsync();
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta powrotu do Saloonu" })).ToBeVisibleAsync();

            await page.Keyboard.TypeAsync(user.Email);
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.TypeAsync(E2EEnvironment.UserPassword);
            await page.Keyboard.PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 3000 });
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);
        }

        // Mobile: karty mieszczą się w viewport (przewijanie wewnątrz karty), bez poziomego przewijania strony.
        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 640)]
        public async Task Cards_FitMobileViewport(int width, int height)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            await OpenRegisterAsync(page);
            await Card(page).EvaluateAsync("d => Promise.all(d.getAnimations({ subtree: true }).map(a => a.finished))");
            var box = await Card(page).EvaluateAsync<double[]>("d => { const b = d.getBoundingClientRect(); return [b.left, b.top, b.right, b.bottom]; }");
            Assert.True(box[0] >= 0 && box[1] >= 0 && box[2] <= width + 0.5 && box[3] <= height + 0.5, $"Karta poza viewport: {string.Join(", ", box)}");
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ScrollIntoViewIfNeededAsync();
            await Expect(Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" })).ToBeInViewportAsync();
            await Ui.AssertNoHorizontalOverflowAsync(page, $"karta rejestracji {width}");
        }
    }
}
