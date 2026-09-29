using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 22 — pełny interfejs PL/EN. Język z cookie "sp-lang" (prerender i circuit w tym samym języku) albo przełącznik
    // PL/EN (zmiana w miejscu, bez przeładowania: sesja, scena 3D, radio, połączenie SignalR i trwający pojedynek zostają).
    // Treści użytkowników (tytuły, przydomki) i marka SansPost nie są tłumaczone.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Localization")]
    public class LocalizationE2ETests
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

        // Teksty interfejsu, które po angielsku nie mogą się pojawić (treść użytkowników bywa polska — dlatego nie
        // sprawdzamy ogólnie polskich liter, tylko słowa z interfejsu).
        private static readonly string[] PolishUi =
        {
            "Zaloguj się", "Załóż konto", "Wyloguj", "Wczytywanie", "Pokaż starsze", "Szukaj postów", "Tablica Wanted",
            "Kącik muzyczny", "Stół gry", "Karta rozmów", "Spróbuj ponownie", "Przejdź do treści", "Nawigacja", "Odkrywaj"
        };

        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public LocalizationE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private async Task<IBrowserContext> EnglishContextAsync(int width = 1280, int height = 800, bool reducedMotion = true, bool gpu = false)
        {
            var context = await _env.NewContextAsync(width: width, height: height, reducedMotion: reducedMotion);
            await context.AddCookiesAsync(new[] { new Cookie { Name = "sp-lang", Value = "en", Url = _env.Main.BaseUrl } });
            if (gpu)
                await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task SwitchLanguageAsync(IPage page, string language)
        {
            await page.Locator($".language-option[lang='{language}']:visible").ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", language);
            await Expect(page.Locator($".language-option[lang='{language}']:visible")).ToHaveAttributeAsync("aria-pressed", "true");
        }

        private static async Task AssertNoPolishUiAsync(IPage page, string where)
        {
            var text = await page.EvaluateAsync<string>("() => document.body.innerText + ' ' + [...document.querySelectorAll('[aria-label],[title],[placeholder]')]" +
                ".map(e => (e.getAttribute('aria-label') || '') + ' ' + (e.getAttribute('title') || '') + ' ' + (e.getAttribute('placeholder') || '')).join(' ')");
            var found = PolishUi.Where(p => text.Contains(p, StringComparison.Ordinal)).ToList();
            Assert.True(found.Count == 0, $"{where}: polski tekst interfejsu w EN: {string.Join(", ", found)}");
        }

        private async Task AuditAsync(IPage page, string name, List<string> blocking)
        {
            var result = await page.RunAxe();
            foreach (var violation in result.Violations)
            {
                var line = $"{name}: [{violation.Impact}] {violation.Id} — {violation.Help}";
                _output.WriteLine(line);
                if (violation.Impact is "critical" or "serious")
                    blocking.Add(line);
            }
        }

        // Ekrany publiczne po angielsku: język dokumentu, brak polskiego interfejsu, axe 0 critical/serious,
        // bez poziomego przewijania na 1440/1024/768/390/360.
        [Fact]
        public async Task EnglishPublicScreens_Translated_Accessible_Responsive()
        {
            await using var context = await EnglishContextAsync();
            var page = await context.NewPageAsync();
            var blocking = new List<string>();

            foreach (var (path, name) in new[] { ("/", "Entrance"), ("/saloon", "Saloon"), ("/categories", "Categories"), ("/search?q=games", "Search"),
                         ("/login", "Login"), ("/register", "Register"), ("/post-view/2", "Post") })
            {
                await page.SetViewportSizeAsync(1280, 800);
                await Ui.GotoAsync(page, path);
                await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
                await AssertNoPolishUiAsync(page, name);
                await AuditAsync(page, $"{name} (EN)", blocking);
                foreach (var (w, h) in new[] { (1440, 900), (1024, 768), (768, 1024), (390, 844), (360, 740) })
                {
                    await page.SetViewportSizeAsync(w, h);
                    await Ui.AssertNoHorizontalOverflowAsync(page, $"{name} EN {w}px");
                }
            }

            // Karty Saloonu na Entrance (logowanie, rejestracja z przydomkiem) po angielsku.
            await page.SetViewportSizeAsync(1280, 800);
            await Ui.GotoAsync(page, "/");
            foreach (var sign in new[] { "Create account", "Sign in" })
            {
                await page.Locator("nav.entrance-signs").GetByRole(AriaRole.Link, new() { Name = sign }).ClickAsync();
                await Expect(page.Locator("dialog[open] .card-form")).ToBeVisibleAsync();
                await page.Locator("dialog[open]").EvaluateAsync("d => Promise.all(d.getAnimations({ subtree: true }).map(a => a.finished))");
                await AssertNoPolishUiAsync(page, $"Entrance card {sign}");
                await AuditAsync(page, $"Entrance card {sign} (EN)", blocking);
                await page.Keyboard.PressAsync("Escape");
                await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            }

            Assert.True(blocking.Count == 0, string.Join("\n", blocking));
        }

        // Main Hall 3D po angielsku: przyciski stref, napisy malowane w scenie, BAR, Wanted, Kącik muzyczny, Stół gry.
        [Fact]
        public async Task EnglishHall_Zones_SceneLabels_Panels()
        {
            await using var context = await EnglishContextAsync(reducedMotion: false, gpu: true);
            var page = await context.NewPageAsync();
            var blocking = new List<string>();
            await Ui.GotoAsync(page, "/saloon?scene=3d");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

            await Expect(page.Locator(".hall-zone-name")).ToHaveTextAsync(new[] { "Bar", "Wanted Board", "Game Table", "Music Corner" });
            Assert.Equal("GAME TABLE", await page.EvaluateAsync<string>("() => window.__sansPostHall.labels.game"));
            Assert.Equal("MUSIC", await page.EvaluateAsync<string>("() => window.__sansPostHall.labels.music"));
            await AssertNoPolishUiAsync(page, "Hall");
            await AuditAsync(page, "Main Hall 3D (EN)", blocking);

            await page.Locator(".hall-zone[data-zone='bar']").ClickAsync();
            await Expect(page.Locator("dialog[open] [data-bar-item='newest']")).ToBeVisibleAsync();
            await Expect(page.Locator("dialog[open]")).ToContainTextAsync("Conversation Menu");
            await AssertNoPolishUiAsync(page, "BAR");
            await AuditAsync(page, "BAR (EN)", blocking);
            await page.Locator("dialog[open] [data-bar-item='newest']").ClickAsync();
            await Expect(page.Locator("dialog[open]")).ToContainTextAsync("Latest");
            await AssertNoPolishUiAsync(page, "BAR list");
            await page.Keyboard.PressAsync("Escape");
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

            await page.Locator(".hall-zone[data-zone='wanted']").ClickAsync();
            await Expect(page.Locator("dialog[open]")).ToContainTextAsync("Most Wanted");
            await AssertNoPolishUiAsync(page, "Wanted");
            await AuditAsync(page, "Wanted (EN)", blocking);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

            await page.Locator(".hall-zone[data-zone='music']").ClickAsync();
            await Expect(page.Locator("dialog[open].music-panel")).ToBeVisibleAsync();
            await Expect(page.Locator("dialog[open] [data-music-toggle]")).ToContainTextAsync("Play");
            await AssertNoPolishUiAsync(page, "Music");
            await AuditAsync(page, "Music (EN)", blocking);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

            await page.Locator(".hall-zone[data-zone='game']").ClickAsync();
            await Expect(page.Locator("dialog[open].game-panel")).ToBeVisibleAsync();
            await Expect(page.Locator("dialog[open].game-panel")).ToContainTextAsync("The Gunslinger's Draw");
            await Expect(page.Locator("dialog[open] .game-card[data-card='shoot'] .game-card-name")).ToHaveTextAsync("Shoot");
            await Expect(page.Locator("dialog[open] .game-card[data-card='dodge'] .game-card-name")).ToHaveTextAsync("Dodge");
            await AssertNoPolishUiAsync(page, "Game");
            await AuditAsync(page, "Game Table (EN)", blocking);

            Assert.True(blocking.Count == 0, string.Join("\n", blocking));
        }

        // Przełącznik w miejscu: ta sama strona (bez przeładowania), ta sama scena, radio gra dalej (ten sam strumień),
        // trwający pojedynek na tym samym połączeniu SignalR (bez nowego negotiate) — i dalej po angielsku.
        [Fact]
        public async Task LanguageSwitch_InPlace_KeepsSceneAudioSignalRAndDuel()
        {
            var a = await Session.UserAsync(_env);
            var b = await Session.UserAsync(_env);
            await using var _a = a;
            await using var _b = b;
            await a.Context.AddInitScriptAsync(CapableGpu);
            await b.Context.AddInitScriptAsync(CapableGpu);
            var streams = await FakeRadio.RouteStreamsAsync(a.Context);

            var negotiations = 0;
            a.Page.Request += (_, request) => { if (request.Url.Contains("hubs/duel/negotiate", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref negotiations); };

            foreach (var page in new[] { a.Page, b.Page })
            {
                await Ui.GotoAsync(page, "/saloon?scene=3d");
                await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
                await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");
            }
            await a.Page.EvaluateAsync("() => { window.__langMarker = 'kept'; }");

            // Radio w sali (atrapa strumieni) — gra dalej po zamknięciu okna.
            await a.Page.Locator(".hall-zone[data-zone='music']").ClickAsync();
            await a.Page.Locator("dialog[open] [data-music-toggle]").ClickAsync();
            await a.Page.WaitForFunctionAsync("() => window.__sansPostMusic.status === 'playing'");
            var src = await a.Page.EvaluateAsync<string?>("() => document.getElementById('sp-radio')?.getAttribute('src') ?? null");
            await a.Page.Keyboard.PressAsync("Escape");
            await Expect(a.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await a.Page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

            // Pojedynek na żywo A–B (po polsku), potem A zamyka okno stołu — połączenie zostaje (trwający pojedynek).
            foreach (var page in new[] { a.Page, b.Page })
            {
                await page.Locator(".hall-zone[data-zone='game']").ClickAsync();
                await Expect(page.Locator("dialog[open].game-panel")).ToBeVisibleAsync();
                await page.WaitForFunctionAsync("() => window.sansPostDuel && window.sansPostDuel.state() === 'Connected'");
            }
            await a.Page.Locator("dialog[open].game-panel").GetByRole(AriaRole.Button, new() { Name = $"Wyzwij: {b.User!.Alias}", Exact = true }).ClickAsync();
            await b.Page.Locator("dialog[open].game-panel .game-invite").Filter(new() { HasText = a.User!.Alias })
                .GetByRole(AriaRole.Button, new() { Name = "Przyjmij" }).ClickAsync();
            await a.Page.Locator("dialog[open].game-panel .game-ready-button").ClickAsync();
            await b.Page.Locator("dialog[open].game-panel .game-ready-button:not([disabled])").ClickAsync();
            await Expect(a.Page.Locator("dialog[open].game-panel .game-board-title")).ToContainTextAsync("Runda 1 z 12");
            var duelId = await a.Page.EvaluateAsync<string>("async () => (await window.sansPostDuel.invoke('RequestState')).duel.duelId");
            await a.Page.Keyboard.PressAsync("Escape");
            await Expect(a.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await a.Page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");
            var negotiateBefore = Volatile.Read(ref negotiations);
            Assert.True(negotiateBefore >= 1);

            await SwitchLanguageAsync(a.Page, "en");

            Assert.Equal("kept", await a.Page.EvaluateAsync<string?>("() => window.__langMarker ?? null"));   // bez przeładowania
            Assert.Equal("Connected", await a.Page.EvaluateAsync<string>("() => window.sansPostDuel.state()"));
            Assert.Equal("playing", await a.Page.EvaluateAsync<string>("() => window.__sansPostMusic.status"));
            Assert.Equal(src, await a.Page.EvaluateAsync<string?>("() => document.getElementById('sp-radio')?.getAttribute('src') ?? null"));
            await a.Page.WaitForFunctionAsync("() => window.__sansPostHall.labels.game === 'GAME TABLE' && window.__sansPostHall.relabels === 1");
            Assert.False(await a.Page.EvaluateAsync<bool>("() => window.__sansPostHall.disposed"));
            await Expect(a.Page.Locator(".hall-zone-name")).ToHaveTextAsync(new[] { "Bar", "Wanted Board", "Game Table", "Music Corner" });
            await Expect(a.Page.Locator(".mini-player")).ToBeVisibleAsync();
            await AssertNoPolishUiAsync(a.Page, "Hall po przełączeniu");

            // Powrót do stołu: ten sam pojedynek, ta sama runda — po angielsku, ruch działa, rywal (PL) widzi rozstrzygnięcie.
            await a.Page.Locator(".hall-zone[data-zone='game']").ClickAsync();
            await Expect(a.Page.Locator("dialog[open].game-panel .game-board-title")).ToContainTextAsync("Round 1 of 12");
            Assert.Equal(duelId, await a.Page.EvaluateAsync<string>("async () => (await window.sansPostDuel.invoke('RequestState')).duel.duelId"));
            await Expect(a.Page.Locator("dialog[open] .game-card[data-card='reload'] .game-card-name")).ToHaveTextAsync("Reload");
            await a.Page.Locator("dialog[open] .game-card[data-card='reload']").ClickAsync();
            await b.Page.Locator("dialog[open] .game-card[data-card='reload']").ClickAsync();
            await Expect(b.Page.Locator("dialog[open].game-panel .game-history-list > li")).ToHaveCountAsync(1);
            await Expect(a.Page.Locator("dialog[open].game-panel .game-history-list > li")).ToHaveCountAsync(1);
            await Expect(b.Page.Locator("dialog[open].game-panel .game-board-title")).ToContainTextAsync("Runda 2 z 12");
            await Expect(a.Page.Locator("dialog[open].game-panel .game-board-title")).ToContainTextAsync("Round 2 of 12");
            Assert.Equal(negotiateBefore, Volatile.Read(ref negotiations));
            await AssertNoPolishUiAsync(a.Page, "Game po przełączeniu");

            // Sesja zostaje (nadal zalogowany), a po przeładowaniu strona startuje w wybranym języku.
            await a.Page.Keyboard.PressAsync("Escape");
            await Ui.GotoAsync(a.Page, "/saloon");
            await Expect(a.Page.Locator("html")).ToHaveAttributeAsync("lang", "en");
            await Expect(a.Page.Locator(".hall-corner a[href='/me']")).ToHaveAttributeAsync("aria-label", $"My account ({a.User.Alias})");

            // I z powrotem po polsku — w miejscu.
            await SwitchLanguageAsync(a.Page, "pl");
            await Expect(a.Page.Locator(".hall-zone-name")).ToHaveTextAsync(new[] { "Bar", "Tablica Wanted", "Stół gry", "Kącik muzyczny" });
            _output.WriteLine($"negotiate: {negotiateBefore}, strumienie radia: {streams[FakeRadioBrowser.Stations[0].Name]}");
        }

        // Mobile EN: dolna nawigacja i strony w wąskim widoku po angielsku, dostępne nazwy przycisków po angielsku.
        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task EnglishMobile_NavigationAndAccessibleNames(int width, int height)
        {
            await using var context = await EnglishContextAsync(width, height);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/categories");
            var nav = page.Locator("nav.bottom-nav");
            await Expect(nav).ToBeVisibleAsync();
            await Expect(nav).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("^[A-Za-z ]+$"));
            await AssertNoPolishUiAsync(page, $"Mobile {width}");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"Mobile EN {width}px");
            await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Interface language" }).First).ToBeAttachedAsync();
        }
    }
}
