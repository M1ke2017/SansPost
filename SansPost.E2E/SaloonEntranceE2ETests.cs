using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 13–14 — "/" to wejście do Saloonu (własny layout, drzwi), "/saloon" to hub. Gość czyta bez konta;
    // logowanie i rejestracja kończą się w Saloonie; zalogowany wchodzi jednym przyciskiem; reduced motion = od razu.
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

        private static ILocator Signs(IPage page) => page.Locator("nav.entrance-signs");
        private static ILocator Enter(IPage page, string name = "Wejdź jako gość") => Signs(page).GetByRole(AriaRole.Button, new() { Name = name });

        [Fact]
        public async Task Entrance_HasOwnLayout_WithoutAppChrome_AndThreeGuestActions()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            await Expect(page.Locator("h1#entrance-title")).ToHaveTextAsync("SansPost");
            foreach (var chrome in new[] { ".app-header", ".primary-nav", "#header-search", ".bottom-nav", ".app-footer" })
                await Expect(page.Locator(chrome)).ToHaveCountAsync(0);

            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToHaveAttributeAsync("href", "/login?returnUrl=%2Fsaloon");
            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" })).ToHaveAttributeAsync("href", "/register");
            await Expect(Enter(page)).ToBeVisibleAsync();
            await Expect(page.Locator(".saloon svg[aria-hidden='true'], .entrance-town[aria-hidden='true']")).Not.ToHaveCountAsync(0);
        }

        // Gość: drzwi się otwierają (klasa + czas w atrybucie), potem routing Blazora bez przeładowania → feed.
        [Fact]
        public async Task Guest_EntersAsGuest_DoorsOpen_ThenSaloonWithoutReload_SecondEntryIsShorter()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await page.EvaluateAsync("() => window.__sameDocument = true");

            await Enter(page).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "1500");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sameDocument === true"), "Wejście nie może przeładowywać strony.");

            // Kolejne wejście w tej samej sesji: krótsza animacja (600 ms zamiast 1500 ms); Entrance nie jest pomijany.
            await page.EvaluateAsync("() => Blazor.navigateTo('/')");
            await Expect(page.Locator("h1#entrance-title")).ToBeVisibleAsync();
            await Enter(page).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "600");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
        }

        // Podwójne kliknięcie w trakcie wejścia nie uruchamia drugiego przejścia.
        [Fact]
        public async Task DoubleClick_DuringEntry_NavigatesOnce()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            var navigations = 0;
            page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };

            await Enter(page).ClickAsync();
            await Enter(page).ClickAsync(new() { Force = true });
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await page.WaitForTimeoutAsync(900);

            Assert.Equal(1, navigations);
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
        }

        // prefers-reduced-motion: brak animacji i praktycznie natychmiastowe przejście.
        [Fact]
        public async Task ReducedMotion_EntersImmediately_WithoutDoorAnimation()
        {
            await using var context = await _env.NewContextAsync(reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            var clock = Stopwatch.StartNew();
            await Enter(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 500 });
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 500, $"Przejście trwało {clock.ElapsedMilliseconds} ms.");
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
        }

        // Reduced motion wyłącza cały ruch sceny: ambient (chmury, kurz, szyld, gwiazdy, lampy), parallax i drzwi.
        [Theory]
        [InlineData("light")]
        [InlineData("dark")]
        public async Task ReducedMotion_DisablesAmbientMotion_AndPointerParallax(string theme)
        {
            await using var context = await _env.NewContextAsync(colorScheme: theme == "dark" ? ColorScheme.Dark : ColorScheme.Light, reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            var running = await page.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('.entrance, .entrance *')]
                .flatMap(e => [e, ...['::before', '::after'].map(p => ({ el: e, pseudo: p }))])
                .filter(x => getComputedStyle(x.el ?? x, x.pseudo ?? null).animationName !== 'none')
                .map(x => (x.el ?? x).className?.baseVal ?? (x.el ?? x).className)");
            Assert.Empty(running);

            await page.Mouse.MoveAsync(100, 100);
            await page.Mouse.MoveAsync(900, 500);
            Assert.Equal("", await page.EvaluateAsync<string>("() => document.querySelector('.entrance').style.getPropertyValue('--px')"));
        }

        // Desktop z myszą: scena żyje (ambient) i reaguje na wskaźnik (parallax warstw), pełna sekwencja ≤ ~1.6 s.
        [Fact]
        public async Task Desktop_PointerParallax_AndCinematicEntryWithinBudget()
        {
            await using var context = await _env.NewContextAsync(width: 1440, height: 900);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            Assert.Equal("sign-sway", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.saloon-sign')).animationName"));
            await page.Mouse.MoveAsync(1300, 150);
            await page.WaitForFunctionAsync("() => parseFloat(document.querySelector('.entrance').style.getPropertyValue('--px')) > 0.5");
            // Parallax porusza tło (chmury), nie podłoże: fasada ma 0 w pionie, pierwszy plan nie ma parallaxu wcale.
            Assert.NotEqual("none", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.entrance-clouds')).translate"));
            Assert.Matches(@"^-?[\d.]+px( 0px)?$", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.saloon')).translate"));
            Assert.Equal("none", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.entrance-foreground')).translate"));

            var clock = Stopwatch.StartNew();
            await Enter(page).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "1500");
            Assert.Equal("door-left", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.door-left')).animationName"));
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 3000 });
            clock.Stop();

            Assert.InRange(clock.ElapsedMilliseconds, 1300, 2300);   // 1500 ms sekwencji + narzut przeglądarki testowej
        }

        // Sprint 14B-FIX — linia gruntu: kursor w dowolnym miejscu (krawędzie i narożniki) nie przesuwa w pionie fasady,
        // wejścia, desek, pierwszego planu, tekstu ani tabliczek; fasada najwyżej ±2 px w poziomie; brak szczeliny
        // między fasadą a deskami. Tło (chmury) nadal reaguje. Po powrocie na Entrance scena jest w spoczynku.
        private const string GroundScript = @"() => {
            const r = s => { const b = document.querySelector(s).getBoundingClientRect(); return { x: b.x, y: b.y, bottom: b.bottom }; };
            return {
                saloon: r('.saloon'), door: r('.saloon-door'), boardwalk: r('.entrance-boardwalk'), foreground: r('.entrance-foreground'),
                lead: r('.entrance-lead'), signs: r('.entrance-signs'), town: r('.entrance-town'), clouds: r('.entrance-clouds')
            };
        }";

        [Theory]
        [InlineData(1440, 900, "light")]
        [InlineData(1440, 900, "dark")]
        [InlineData(1280, 800, "light")]
        [InlineData(1280, 800, "dark")]
        [InlineData(1024, 768, "light")]
        [InlineData(1024, 768, "dark")]
        [InlineData(768, 1024, "light")]
        [InlineData(768, 1024, "dark")]
        public async Task PointerParallax_NeverLiftsGround_OrMovesActions(int width, int height, string theme)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height, colorScheme: theme == "dark" ? ColorScheme.Dark : ColorScheme.Light);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            var rest = await page.EvaluateAsync<System.Text.Json.JsonElement>(GroundScript);

            var points = new (string Name, int X, int Y)[]
            {
                ("środek", width / 2, height / 2), ("góra", width / 2, 2), ("dół", width / 2, height - 2),
                ("lewo", 2, height / 2), ("prawo", width - 2, height / 2),
                ("lewy-górny", 2, 2), ("prawy-górny", width - 2, 2), ("lewy-dolny", 2, height - 2), ("prawy-dolny", width - 2, height - 2)
            };
            var cloudsMoved = false;
            foreach (var (name, x, y) in points)
            {
                await page.Mouse.MoveAsync(x, y);
                await page.WaitForTimeoutAsync(60);   // jedna–dwie klatki rAF
                var now = await page.EvaluateAsync<System.Text.Json.JsonElement>(GroundScript);
                var label = $"{theme} {width}px, kursor: {name}";

                foreach (var layer in new[] { "saloon", "door", "boardwalk", "foreground", "lead", "signs" })
                {
                    var dy = Math.Abs(now.GetProperty(layer).GetProperty("y").GetDouble() - rest.GetProperty(layer).GetProperty("y").GetDouble());
                    Assert.True(dy <= 0.5, $"{layer} przesunął się w pionie o {dy:F2} px ({label})");
                }
                foreach (var layer in new[] { "boardwalk", "foreground", "lead", "signs" })
                {
                    var dx = Math.Abs(now.GetProperty(layer).GetProperty("x").GetDouble() - rest.GetProperty(layer).GetProperty("x").GetDouble());
                    Assert.True(dx <= 0.5, $"{layer} przesunął się w poziomie o {dx:F2} px ({label})");
                }
                var saloonDx = Math.Abs(now.GetProperty("saloon").GetProperty("x").GetDouble() - rest.GetProperty("saloon").GetProperty("x").GetDouble());
                Assert.True(saloonDx <= 2.01, $"fasada przesunęła się w poziomie o {saloonDx:F2} px ({label})");

                // Brak szczeliny: fasada stoi na deskach, a miasteczko (z pasem pod sylwetką) sięga co najmniej do desek.
                var boardwalkTop = now.GetProperty("boardwalk").GetProperty("y").GetDouble();
                Assert.True(Math.Abs(now.GetProperty("saloon").GetProperty("bottom").GetDouble() - boardwalkTop) <= 1, $"szczelina pod fasadą ({label})");
                Assert.True(now.GetProperty("town").GetProperty("bottom").GetDouble() + 8 >= boardwalkTop, $"szczelina pod miasteczkiem ({label})");

                cloudsMoved |= Math.Abs(now.GetProperty("clouds").GetProperty("x").GetDouble() - rest.GetProperty("clouds").GetProperty("x").GetDouble()) > 2;
            }
            Assert.True(cloudsMoved, "Parallax tła powinien przesuwać chmury.");

            // Po wejściu i powrocie na Entrance — położenie spoczynkowe.
            await Enter(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 4000 });
            await page.EvaluateAsync("() => Blazor.navigateTo('/')");
            await Expect(page.Locator("h1#entrance-title")).ToBeVisibleAsync();
            var back = await page.EvaluateAsync<System.Text.Json.JsonElement>(GroundScript);
            foreach (var layer in new[] { "saloon", "boardwalk", "signs" })
                Assert.True(Math.Abs(back.GetProperty(layer).GetProperty("y").GetDouble() - rest.GetProperty(layer).GetProperty("y").GetDouble()) <= 0.5, $"{layer} nie wrócił do spoczynku po powrocie");
        }

        // Tylko klawiatura: Tab do tabliczek, Spacja wchodzi jako gość; Enter na "Zaloguj się" otwiera logowanie.
        [Fact]
        public async Task KeyboardOnly_Tab_Space_Enter_Work()
        {
            await using var context = await _env.NewContextAsync(reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Enter(page)).ToBeFocusedAsync();
            Assert.Equal("solid", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));
            await page.Keyboard.PressAsync("Space");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));

            await Ui.GotoAsync(page, "/");
            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).FocusAsync();
            // Sprint 14D: Enter na tabliczce otwiera kartę logowania (fokus w polu Email); bez circuitu — zwykły link /login.
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("dialog[open]").GetByLabel("Email")).ToBeFocusedAsync();
            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToHaveAttributeAsync("href", "/login?returnUrl=%2Fsaloon");
        }

        [Fact]
        public async Task Login_FromEntrance_LandsInSaloon_LogoutReturnsToEntrance()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            await Ui.GotoAsync(page, "/");
            // Sprint 14D: tabliczka otwiera "Kartę powrotu do Saloonu" (ten sam POST /auth/login).
            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).ClickAsync();
            var card = page.Locator("dialog[open]");
            await card.GetByLabel("Email").FillAsync(user.Email);
            await card.Locator("#card-login-password").FillAsync(E2EEnvironment.UserPassword);
            await card.GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).ClickAsync();

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);

            await page.Locator(".menu-trigger").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Wyloguj się" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));
            await Expect(page.Locator("h1#entrance-title")).ToBeVisibleAsync();
            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToBeVisibleAsync();
        }

        [Fact]
        public async Task Register_FromEntrance_LandsInSaloonSignedIn()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            // Sprint 14D: "Karta nowego przybysza" — rejestracja i logowanie tymi samymi danymi, potem /saloon.
            await Ui.GotoAsync(page, "/");
            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" }).ClickAsync();
            var card = page.Locator("dialog[open]");
            await Expect(card.Locator("output#alias-value")).Not.ToBeEmptyAsync();
            var alias = await card.Locator("output#alias-value").InnerTextAsync();
            var email = $"ent-{Guid.NewGuid():N}"[..16] + "@example.test";
            await card.Locator("#card-email").FillAsync(email);
            await card.Locator("#card-password").FillAsync(E2EEnvironment.UserPassword);
            await card.Locator("#card-confirm").FillAsync(E2EEnvironment.UserPassword);
            await card.GetByRole(AriaRole.Button, new() { Name = "Dołącz do Saloonu" }).ClickAsync();

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(alias);
        }

        // Zalogowany: bez tabliczek logowania/rejestracji, z przydomkiem i jednym wejściem.
        [Fact]
        public async Task LoggedUser_SeesAlias_AndSingleEnterAction()
        {
            await using var session = await Session.UserAsync(_env, reducedMotion: true);
            var page = session.Page;
            await Ui.GotoAsync(page, "/");

            await Expect(page.Locator(".entrance-lead")).ToContainTextAsync(session.User!.Alias);
            await Expect(Signs(page).GetByRole(AriaRole.Link)).ToHaveCountAsync(0);
            await Expect(Signs(page).GetByRole(AriaRole.Button)).ToHaveCountAsync(1);

            await Enter(page, "Wejdź do Saloonu").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(session.User.Alias);
        }

        // "Odkrywaj" pozostaje aktywne także z query stringiem (desktop i dolna nawigacja mobile).
        [Theory]
        [InlineData("/saloon")]
        [InlineData("/saloon?sort=popular")]
        [InlineData("/saloon?utm=x&sort=newest")]
        public async Task DiscoverNav_StaysActive_WithQueryString(string path)
        {
            await using var desktop = await _env.NewContextAsync();
            var page = await desktop.NewPageAsync();
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".primary-nav a.active")).ToHaveTextAsync(new Regex("Odkrywaj"));

            await using var mobile = await _env.NewContextAsync(width: 390, height: 844);
            var phone = await mobile.NewPageAsync();
            await Ui.GotoAsync(phone, path);
            await Expect(phone.Locator(".bottom-nav a.active")).ToHaveTextAsync(new Regex("Odkrywaj"));
        }

        [Fact]
        public async Task DiscoverNav_StaysActive_AfterClientSideSortChange()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/saloon");

            await page.GetByRole(AriaRole.Button, new() { Name = "Popularne" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?sort=popular"));
            await Expect(page.Locator(".primary-nav a.active")).ToHaveTextAsync(new Regex("Odkrywaj"));
        }
    }
}
