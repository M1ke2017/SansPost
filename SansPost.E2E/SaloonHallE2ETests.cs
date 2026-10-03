using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 15 — Saloon Main Hall 3D (/saloon). Ta sama decyzja renderera co Entrance: Pending → ThreeD (sala) albo
    // Pending → CssFallback (klasyczny feed). Chromium headless ma WebGL programowy, więc domyślne /saloon to fallback;
    // salę testujemy przez "?scene=3d" (pomija tylko kryteria wydajności), a przejście Entrance → sala z symulacją
    // sprzętowego GPU (jak EntranceRendererFlashE2ETests). Stan sali: window.__sansPostHall (frames, area, hover, moving,
    // cameraX/Y/Z, points — rzut stref w kadrze głównym, drawCalls, triangles, disposed).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "SaloonHall")]
    public class SaloonHallE2ETests
    {
        private const string Hall3d = "/saloon?scene=3d";

        // Każda klatka: czy klasyczny feed jest widoczny, czy trwa Pending, czy sala gotowa, wysokość kamery.
        private const string Sampler = @"(() => {
            const f = window.__hallTrace = { samples: [] };
            const vis = el => !!el && el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) && parseFloat(getComputedStyle(el).opacity) > 0.02;
            const sample = t => {
                if (location.pathname === '/saloon') f.samples.push({
                    classic: [...document.querySelectorAll('.saloon-classic .frontier-banner, .saloon-classic .layout-with-aside')].some(vis),
                    pending: vis(document.querySelector('.hall-pending')),
                    ready: !!document.querySelector('.saloon-hall.is-ready'),
                    cameraY: window.__sansPostHall ? window.__sansPostHall.cameraY ?? null : null
                });
                if (!f.stop) requestAnimationFrame(sample);
            };
            requestAnimationFrame(sample);
        })()";

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

        public SaloonHallE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private sealed record Sample(bool Classic, bool Pending, bool Ready, double? CameraY);

        private static ILocator Zone(IPage page, string zone) => page.Locator($".hall-zone[data-zone='{zone}']");
        private static ILocator Panel(IPage page) => page.Locator("dialog[open]");
        private static Task<double> Debug(IPage page, string field) => page.EvaluateAsync<double>($"() => window.__sansPostHall.{field}");
        private static Task<int> CanvasCount(IPage page) => page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length");
        private static ILocator Helper(IPage page) => page.Locator("nav.hall-zones");
        private static ILocator Caption(IPage page) => page.Locator(".hall-zones-caption");
        private static readonly Regex Compact = new("is-compact");
        private static readonly string[] AllZones = { "bar", "wanted", "game", "music" };
        private static string Js(double value) => value.ToString(CultureInfo.InvariantCulture);

        // Klik w canvas w rzucie strefy z kadru głównego (window.__sansPostHall.points) — z kontrolą, że w tym miejscu
        // nie leży żaden element HTML (pasek, zapowiedź), czyli klik naprawdę trafia w scenę 3D.
        private static async Task ClickZoneInSceneAsync(IPage page, string zone, double dx = 0, double dy = 0)
        {
            var point = await page.EvaluateAsync<double[]>($"() => window.__sansPostHall.points.{zone}");
            var box = (await page.Locator(".saloon-hall canvas").BoundingBoxAsync())!;
            var x = box.X + point[0] + dx;
            var y = box.Y + point[1] + dy;
            Assert.True(x > box.X && x < box.X + box.Width && y > box.Y && y < box.Y + box.Height, $"Strefa {zone} poza kadrem ({x:0}, {y:0}).");
            Assert.Equal("CANVAS", await page.EvaluateAsync<string>($"() => document.elementFromPoint({Js(x)}, {Js(y)})?.tagName"));
            await page.Mouse.ClickAsync((float)x, (float)y);
        }

        private static async Task AssertNoOverlapAsync(IPage page)
        {
            var boxes = new List<(string Zone, LocatorBoundingBoxResult Box)>();
            foreach (var zone in AllZones) boxes.Add((zone, (await Zone(page, zone).BoundingBoxAsync())!));
            for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (a, b) = (boxes[i].Box, boxes[j].Box);
                var overlap = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Assert.False(overlap, $"Przyciski {boxes[i].Zone} i {boxes[j].Zone} nachodzą na siebie.");
            }
        }

        private static Task WaitHelperOpacityAsync(IPage page, string condition) =>
            page.WaitForFunctionAsync($"() => parseFloat(getComputedStyle(document.querySelector('nav.hall-zones')).opacity) {condition}");

        private static async Task OpenHallAsync(IPage page, string path = Hall3d)
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await WaitCameraStillAsync(page);
        }

        // Kamera stoi: przejazd zakończony (saloon3d ustawia "moving" na końcu animacji, w tej samej klatce co pozycję).
        private static Task WaitCameraStillAsync(IPage page) =>
            page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");

        private static async Task<List<Sample>> StopTraceAsync(IPage page)
        {
            var json = await page.EvaluateAsync<string>("() => { window.__hallTrace.stop = true; return JSON.stringify(window.__hallTrace.samples); }");
            return JsonSerializer.Deserialize<List<Sample>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        // ---- Start ---------------------------------------------------------------------------------------------

        // Bezpośrednie /saloon z salą 3D: Pending → sala; klasyczny feed ani razu, jeden canvas, cztery strefy w HTML.
        [Fact]
        public async Task DirectSaloon_Hall3d_PendingThenHall_WithoutClassicFlash()
        {
            await using var context = await _env.NewContextAsync(width: 1024, height: 700);
            await context.AddInitScriptAsync(Sampler);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var samples = await StopTraceAsync(page);
            Assert.DoesNotContain(samples, s => s.Classic);
            Assert.Contains(samples, s => s.Pending);
            Assert.Equal(1, await CanvasCount(page));
            await Expect(page.Locator(".saloon-hall canvas")).ToHaveCountAsync(1);
            await Expect(page.Locator(".hall-3d")).ToHaveAttributeAsync("aria-hidden", "true");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Saloon SansPost — sala główna");
            var zones = page.GetByRole(AriaRole.Navigation, new() { Name = "Strefy Saloonu" }).GetByRole(AriaRole.Button);
            await Expect(zones).ToHaveCountAsync(4);
            foreach (var name in new[] { "Bar", "Tablica Wanted", "Stół gry", "Kącik muzyczny" })
                await Expect(zones.Filter(new() { HasText = name })).ToBeVisibleAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("() => document.querySelectorAll('.saloon-classic').length"));
            Assert.True(await Debug(page, "drawCalls") is > 0 and < 60, "Scena scalona (batching + instancing).");

            // Render na żądanie: sala w spoczynku nie rysuje klatek (brak bezczynnej pętli 60 FPS).
            var frames = await Debug(page, "frames");
            await page.WaitForTimeoutAsync(800);
            Assert.Equal(frames, await Debug(page, "frames"));
            await Ui.AssertNoHorizontalOverflowAsync(page, "sala 1024");
        }

        // WebGL programowy (domyślne /saloon w headless): Pending → klasyczny feed dopiero po decyzji, bez canvas i Three.js.
        // Następne wejście z zapamiętanym fallbackiem: klasyczny widok od razu z serwera.
        [Fact]
        public async Task DirectSaloon_SoftwareRenderer_PendingThenClassicFallback()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            var requests = new List<string>();
            page.Request += (_, request) => requests.Add(request.Url);
            var html = await (await context.APIRequest.GetAsync("/saloon")).TextAsync();
            Assert.Contains("saloon-classic is-pending", html);   // treść dla robotów i bez JS, ukryta do decyzji

            await Ui.GotoAsync(page, "/saloon");
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
            await Expect(page.Locator(".saloon-classic:not(.is-pending) .post-card").First).ToBeVisibleAsync();
            Assert.Equal(0, await CanvasCount(page));
            Assert.DoesNotContain(requests, url => url.Contains("three.module"));
            Assert.Equal("css", (await context.CookiesAsync()).First(c => c.Name == "sp-scene").Value);
            await Expect(page.Locator(".app-header")).ToBeVisibleAsync();   // nagłówek ukrywa tylko sala 3D
            await Expect(page.Locator(".hall-corner")).ToHaveCountAsync(0);

            var known = await (await context.APIRequest.GetAsync("/saloon")).TextAsync();
            Assert.DoesNotContain("class=\"saloon-hall", known);
            Assert.DoesNotContain("class=\"saloon-classic is-pending", known);
            Assert.Contains("class=\"saloon-classic", known);
        }

        // Entrance 3D → drzwi → /saloon → sala 3D: bez klasycznego feedu po drodze, kamera zaczyna na progu i osiada w kadrze.
        [Fact]
        public async Task EntranceThroughDoors_ToHall3d_NoClassicFlash_CameraSettles()
        {
            await using var context = await _env.NewContextAsync(width: 1024, height: 700);
            await context.AddInitScriptAsync(CapableGpu);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer", "3d");
            await page.EvaluateAsync(Sampler);

            await page.GetByRole(AriaRole.Button, new() { Name = "Wejdź jako gość" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"), new() { Timeout = 10_000 });
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await WaitCameraStillAsync(page);

            var samples = await StopTraceAsync(page);
            Assert.NotEmpty(samples);
            Assert.DoesNotContain(samples, s => s.Classic);
            var heights = samples.Where(s => s.Ready && s.CameraY is not null).Select(s => s.CameraY!.Value).ToList();
            Assert.True(heights.First() < 2.0, $"Sala zaczyna na progu (wysokość kamery {heights.First():0.00}).");
            Assert.Equal(await page.EvaluateAsync<double>("() => window.__sansPostHall.main[1]"), await Debug(page, "cameraY"), 2);   // po przejeździe: kadr główny
            Assert.Equal(1, await CanvasCount(page));
        }

        // ---- BAR ---------------------------------------------------------------------------------------------

        // Klik w bar na canvas (raycasting) — kamera podchodzi do baru, otwiera się Karta rozmów (Sprint 16, HTML).
        // Escape na Karcie: okno znika, kamera wraca do kadru głównego.
        [Fact]
        public async Task BarCanvasClick_OpensConversationMenu_EscapeClosesAndResetsCamera()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await Debug(page, "cameraZ");

            await ClickZoneInSceneAsync(page, "bar");

            await Expect(Panel(page).GetByRole(AriaRole.Heading, new() { Name = "Karta rozmów" })).ToBeVisibleAsync();
            await Expect(Panel(page).Locator("[data-bar-item='newest']")).ToBeVisibleAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            Assert.True(await Debug(page, "cameraZ") < -4, "Kamera podeszła do baru.");

            await page.Keyboard.PressAsync("Escape");
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
            await WaitCameraStillAsync(page);
            Assert.Equal(mainZ, await Debug(page, "cameraZ"), 2);
        }

        // ---- Pasek stref (onboarding) ----------------------------------------------------------------------------

        // Desktop: nad salą 3D nie ma nagłówka aplikacji — sala zajmuje cały ekran. Dyskretny narożnik zostawia tylko
        // globalne akcje (wyjście, motyw, logowanie) — bez wyszukiwania i kategorii (trafią do BAR); pasek stref to jedna
        // niska linia u dołu. Po wyjściu z sali nagłówek wraca.
        [Fact]
        public async Task Desktop_Hall3d_WithoutAppHeader_CornerAndLightDock()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await Expect(page.Locator(".app-header")).ToBeHiddenAsync();
            var hall = (await page.Locator(".saloon-hall").BoundingBoxAsync())!;
            Assert.True(hall.Y <= 0.5 && hall.Height >= 799, $"Sala na cały ekran (y {hall.Y}, h {hall.Height}).");
            Assert.Equal(0, await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight - innerHeight"));

            var corner = page.GetByRole(AriaRole.Navigation, new() { Name = "SansPost" });
            await Expect(corner.GetByRole(AriaRole.Link, new() { Name = "SansPost" })).ToHaveAttributeAsync("href", "/");
            await Expect(corner.GetByRole(AriaRole.Button, new() { Name = "Przełącz jasny lub ciemny motyw" })).ToBeVisibleAsync();
            // Sprint 16 final: "Zaloguj się" otwiera kartę Saloonu nad salą (nie klasyczną stronę) — BarAuth / HallAuth testy.
            await Expect(corner.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się" })).ToHaveAttributeAsync("aria-haspopup", "dialog");
            await Expect(corner.GetByRole(AriaRole.Link)).ToHaveCountAsync(1);   // tylko wyjście; logowanie to przycisk karty
            await Expect(page.Locator(".saloon-hall a[href^='/search'], .saloon-hall a[href='/categories']")).ToHaveCountAsync(0);

            // Dok: jedna linia, lekki (półprzezroczysty), przyciski nadal wygodne do kliknięcia.
            var dock = (await Helper(page).BoundingBoxAsync())!;
            Assert.True(dock.Height < 60, $"Dok stref w jednej linii (wysokość {dock.Height}).");
            foreach (var zone in AllZones)
                Assert.True((await Zone(page, zone).BoundingBoxAsync())!.Height >= 34, $"Przycisk {zone} za niski.");
            Assert.True(await Helper(page).EvaluateAsync<double>("n => parseFloat(getComputedStyle(n).backgroundColor.split(',')[3] ?? '1')") < 0.6, "Tło doku półprzezroczyste.");
            await Ui.AssertNoHorizontalOverflowAsync(page, "sala 1280");

            await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
            await Expect(page).ToHaveURLAsync(Ui.Path("/categories"));
            await Expect(page.Locator(".app-header")).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.disposed === true");
        }

        // Desktop: pierwsze wejście w sesji — pełny pasek z podpisem. Po pierwszym świadomym wyborze strefy pasek staje się
        // kompaktowy (bez podpisu, przygaszony, pełny przy fokusie) i zostaje taki przy kolejnych wejściach w tej sesji.
        // Nowa sesja — znów onboarding.
        [Fact]
        public async Task HelperBar_Desktop_FullOnFirstVisit_CompactAfterFirstZone()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await Expect(Caption(page)).ToBeVisibleAsync();
            await Expect(Helper(page)).Not.ToHaveClassAsync(Compact);
            await WaitHelperOpacityAsync(page, "=== 1");
            var nav = (await Helper(page).BoundingBoxAsync())!;
            Assert.True(nav.Y + nav.Height <= 800 && nav.Y > 400, $"Pasek u dołu sali (y {nav.Y}, h {nav.Height}).");
            await AssertNoOverlapAsync(page);

            await Zone(page, "game").ClickAsync();
            await Expect(page.Locator("dialog[open].game-panel")).ToBeVisibleAsync();
            await Expect(Helper(page)).ToHaveClassAsync(Compact);
            await Expect(Caption(page)).ToHaveCountAsync(0);
            Assert.Equal("1", await page.EvaluateAsync<string>("() => sessionStorage.getItem('sp-hall-onboarded')"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");

            // Kolejne wejście w tej samej sesji: od razu kompaktowy; przygaszony, ale pełny przy fokusie klawiatury.
            await OpenHallAsync(page);
            await Expect(Helper(page)).ToHaveClassAsync(Compact);
            await Expect(Caption(page)).ToHaveCountAsync(0);
            await page.Mouse.MoveAsync(5, 5);
            await WaitHelperOpacityAsync(page, "< 0.9");
            await Zone(page, "wanted").FocusAsync();
            await WaitHelperOpacityAsync(page, "=== 1");
            await AssertNoOverlapAsync(page);
            foreach (var zone in AllZones)
                Assert.True((await Zone(page, zone).BoundingBoxAsync())!.Height >= 30, $"Przycisk {zone} za mały w trybie kompaktowym.");

            await using var fresh = await _env.NewContextAsync(width: 1280, height: 800);
            var next = await fresh.NewPageAsync();
            await OpenHallAsync(next);
            await Expect(Caption(next)).ToBeVisibleAsync();
            await Expect(Helper(next)).Not.ToHaveClassAsync(Compact);
        }

        // Klawiatura na pasku: Tab / Shift+Tab po strefach (fokus widoczny, strefa podświetlona w sali), Enter otwiera,
        // Escape zamyka i zwraca fokus na przycisk, który otworzył strefę.
        [Fact]
        public async Task HelperBar_Keyboard_TabShiftTabEnterEscape_FocusReturns()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await Zone(page, "bar").FocusAsync();
            foreach (var zone in new[] { "wanted", "game", "music" })
            {
                await page.Keyboard.PressAsync("Tab");
                await Expect(Zone(page, zone)).ToBeFocusedAsync();
                Assert.Equal("solid", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));
                Assert.Equal(zone, await page.EvaluateAsync<string>("() => window.__sansPostHall.hover"));
            }
            await page.Keyboard.PressAsync("Shift+Tab");
            await Expect(Zone(page, "game")).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("dialog[open].game-panel")).ToBeVisibleAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?game=table"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(Zone(page, "game")).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // Tylko klawiatura: Tab do strefy BAR, Enter → Karta rozmów (fokus w oknie), Shift+Tab nie trafia do sali,
        // "Wszystkie rozmowy" z sortowaniem (istniejący feed), "Zamknij" → fokus wraca na BAR.
        [Fact]
        public async Task BarKeyboard_OpenSortClose_FocusReturns()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var bar = Zone(page, "bar");
            await bar.FocusAsync();
            Assert.Equal("solid", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.hover"));   // fokus podświetla strefę w sali
            await page.Keyboard.PressAsync("Enter");

            await Expect(Panel(page).Locator("[data-bar-item='newest']")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('dialog[open]')"), "Shift+Tab nie wychodzi z panelu.");

            await Panel(page).Locator("[data-bar-item='all']").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Panel(page).Locator(".post-card").First).ToBeVisibleAsync();
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=all&sort=popular"));
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");

            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(bar).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // Strefy Wanted, Music i Game mają własne testy (WantedBoardE2ETests, MusicCornerE2ETests, GameTableE2ETests).

        // ---- Reduced motion, mobile ----------------------------------------------------------------------------

        // Reduced motion (wspólna decyzja): /saloon to klasyczny feed — wszystkie funkcje działają, bez sali.
        // Gdy reduced motion włączy się przy otwartej sali: wybór BAR bez przejazdu — kamera i panel od razu.
        [Fact]
        public async Task ReducedMotion_ClassicFeed_AndInstantBarInOpenHall()
        {
            await using (var reduced = await _env.NewContextAsync(reducedMotion: true))
            {
                var page = await reduced.NewPageAsync();
                await Ui.GotoAsync(page, Hall3d);
                await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
                await Expect(page.Locator(".saloon-classic .post-card").First).ToBeVisibleAsync();
                Assert.Equal(0, await CanvasCount(page));
            }

            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var hall = await context.NewPageAsync();
            await OpenHallAsync(hall);
            await hall.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await Zone(hall, "bar").ClickAsync();
            await Expect(Panel(hall).Locator("[data-bar-item='newest']")).ToBeVisibleAsync();
            var z = await Debug(hall, "cameraZ");
            await hall.WaitForTimeoutAsync(300);
            Assert.Equal(z, await Debug(hall, "cameraZ"));   // bez animacji: kamera już na miejscu
            Assert.True(z < -4);
        }

        // Mobile: pasek stref zawsze pełny (także po wyborze strefy), przyklejony u dołu sali, przyciski ≥ 44 px bez
        // nachodzenia; zapowiedź "Wkrótce" w całości nad paskiem; bar otwiera się też stuknięciem obok lady (hojna strefa
        // trafienia); panel feedu nie szerszy niż ekran; bez poziomego przewijania.
        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_HelperBarAlwaysFull_NoteAndPanelFitViewport(int width, int height)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var hall = (await page.Locator(".saloon-hall").BoundingBoxAsync())!;
            var nav = (await Helper(page).BoundingBoxAsync())!;
            Assert.True(nav.X >= 0 && nav.X + nav.Width <= width && nav.Y + nav.Height <= hall.Y + hall.Height + 0.5, "Pasek w sali, w viewport.");
            Assert.True(nav.Y > hall.Y + hall.Height * 0.6, "Pasek u dołu sali — scena nad nim zostaje odsłonięta.");
            foreach (var zone in AllZones)
            {
                var box = (await Zone(page, zone).BoundingBoxAsync())!;
                Assert.True(box.Height >= 44 && box.Width >= 44, $"Strefa {zone}: cel dotyku {box.Width}x{box.Height}.");
            }
            await AssertNoOverlapAsync(page);
            Assert.True(await Debug(page, "cameraZ") < -2, "Na wąskim ekranie kamera bliżej baru.");
            await Expect(page.Locator(".app-header")).ToBeVisibleAsync();   // na telefonie nagłówek i dolna nawigacja zostają
            await Expect(page.Locator(".hall-corner")).ToBeHiddenAsync();
            await Ui.AssertNoHorizontalOverflowAsync(page, $"sala {width}");

            // Stół gry na telefonie: pełnoszeroki arkusz, pasek stref zostaje pełny.
            await Zone(page, "game").ClickAsync();
            await Expect(page.Locator("dialog[open].game-panel")).ToBeVisibleAsync();
            await page.Locator("dialog[open]").EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
            var sheet = (await page.Locator("dialog[open].game-panel").BoundingBoxAsync())!;
            Assert.True(sheet.X >= 0 && sheet.X + sheet.Width <= width + 0.5, "Arkusz stołu gry w szerokości ekranu.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"stół gry {width}");
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForFunctionAsync("() => !document.querySelector('dialog[open]') && window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            await Expect(Caption(page)).ToHaveCountAsync(0);
            foreach (var z in AllZones) Assert.True((await Zone(page, z).BoundingBoxAsync())!.Height >= 44, "Pasek na mobile zostaje pełny.");

            await ClickZoneInSceneAsync(page, "bar", dx: 30, dy: 40);
            await Expect(Panel(page).Locator("[data-bar-item='newest']")).ToBeVisibleAsync();
            await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
            var panel = await Panel(page).BoundingBoxAsync();
            Assert.True(panel!.X >= 0 && panel.X + panel.Width <= width + 0.5, $"Panel szerszy niż ekran: {panel.X} + {panel.Width}.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"panel baru {width}");
        }

        // ---- Cykl życia ------------------------------------------------------------------------------------------

        // Wyjście z /saloon (nawigacja Blazora) zwalnia salę; kolejne wejścia nie mnożą canvasów ani pętli.
        [Fact]
        public async Task LeaveAndReenter_DisposesHall_ExactlyOneCanvas()
        {
            await using var context = await _env.NewContextAsync(width: 1024, height: 700);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            for (var round = 0; round < 3; round++)
            {
                await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
                await Expect(page).ToHaveURLAsync(Ui.Path("/categories"));
                await page.WaitForFunctionAsync("() => window.__sansPostHall.disposed === true");
                Assert.Equal(0, await CanvasCount(page));
                var frames = await Debug(page, "frames");
                await page.WaitForTimeoutAsync(300);
                Assert.Equal(frames, await Debug(page, "frames"));

                await page.EvaluateAsync("() => Blazor.navigateTo('/saloon?scene=3d')");
                await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
                Assert.Equal(1, await CanvasCount(page));
            }
        }

        // ---- Visual fix: obrazy w ramach — bez wspólnej płaszczyzny z ramą, stabilne klatka po klatce ----------------------

        // Sonda (__sansPostHallProbe): płótna jednolitą magentą, prostokąty ich wnętrza w każdej klatce; bufor rysowania
        // zachowany, żeby odczyt pikseli po renderze na żądanie był wiarygodny. Każdy piksel wnętrza płótna, który nie jest
        // magentą, to rama albo ściana przebita przez obraz (z-fighting). Przed poprawką: 76–100% wnętrza w każdej klatce.
        private const string PaintingProbe = @"window.__sansPostHallProbe = true;
            (() => { const get = HTMLCanvasElement.prototype.getContext;
              HTMLCanvasElement.prototype.getContext = function (type, attributes) {
                if (/webgl/i.test(type)) attributes = { ...(attributes || {}), preserveDrawingBuffer: true };
                return get.call(this, type, attributes); }; })();";

        private const string PaintingSampler = @"() => {
            window.__pic = { sampled: 0, worst: 0, worstAt: null };
            let last = -1;
            const canvas = document.querySelector('.saloon-hall canvas'), gl = canvas.getContext('webgl2');
            const tick = () => {
                const d = window.__sansPostHall;
                if (window.__pic.stop) return;
                if (d.frames !== last && d.paintingRects) {
                    last = d.frames;
                    const r = canvas.getBoundingClientRect(), sx = canvas.width / r.width, sy = canvas.height / r.height;
                    d.paintingRects.forEach((p, i) => {
                        if (!p) return;
                        const x0 = Math.max(0, Math.round((p.x0 - r.left) * sx)), x1 = Math.min(canvas.width, Math.round((p.x1 - r.left) * sx));
                        const y0 = Math.max(0, Math.round((p.y0 - r.top) * sy)), y1 = Math.min(canvas.height, Math.round((p.y1 - r.top) * sy));
                        const w = x1 - x0, h = y1 - y0;
                        if (w < 6 || h < 6) return;
                        const px = new Uint8Array(w * h * 4);
                        gl.readPixels(x0, canvas.height - y1, w, h, gl.RGBA, gl.UNSIGNED_BYTE, px);
                        // Tylko piksele wewnątrz rzutowanego czworokąta płótna (przy widoku z ukosa prostokąt otaczający
                        // obejmuje też ścianę obok obrazu — to nie jest z-fighting). Czworokąt wypukły: ten sam znak iloczynów.
                        const quad = p.poly.map(([qx, qy]) => [(qx - r.left) * sx, (qy - r.top) * sy]);
                        const inside = (x, y) => {
                            let sign = 0;
                            for (let k = 0; k < 4; k++) {
                                const [ax, ay] = quad[k], [bx, by] = quad[(k + 1) % 4];
                                const cross = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
                                if (cross !== 0) { if (sign === 0) sign = Math.sign(cross); else if (Math.sign(cross) !== sign) return false; }
                            }
                            return true;
                        };
                        let off = 0, count = 0;
                        for (let row = 0; row < h; row++) {
                            const y = canvas.height - (canvas.height - y1 + row) - 0.5;   // readPixels: od dołu
                            for (let col = 0; col < w; col++) {
                                if (!inside(x0 + col + 0.5, y)) continue;
                                const k = (row * w + col) * 4;
                                count++;
                                if (!(px[k] > 200 && px[k + 1] < 70 && px[k + 2] > 200)) off++;
                            }
                        }
                        if (count < 36) return;
                        window.__pic.sampled++;
                        if (off / count > window.__pic.worst) { window.__pic.worst = off / count; window.__pic.worstAt = { painting: i, area: d.area, moving: d.moving, w, h, count, off }; }
                    });
                }
                requestAnimationFrame(tick);
            };
            requestAnimationFrame(tick);
        }";

        [Theory]
        [InlineData(ColorScheme.Light)]
        [InlineData(ColorScheme.Dark)]
        public async Task Paintings_OwnDepthLayer_StableFrameByFrameThroughCameraMoves(ColorScheme scheme)
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800, colorScheme: scheme);
            await context.AddInitScriptAsync(CapableGpu);
            await context.AddInitScriptAsync(PaintingProbe);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            // Geometria: rama ma własną głębokość od ściany, płótno wyraźnie przed frontem ramy; płótno nieprzezroczyste,
            // bez cieni (dekoracja nie daje cienia, a cień na płaskiej teksturze dawał tylko artefakty).
            var paintings = await page.EvaluateAsync<System.Text.Json.JsonElement>("() => window.__sansPostHall.paintings");
            Assert.Equal(3, paintings.GetArrayLength());   // dwa obrazy + zdjęcie "Tak wyglądaliśmy…" (Sprint 24)
            foreach (var painting in paintings.EnumerateArray())
            {
                Assert.InRange(painting.GetProperty("canvasGap").GetDouble(), 0.01, 0.03);
                Assert.True(painting.GetProperty("frameDepth").GetDouble() > 0.01, $"Rama przed ścianą: {painting}");
                Assert.False(painting.GetProperty("transparent").GetBoolean());
                Assert.False(painting.GetProperty("castShadow").GetBoolean());
                Assert.False(painting.GetProperty("receiveShadow").GetBoolean());
            }

            // Klatka po klatce: kadr główny, przejazdy do BAR / Wanted / Kącika muzycznego i powroty.
            await page.EvaluateAsync(PaintingSampler);
            foreach (var zone in new[] { "bar", "wanted", "music" })
            {
                await Zone(page, zone).ClickAsync();
                await Expect(Panel(page)).ToBeVisibleAsync();
                await WaitCameraStillAsync(page);
                await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
                await Expect(Panel(page)).ToHaveCountAsync(0);
                await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            }
            var result = await page.EvaluateAsync<System.Text.Json.JsonElement>("() => { window.__pic.stop = true; return window.__pic; }");

            Assert.True(result.GetProperty("sampled").GetInt32() >= 5, $"Za mało klatek z obrazem w kadrze: {result}");
            // Margines na drobne przesłonięcie przez kinkiet wiszący przy obrazie w ukośnych kadrach (~1–3%) — z-fighting
            // zajmował 76–100% wnętrza płótna.
            Assert.True(result.GetProperty("worst").GetDouble() < 0.05, $"Obraz przebity przez ramę/ścianę: {result}");
        }
    }
}
