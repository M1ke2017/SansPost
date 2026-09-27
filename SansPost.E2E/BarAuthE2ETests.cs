using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 16B-FIX — logowanie i rejestracja rozpoczęte w BAR zostają w Saloonie: karta w oknie BAR (te same
    // formularze co karty na Entrance), po sukcesie powrót dokładnie do kontekstu (formularz rozmowy, ta sama rozmowa).
    // Bezpośrednie /login i /register pozostają klasycznymi stronami.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "BarAuth")]
    public class BarAuthE2ETests
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

        public BarAuthE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].bar-panel");
        private static ILocator Item(IPage page, string id) => page.Locator($"dialog[open] [data-bar-item='{id}']");
        private static ILocator AuthTitle(IPage page) => page.Locator("#bar-auth-title");
        private static ILocator LoginCta(IPage page) => Panel(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się, aby rozpocząć rozmowę" }).First;
        private static Task<bool> FocusNotInBackground(IPage page) =>
            page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }");

        private async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800)
        {
            var context = await _env.NewContextAsync(width: width, height: height);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
        }

        private static async Task OpenBarAsync(IPage page)
        {
            await page.Locator(".hall-zone[data-zone='bar']").ClickAsync();
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
        }

        // Karta logowania w BAR: wypełnienie i wysłanie (istniejące POST /auth/login), potem przeładowanie pod cel.
        private static async Task SignInInBarAsync(IPage page, string email)
        {
            await page.Locator("#bar-login-email").FillAsync(email);
            await page.Locator("#bar-login-password").FillAsync(E2EEnvironment.UserPassword);
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
        }

        // ---- 1, 2, 3, 8, 13. Karta logowania i rejestracji w BAR, przełączanie, Escape, klawiatura --------------------

        [Fact]
        public async Task GuestCards_OpenInBar_SwitchInPlace_EscapeReturnsToCta()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            var history = await page.EvaluateAsync<int>("() => history.length");

            await LoginCta(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=login&next=%2Fnew&back=%2Fsaloon%3Fbar%3Dmenu"));
            Assert.Equal("/saloon", await page.EvaluateAsync<string>("() => location.pathname"));   // bez klasycznej strony
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1);
            await Expect(AuthTitle(page)).ToHaveTextAsync("Powrót do Saloonu");
            await Expect(page.Locator("#bar-login-email")).ToBeFocusedAsync();
            await Expect(Panel(page).GetByLabel("Email")).ToBeVisibleAsync();
            await Expect(Panel(page).GetByLabel("Hasło", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true })).ToBeVisibleAsync();
            await Expect(Panel(page).Locator("input[name=ReturnUrl]")).ToHaveValueAsync("/new");

            for (var i = 0; i < 12; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await FocusNotInBackground(page), $"Tab #{i + 1} trafił do sali pod oknem.");
            }

            // Logowanie ↔ rejestracja w tym samym oknie i na tym samym poziomie (bez nowego wpisu historii).
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=register&next=%2Fnew&back=%2Fsaloon%3Fbar%3Dmenu"));
            await Expect(AuthTitle(page)).ToHaveTextAsync("Karta nowego przybysza");
            await Expect(Panel(page).Locator("output#alias-value")).Not.ToBeEmptyAsync();
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Losuj inny" })).ToBeVisibleAsync();
            await Expect(Panel(page).GetByLabel("Email")).ToBeVisibleAsync();
            await Expect(Panel(page).GetByLabel("Hasło", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(Panel(page).GetByLabel("Powtórz hasło")).ToBeVisibleAsync();
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).Last.ClickAsync();
            await Expect(AuthTitle(page)).ToHaveTextAsync("Powrót do Saloonu");
            Assert.Equal(history + 1, await page.EvaluateAsync<int>("() => history.length"));

            // Escape (pole puste) → poprzedni poziom BAR; fokus na przycisku, który otworzył kartę.
            await page.Locator("#bar-login-email").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(LoginCta(page)).ToBeFocusedAsync();

            // Wpisany email nie znika przez Escape — karta zostaje.
            await LoginCta(page).ClickAsync();
            await page.Locator("#bar-login-email").FillAsync("ktos@example.test");
            await page.Keyboard.PressAsync("Escape");
            await Expect(AuthTitle(page)).ToBeVisibleAsync();
            await Expect(page.Locator("#bar-login-email")).ToHaveValueAsync("ktos@example.test");
            await Panel(page).Locator(".bar-auth > .bar-back").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(LoginCta(page)).ToBeFocusedAsync();
        }

        // ---- 4, 7. Rozpocznij rozmowę → logowanie → formularz rozmowy z tematem --------------------------------------

        [Fact]
        public async Task StartConversation_LoginInBar_ReturnsToNewPostFormWithTopic()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "topic-travel").ClickAsync();

            await LoginCta(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=login&next=%2Fnew%3Fcategory%3Dtravel&back=%2Fsaloon%3Fbar%3Dtopic%26category%3Dtravel"));
            await Expect(Panel(page).GetByText("Po zalogowaniu od razu przejdziesz do formularza nowej rozmowy.")).ToBeVisibleAsync();

            await SignInInBarAsync(page, user.Email);
            await Expect(page).ToHaveURLAsync(Ui.Path("/new?category=travel"), new() { Timeout = 15_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator("#post-category")).ToHaveValueAsync("Travel");
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);

            // "Wstecz" nie wraca do formularza logowania (wpis karty zastąpiony) — tylko do tematu.
            await page.GoBackAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=travel"), new() { Timeout = 15_000 });
        }

        // ---- 5. Rejestracja w BAR → automatyczne logowanie → kontekst -----------------------------------------------

        [Fact]
        public async Task Register_InBar_SignsInAutomatically_ReturnsToContext()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "topic-ideas").ClickAsync();
            await Panel(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" }).First.ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=register&next=%2Fnew%3Fcategory%3Dideas&back=%2Fsaloon%3Fbar%3Dtopic%26category%3Dideas"));

            var alias = Panel(page).Locator("output#alias-value");
            await Expect(alias).Not.ToBeEmptyAsync();
            var chosen = (await alias.InnerTextAsync()).Trim();
            var email = $"bar-{Guid.NewGuid():N}"[..20] + "@example.test";
            await page.Locator("#bar-register-email").FillAsync(email);
            await page.Locator("#bar-register-password").FillAsync(E2EEnvironment.UserPassword);
            await page.Locator("#bar-register-confirm").FillAsync(E2EEnvironment.UserPassword);
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();

            await Expect(page).ToHaveURLAsync(Ui.Path("/new?category=ideas"), new() { Timeout = 15_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(chosen);   // zalogowany przydomkiem z generatora
            await Expect(page.Locator("#post-category")).ToHaveValueAsync("Ideas");
        }

        // ---- 6, 8, 13. Komentarz / reakcja w rozmowie → logowanie → ta sama rozmowa ----------------------------------

        [Fact]
        public async Task Conversation_LoginFromComposer_ReturnsToSameConversation_EscapeReturnsToLike()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa z logowaniem {Guid.NewGuid():N}"[..40];
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "General");
            var reader = await _env.Main.CreateUserAsync();

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page, $"/saloon?bar=post&id={postId}&from=newest&scene=3d");
            await Expect(page.Locator("#bar-conversation-title")).ToHaveTextAsync(title);
            var conversationUrl = $"/saloon?bar=post&id={postId}&from=newest";

            // Reakcja gościa → karta logowania; Escape → ta sama rozmowa, fokus na reakcji.
            await Panel(page).Locator(".bar-conversation .like-button").ClickAsync();
            await Expect(AuthTitle(page)).ToHaveTextAsync("Powrót do Saloonu");
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do rozmowy" })).ToBeVisibleAsync();
            await Expect(Panel(page).GetByText("Po zalogowaniu wrócisz do tej rozmowy — z reakcjami i komentarzami.")).ToBeVisibleAsync();
            await page.Locator("#bar-login-email").FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            // Dokładnie poprzedni wpis historii — tu link wejściowy z "&scene=3d".
            await Expect(page).ToHaveURLAsync(new Regex($@"/saloon\?bar=post&id={postId}&from=newest(&scene=3d)?$"));
            await Expect(Panel(page).Locator(".bar-conversation .like-button")).ToBeFocusedAsync();

            // Komentarz: "Zaloguj się" w dyskusji → logowanie → ta sama rozmowa, już z formularzem komentarza.
            await Panel(page).Locator(".bar-conversation .composer").GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).ClickAsync();
            await Expect(Panel(page).Locator("input[name=ReturnUrl]")).ToHaveValueAsync(conversationUrl);
            await SignInInBarAsync(page, reader.Email);
            await Expect(page).ToHaveURLAsync(Ui.Path(conversationUrl), new() { Timeout = 15_000 });
            await Expect(page.Locator("#bar-conversation-title")).ToHaveTextAsync(title, new() { Timeout = 20_000 });
            await Expect(Panel(page).Locator("#bar-new-comment")).ToBeVisibleAsync();
            await Panel(page).Locator("#bar-new-comment").FillAsync("Komentarz po zalogowaniu w barze.");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(Panel(page).Locator(".comment-body").Last).ToHaveTextAsync("Komentarz po zalogowaniu w barze.");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1);   // nadal Main Hall
            Assert.Equal(1, await page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length"));
        }

        // ---- 9, 10. Bezpośrednie /login i /register — klasyczne strony ---------------------------------------------

        [Fact]
        public async Task DirectLoginAndRegister_RemainClassicPages()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/login");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Zaloguj się", Level = 1 })).ToBeVisibleAsync();
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
            await Expect(page.Locator(".bar-auth")).ToHaveCountAsync(0);
            await Expect(page).ToHaveTitleAsync("Logowanie · SansPost");

            await Ui.GotoAsync(page, "/register");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Załóż konto", Level = 1 })).ToBeVisibleAsync();
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
            await Expect(page).ToHaveTitleAsync("Załóż konto · SansPost");
        }

        // ---- 11. Open redirect ----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("https%3A%2F%2Fevil.example%2Fsteal")]
        [InlineData("%2F%2Fevil.example")]
        [InlineData("%2F%5Cevil.example")]
        [InlineData("javascript%3Aalert(1)")]
        public async Task OpenRedirect_InBarNext_IsIgnored_LoginStaysOnSite(string next)
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page, $"/saloon?bar=login&next={next}&back=%2Fsaloon%3Fbar%3Dmenu&scene=3d");
            await Expect(AuthTitle(page)).ToHaveTextAsync("Powrót do Saloonu");
            await Expect(Panel(page).Locator("input[name=ReturnUrl]")).ToHaveValueAsync("/saloon?bar=menu");   // obcy adres odrzucony

            // Podmiana ukrytego pola w przeglądarce też nie wyprowadzi poza serwis — ReturnUrl weryfikuje serwer.
            await Panel(page).Locator("input[name=ReturnUrl]").EvaluateAsync("i => i.value = 'https://evil.example/steal'");
            await SignInInBarAsync(page, user.Email);
            await page.WaitForURLAsync(url => !url.Contains("bar=login"), new() { Timeout = 15_000, WaitUntil = WaitUntilState.Commit });
            Assert.StartsWith(_env.Main.BaseUrl.TrimEnd('/'), page.Url);
            Assert.DoesNotContain("evil", page.Url);
        }

        // ---- 12. Mobile 390 / 360 ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_AuthCards_FullWidth_CtaReachable(int width, int height)
        {
            await using var context = await NewContextAsync(width, height);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await LoginCta(page).ScrollIntoViewIfNeededAsync();
            await LoginCta(page).ClickAsync();
            await Expect(AuthTitle(page)).ToBeVisibleAsync();

            async Task AssertCardAsync(string name, string submit)
            {
                var panel = Panel(page);
                await panel.EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
                var box = (await panel.BoundingBoxAsync())!;
                Assert.True(Math.Abs(box.X) < 0.5 && Math.Abs(box.Width - width) < 0.5, $"{name}: karta nie na pełną szerokość.");
                Assert.False(await panel.EvaluateAsync<bool>("d => [...d.querySelectorAll('*')].some(e => e.getBoundingClientRect().right > innerWidth + 0.5)"), $"{name}: element poza ekranem.");
                await Ui.AssertNoHorizontalOverflowAsync(page, $"{name} {width}");
                foreach (var input in await panel.Locator(".bar-auth input:not([type=hidden])").AllAsync())
                {
                    await input.ScrollIntoViewIfNeededAsync();
                    Assert.True((await input.BoundingBoxAsync())!.Width > width * 0.6, $"{name}: pole za wąskie.");
                }
                var cta = panel.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true });
                Assert.True((await cta.BoundingBoxAsync())!.Height >= 44, $"{name}: CTA za niskie.");
                foreach (var action in await panel.Locator(".bar-auth .card-link, .bar-auth > .bar-back").AllAsync())
                    Assert.True((await action.BoundingBoxAsync())!.Height >= 44, $"{name}: akcja za niska na dotyk.");
            }

            await AssertCardAsync("logowanie", "Zaloguj się");

            // Klawiatura ekranowa (mniejszy widok): fokus w haśle, główne CTA nadal w zasięgu.
            await page.SetViewportSizeAsync(width, height / 2);
            await page.Locator("#bar-login-password").FocusAsync();
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true })).ToBeInViewportAsync();
            await page.SetViewportSizeAsync(width, height);

            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Załóż konto" }).ClickAsync();
            await Expect(AuthTitle(page)).ToHaveTextAsync("Karta nowego przybysza");
            await Expect(page.Locator("#bar-register-email")).ToBeVisibleAsync();
            await AssertCardAsync("rejestracja", "Dołącz do Saloonu");
            await page.SetViewportSizeAsync(width, height / 2);
            await page.Locator("#bar-register-confirm").FocusAsync();
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" })).ToBeInViewportAsync();
        }
    }
}
