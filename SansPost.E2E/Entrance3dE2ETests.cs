using System.Text.Json;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 14D — Entrance 3D (Three.js) jako renderer główny z fallbackiem CSS/SVG.
    // Chromium headless ma wyłącznie WebGL programowy (SwiftShader) — domyślne "/" wybiera więc CSS (to też jest test
    // decyzji). Scenę 3D testujemy przez "/?scene=3d" (pomija tylko kryteria wydajności), a samą decyzję "dobry GPU → 3D"
    // przez czystą funkcję chooseRenderer. Stan sceny: window.__sansPost3d (frames = narysowane klatki, doorAngle,
    // cameraZ, layout, porchY, saloonTopY, disposed).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Entrance3d")]
    public class Entrance3dE2ETests
    {
        private const string Scene3d = "/?scene=3d";
        private const string NoWebGl = @"(() => {
            const original = HTMLCanvasElement.prototype.getContext;
            HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
                return /webgl/i.test(type) ? null : original.call(this, type, ...rest);
            };
        })()";

        private readonly E2EEnvironment _env;

        public Entrance3dE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Signs(IPage page) => page.Locator("nav.entrance-signs");
        private static ILocator Enter(IPage page) => Signs(page).GetByRole(AriaRole.Button, new() { Name = "Wejdź jako gość" });
        private static ILocator Canvas(IPage page) => page.Locator(".entrance-3d canvas");
        private static Task<double> Debug(IPage page, string field) => page.EvaluateAsync<double>($"() => window.__sansPost3d.{field}");

        private static async Task Open3dAsync(IPage page)
        {
            await Ui.GotoAsync(page, Scene3d);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer", "3d", new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPost3d && window.__sansPost3d.frames >= 1");
        }

        // Decyzja zapadła (atrybut "pending" znika) i wypadła na CSS: bez canvas i bez klasy .has-3d.
        private static async Task AssertCssRendererAsync(IPage page, string reasonPattern)
        {
            await Expect(page.Locator(".entrance")).Not.ToHaveAttributeAsync("data-renderer-reason", "pending", new() { Timeout = 20_000 });
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer", "css");
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", new System.Text.RegularExpressions.Regex($"^({reasonPattern})$"));
            await Expect(Canvas(page)).ToHaveCountAsync(0);
            await Expect(page.Locator(".entrance.has-3d")).ToHaveCountAsync(0);
        }

        // Spoczynek: licznik klatek stoi przez 600 ms (w ciągu najwyżej 10 s).
        private static async Task<double> WaitIdleAsync(IPage page)
        {
            for (var i = 0; i < 16; i++)
            {
                var frames = await Debug(page, "frames");
                await page.WaitForTimeoutAsync(600);
                if (await Debug(page, "frames") == frames)
                    return frames;
            }
            throw new Xunit.Sdk.XunitException("Scena 3D nie przestaje rysować w spoczynku.");
        }

        private static Task<JsonElement> BoxAsync(ILocator locator) =>
            locator.EvaluateAsync<JsonElement>("e => { const b = e.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; }");

        private static double N(JsonElement box, string name) => box.GetProperty(name).GetDouble();

        // ---- Wybór renderera ----------------------------------------------------------------------------------

        // Domyślne "/" bez wydajnego GPU (tu: SwiftShader) → CSS/SVG, a Three.js w ogóle nie jest pobierany.
        [Fact]
        public async Task DefaultEntrance_WithSoftwareWebGl_FallsBackToCss_WithoutDownloadingThree()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            var requests = new List<string>();
            page.Request += (_, request) => requests.Add(request.Url);
            await Ui.GotoAsync(page, "/");

            await AssertCssRendererAsync(page, "software-gl|performance-caveat");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sansPost3d === undefined"));
            Assert.Contains(requests, url => url.EndsWith("/js/entrance3d.js"));
            Assert.DoesNotContain(requests, url => url.Contains("three.module"));
            Assert.Equal("1", await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('.saloon')).opacity"));
        }

        // Czysta funkcja decyzji: 3D tylko przy sprzętowym WebGL2 bez zastrzeżeń wydajności; reduced motion i brak
        // WebGL zawsze CSS (także przy wymuszeniu).
        [Fact]
        public async Task RendererDecision_ChoosesThree_OnlyForCapableGpu()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/?scene=css");

            var pairs = await page.EvaluateAsync<string[]>(@"async () => {
                const { chooseRenderer } = await import('./js/entrance3d.js');
                const gpu = { webgl: true, rendererName: 'ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11)', deviceMemory: 8 };
                const cases = {
                    gpu,
                    integratedGpu: { ...gpu, rendererName: 'ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11)' },
                    appleGpu: { ...gpu, rendererName: 'Apple M2' },
                    swiftShader: { ...gpu, rendererName: 'ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero)), SwiftShader driver)' },
                    llvmpipe: { ...gpu, rendererName: 'llvmpipe (LLVM 15.0.7, 256 bits)' },
                    basicRender: { ...gpu, rendererName: 'ANGLE (Microsoft, Microsoft Basic Render Driver Direct3D11)' },
                    caveat: { ...gpu, majorPerformanceCaveat: true },
                    noWebGl: { ...gpu, webgl: false },
                    reducedMotion: { ...gpu, reducedMotion: true },
                    lowMemory: { ...gpu, deviceMemory: 2 },
                    saveData: { ...gpu, saveData: true },
                    forcedSoftware: { ...gpu, rendererName: 'SwiftShader', force: true },
                    forcedReducedMotion: { ...gpu, force: true, reducedMotion: true },
                    forcedNoWebGl: { ...gpu, force: true, webgl: false }
                };
                return Object.entries(cases).map(([name, caps]) => { const d = chooseRenderer(caps); return name + '=' + d.renderer + ':' + d.reason; });
            }");
            var results = pairs.Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);

            Assert.Equal("3d:gpu", results["gpu"]);
            Assert.Equal("3d:gpu", results["integratedGpu"]);
            Assert.Equal("3d:gpu", results["appleGpu"]);
            Assert.Equal("css:software-gl", results["swiftShader"]);
            Assert.Equal("css:software-gl", results["llvmpipe"]);
            Assert.Equal("css:software-gl", results["basicRender"]);
            Assert.Equal("css:performance-caveat", results["caveat"]);
            Assert.Equal("css:no-webgl", results["noWebGl"]);
            Assert.Equal("css:reduced-motion", results["reducedMotion"]);
            Assert.Equal("css:low-memory", results["lowMemory"]);
            Assert.Equal("css:save-data", results["saveData"]);
            Assert.Equal("3d:forced", results["forcedSoftware"]);
            Assert.Equal("css:reduced-motion", results["forcedReducedMotion"]);
            Assert.Equal("css:no-webgl", results["forcedNoWebGl"]);
        }

        [Fact]
        public async Task SceneCss_ForcesCssRenderer_WithoutWebGlHost()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/?scene=css");

            await AssertCssRendererAsync(page, "forced");
            await Expect(page.Locator(".entrance-3d")).ToHaveCountAsync(0);
        }

        // Brak WebGL (getContext → null) przy wymuszonym 3D: zostaje pełna scena CSS z jej animacją wejścia.
        [Fact]
        public async Task Scene3d_WithoutWebGl_FallsBackToCssEntrance()
        {
            await using var context = await _env.NewContextAsync();
            await context.AddInitScriptAsync(NoWebGl);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, Scene3d);

            await AssertCssRendererAsync(page, "no-webgl");
            await Enter(page).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "1500");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));
        }

        // Reduced motion: WebGL nie startuje wcale (nawet wymuszony), wejście natychmiast.
        [Fact]
        public async Task Scene3d_ReducedMotion_UsesCss_AndEntersImmediately()
        {
            await using var context = await _env.NewContextAsync(reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, Scene3d);

            await AssertCssRendererAsync(page, "reduced-motion");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sansPost3d === undefined"));
            await Enter(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 1000 });
        }

        // ---- Scena i tabliczki -------------------------------------------------------------------------------

        // Desktop: tabliczki HTML wiszą na tablicach ogłoszeń przy drzwiach — logowanie i rejestracja po lewej (jedna
        // nad drugą), wejście gościa po prawej; wszystkie nad linią ganku, w viewport, bez nakładania się.
        [Theory]
        [InlineData(1440, 900)]
        [InlineData(1024, 768)]
        public async Task Scene3d_Desktop_HangsPlaquesOnNoticeBoardsBesideDoors(int width, int height)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);

            Assert.Equal("porch", await page.EvaluateAsync<string>("() => window.__sansPost3d.layout"));
            await Expect(page.Locator(".entrance-boardwalk")).ToHaveAttributeAsync("data-sign-layout", "porch");
            var login = await BoxAsync(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }));
            var register = await BoxAsync(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" }));
            var guest = await BoxAsync(Enter(page));
            var porchY = await Debug(page, "porchY");

            foreach (var box in new[] { login, register, guest })
            {
                Assert.InRange(N(box, "x"), 0, width - N(box, "w"));
                Assert.InRange(N(box, "y") + N(box, "h"), 0, porchY);
                Assert.True(N(box, "h") >= 40, $"Tabliczka za niska: {box}");
            }
            Assert.True(N(login, "x") + N(login, "w") < width / 2.0, "Logowanie po lewej stronie drzwi.");
            Assert.True(N(guest, "x") > width / 2.0, "Wejście gościa po prawej stronie drzwi.");
            Assert.True(N(login, "y") + N(login, "h") <= N(register, "y"), "Logowanie nad rejestracją, bez nakładania.");
            Assert.True(await page.EvaluateAsync<bool>("() => [...document.querySelectorAll('.entrance-sign')].every(e => e.scrollWidth <= e.clientWidth + 1)"),
                "Tekst tabliczki mieści się w jednej linii.");
            await Expect(page.Locator(".entrance-3d")).ToHaveAttributeAsync("aria-hidden", "true");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "SansPost", Level = 1 })).ToHaveCountAsync(1);
            await Ui.AssertNoHorizontalOverflowAsync(page, $"3d {width}");
        }

        // Wąsko/pionowo: saloon w całości w górnej części kadru, tabliczki pod nim (drogowskaz), bez nachodzenia na budynek.
        [Theory]
        [InlineData(390, 844, "light")]
        [InlineData(360, 740, "dark")]
        [InlineData(768, 1024, "light")]
        public async Task Scene3d_Narrow_StacksPlaquesBelowSaloon(int width, int height, string theme)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height, colorScheme: theme == "dark" ? ColorScheme.Dark : ColorScheme.Light);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);

            await Expect(page.Locator(".entrance-boardwalk")).ToHaveAttributeAsync("data-sign-layout", "stack");
            await page.WaitForFunctionAsync("() => window.__sansPost3d.layout === 'stack'");
            await WaitIdleAsync(page);
            var porchY = await Debug(page, "porchY");
            var lead = await BoxAsync(page.Locator(".entrance-lead"));
            Assert.True(await Debug(page, "saloonTopY") >= 0, "Szczyt saloonu w kadrze.");
            Assert.True(N(lead, "y") >= porchY - 2, $"Tekst i tabliczki pod saloonem (ganek {porchY}px, tekst {N(lead, "y")}px).");
            Assert.True(porchY / height >= 0.45, $"Saloon zajmuje górną część kadru, ganek na {porchY / height:P0}.");
            foreach (var sign in await Signs(page).Locator(".entrance-sign").AllAsync())
            {
                var box = await BoxAsync(sign);
                Assert.InRange(N(box, "x"), 0, width - N(box, "w"));
                Assert.InRange(N(box, "y") + N(box, "h"), 0, height);
            }
            await Ui.AssertNoHorizontalOverflowAsync(page, $"3d {width}");
        }

        // ---- Wejście, klawiatura, resize, cykl życia ------------------------------------------------------------

        // Gość: tabliczki znikają, drzwi (osobne pivoty) się otwierają, kamera przechodzi przez próg, routing Blazora
        // bez przeładowania; po opuszczeniu "/" pętla stoi, canvas usunięty, kontekst zwolniony.
        [Fact]
        public async Task Scene3d_GuestEntry_OpensDoors_CameraThroughDoor_NavigatesWithoutReload_AndDisposes()
        {
            await using var context = await _env.NewContextAsync(width: 900, height: 640);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);
            await page.EvaluateAsync("() => window.__sameDocument = true");
            var startZ = await Debug(page, "cameraZ");
            Assert.Equal(0, await Debug(page, "doorAngle"));

            await Enter(page).ClickAsync();
            await Expect(page.Locator(".entrance.is-entering")).ToHaveAttributeAsync("data-enter-ms", "1800");
            await page.WaitForFunctionAsync("() => getComputedStyle(document.querySelector('.entrance-boardwalk')).opacity === '0'");   // tabliczki znikają
            await page.WaitForFunctionAsync("() => window.__sansPost3d.doorAngle > 70", null, new() { Timeout = 10_000 });
            await page.WaitForFunctionAsync("() => window.__sansPost3d.cameraZ < 0", null, new() { Timeout = 10_000 });
            Assert.True(startZ > 10, $"Kamera startuje przed saloonem (z = {startZ}).");

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sameDocument === true"), "Wejście nie może przeładowywać strony.");

            await page.WaitForFunctionAsync("() => window.__sansPost3d.disposed === true");
            await Expect(page.Locator("canvas")).ToHaveCountAsync(0);
            var frames = await Debug(page, "frames");
            await page.WaitForTimeoutAsync(500);
            Assert.Equal(frames, await Debug(page, "frames"));
        }

        [Fact]
        public async Task Scene3d_DoubleClick_DuringEntry_NavigatesOnce()
        {
            await using var context = await _env.NewContextAsync(width: 900, height: 640);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);
            var navigations = 0;
            page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };

            await Enter(page).ClickAsync();
            await Enter(page).ClickAsync(new() { Force = true });
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(900);
            Assert.Equal(1, navigations);
        }

        [Fact]
        public async Task Scene3d_KeyboardOnly_Tab_Space_Enters()
        {
            await using var context = await _env.NewContextAsync(width: 1100, height: 760);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);

            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Signs(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Enter(page)).ToBeFocusedAsync();
            Assert.Equal("solid", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));
            await page.Keyboard.PressAsync("Space");

            await page.WaitForFunctionAsync("() => window.__sansPost3d.doorAngle > 70", null, new() { Timeout = 10_000 });
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
        }

        // Resize desktop → mobile: canvas podąża za viewportem, tabliczki przechodzą z tablic przy drzwiach na drogowskaz.
        [Fact]
        public async Task Scene3d_Resize_FollowsViewport_AndSwitchesPlaqueLayout()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);
            Assert.Equal(1280, await page.EvaluateAsync<int>("() => document.querySelector('.entrance-3d canvas').clientWidth"));
            await Expect(page.Locator(".entrance-boardwalk")).ToHaveAttributeAsync("data-sign-layout", "porch");

            var frames = await Debug(page, "frames");
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.querySelector('.entrance-3d canvas').clientWidth === 390");
            await page.WaitForFunctionAsync($"() => window.__sansPost3d.frames > {frames}");
            await Expect(page.Locator(".entrance-boardwalk")).ToHaveAttributeAsync("data-sign-layout", "stack");
            await Ui.AssertNoHorizontalOverflowAsync(page, "3d po resize 390");

            await page.SetViewportSizeAsync(1280, 800);
            await Expect(page.Locator(".entrance-boardwalk")).ToHaveAttributeAsync("data-sign-layout", "porch");
        }

        // Kamera nie reaguje na kursor (koniec "trzęsącej się" sceny); statyczna scena nie rysuje klatek.
        // Zmiana motywu dorysowuje klatkę.
        [Fact]
        public async Task Scene3d_StableAtRest_IgnoresPointer_RedrawsOnThemeChange()
        {
            await using var context = await _env.NewContextAsync(width: 1100, height: 760);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);
            var idle = await WaitIdleAsync(page);
            var cameraZ = await Debug(page, "cameraZ");

            foreach (var (x, y) in new[] { (10, 10), (1090, 120), (550, 380), (40, 700), (1080, 740) })
                await page.Mouse.MoveAsync(x, y, new() { Steps = 4 });
            await page.WaitForTimeoutAsync(600);
            Assert.Equal(idle, await Debug(page, "frames"));
            Assert.Equal(cameraZ, await Debug(page, "cameraZ"));

            await page.Locator("[data-theme-toggle]").ClickAsync();
            await page.WaitForFunctionAsync($"() => window.__sansPost3d.frames > {idle}");
        }

        // Logowanie kartą na scenie 3D: karta znika, drzwi się otwierają, potem /saloon (pełne przeładowanie z cookie).
        [Fact]
        public async Task Scene3d_LoginCard_OpensDoors_ThenSaloonAsUser()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync(width: 1100, height: 760);
            var page = await context.NewPageAsync();
            await Open3dAsync(page);

            await Signs(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" }).ClickAsync();
            var card = page.Locator("dialog[open]");
            await card.GetByLabel("Email").FillAsync(user.Email);
            await card.Locator("#card-login-password").FillAsync(E2EEnvironment.UserPassword);
            await card.GetByRole(AriaRole.Button, new() { Name = "Wróć do Saloonu" }).ClickAsync();

            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPost3d.doorAngle > 70", null, new() { Timeout = 10_000 });
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".menu-trigger")).ToContainTextAsync(user.Alias);
            Assert.Equal(0, await page.Locator("canvas").CountAsync());
        }
    }
}
