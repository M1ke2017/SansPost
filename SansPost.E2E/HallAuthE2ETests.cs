using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 16 final — logowanie z Main Hall 3D (narożnik "Zaloguj się"): karta Saloonu nad salą (SaloonLoginCard /
    // SaloonRegisterCard ze wspólnymi formularzami z 16B-FIX), bez klasycznej strony i bez otwierania BAR. Po sukcesie —
    // Main Hall, już z kontem. Bezpośrednie /login i /register — klasyczne (BarAuthE2ETests).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "HallAuth")]
    public class HallAuthE2ETests
    {
        private const string CapableGpu = @"(() => {
            const getContext = HTMLCanvasElement.prototype.getContext;
            HTMLCanvasElement.prototype.getContext = function (type, attributes) {
                if (/webgl/i.test(type) && attributes) { attributes = { ...attributes }; delete attributes.failIfMajorPerformanceCaveat; }
                return getContext.call(this, type, attributes);
            };
            const getParameter = WebGL2RenderingContext.prototype.getParameter;
            WebGL2RenderingContext.prototype.getParameter = function (name) {
                return name === 0x9246 ? 'ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)' : getParameter.call(this, name);
            };
            try { sessionStorage.setItem('sp-scene-probe', 'ok'); } catch { }
        })()";

        private readonly E2EEnvironment _env;

        public HallAuthE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Card(IPage page) => page.Locator("dialog[open].dialog-card");
        private static ILocator CornerLogin(IPage page) => page.Locator("#hall-corner-login");
        private static Task<bool> FocusNotInBackground(IPage page) =>
            page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }");

        private async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800)
        {
            var context = await _env.NewContextAsync(width: width, height: height);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task OpenHallAsync(IPage page)
        {
            await Ui.GotoAsync(page, "/saloon?scene=3d");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
        }

        // Po zalogowaniu: /saloon od nowa — Main Hall z kontem, bez otwartego BAR i bez przejazdu kamery do strefy.
        private static async Task ExpectSignedInHallAsync(IPage page, string alias)
        {
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 15_000 });
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "SansPost" }).GetByRole(AriaRole.Link, new() { Name = $"Moje konto ({alias})" })).ToBeVisibleAsync();
            await Expect(CornerLogin(page)).ToHaveCountAsync(0);
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            Assert.Null(await page.EvaluateAsync<string?>("() => window.__sansPostHall.area"));
        }

        [Fact]
        public async Task CornerLogin_OpensSaloonCardOverHall_KeyboardEscapeReturnsFocus()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var url = page.Url;

            await CornerLogin(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta powrotu do Saloonu" })).ToBeVisibleAsync();
            await Expect(Card(page).GetByLabel("Email")).ToBeFocusedAsync();
            Assert.Equal(url, page.Url);                                            // bez klasycznej strony /login
            await Expect(page.Locator("dialog[open].bar-panel")).ToHaveCountAsync(0);   // i bez otwierania BAR
            Assert.Null(await page.EvaluateAsync<string?>("() => window.__sansPostHall.area"));
            await Expect(Card(page).Locator("input[name=ReturnUrl]")).ToHaveValueAsync("/saloon");

            for (var i = 0; i < 10; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await FocusNotInBackground(page), $"Tab #{i + 1} trafił do sali pod kartą.");
            }

            await Card(page).GetByLabel("Email").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(CornerLogin(page)).ToBeFocusedAsync();
            Assert.Equal(url, page.Url);
        }

        [Fact]
        public async Task CornerRegister_SwitchInsideSaloon_EscapeReturnsFocus()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await CornerLogin(page).ClickAsync();
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta nowego przybysza" })).ToBeVisibleAsync();
            await Expect(Card(page).Locator("output#alias-value")).Not.ToBeEmptyAsync();
            await Expect(Card(page).GetByRole(AriaRole.Button, new() { Name = "Losuj inny" })).ToBeVisibleAsync();
            await Expect(Card(page).GetByLabel("Powtórz hasło")).ToBeVisibleAsync();
            Assert.Contains("/saloon", page.Url);

            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta powrotu do Saloonu" })).ToBeVisibleAsync();
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(Card(page).GetByRole(AriaRole.Heading, new() { Name = "Karta nowego przybysza" })).ToBeVisibleAsync();

            await Card(page).GetByLabel("Email").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(CornerLogin(page)).ToBeFocusedAsync();
        }

        [Fact]
        public async Task CornerLogin_Success_ReturnsToMainHallSignedIn()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await CornerLogin(page).ClickAsync();
            await Card(page).GetByLabel("Email").FillAsync(user.Email);
            await Card(page).Locator("#card-login-password").FillAsync(E2EEnvironment.UserPassword);
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).ClickAsync();

            await ExpectSignedInHallAsync(page, user.Alias);
        }

        [Fact]
        public async Task CornerRegister_Success_SignsIn_ReturnsToMainHall()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await CornerLogin(page).ClickAsync();
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            var alias = Card(page).Locator("output#alias-value");
            await Expect(alias).Not.ToBeEmptyAsync();
            var chosen = (await alias.InnerTextAsync()).Trim();
            await Card(page).Locator("#card-email").FillAsync($"hall-{Guid.NewGuid():N}"[..20] + "@example.test");
            await Card(page).Locator("#card-password").FillAsync(E2EEnvironment.UserPassword);
            await Card(page).Locator("#card-confirm").FillAsync(E2EEnvironment.UserPassword);
            await Card(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();

            await ExpectSignedInHallAsync(page, chosen);
        }

        // Narożnik jest częścią pełnoekranowej sali od 768 px — tam karta mieści się w widoku. Na telefonie (390 / 360)
        // sala ma zwykły nagłówek i dolną nawigację (Sprint 15C-FIX2) — ten układ się nie zmienia.
        [Theory]
        [InlineData(768, 1024)]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Responsive_CornerCardFits_PhoneLayoutUnchanged(int width, int height)
        {
            await using var context = await NewContextAsync(width, height);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            if (width < 768)
            {
                await Expect(page.Locator(".hall-corner")).ToBeHiddenAsync();
                await Expect(page.Locator(".app-header")).ToBeVisibleAsync();
                await Expect(page.Locator(".bottom-nav").GetByRole(AriaRole.Link, new() { Name = "Zaloguj" })).ToHaveAttributeAsync("href", "/login");
                return;
            }

            await CornerLogin(page).ClickAsync();
            await Expect(Card(page).GetByLabel("Email")).ToBeFocusedAsync();
            await Card(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
            var box = (await Card(page).BoundingBoxAsync())!;
            Assert.True(box.X >= 0 && box.X + box.Width <= width + 0.5 && box.Y >= 0 && box.Y + box.Height <= height + 0.5, "Karta w widoku.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"karta logowania {width}");
            Assert.True((await Card(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).BoundingBoxAsync())!.Height >= 44, "CTA za niskie.");
        }
    }
}
