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
        private static Task<bool> FocusOnZoneButton(IPage page) => page.EvaluateAsync<bool>("() => !!document.activeElement?.closest('.hall-zone')");
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

        // Klik w bar na canvas (raycasting) — kamera podchodzi do baru, otwiera się istniejący feed w panelu HTML.
        // Escape: panel znika, kamera wraca do kadru głównego.
        [Fact]
        public async Task BarCanvasClick_OpensFeedPanel_EscapeClosesAndResetsCamera()
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await Debug(page, "cameraZ");

            await ClickZoneInSceneAsync(page, "bar");

            await Expect(Panel(page).GetByRole(AriaRole.Heading, new() { Name = "Bar — rozmowy" })).ToBeVisibleAsync();
            await Expect(Panel(page).Locator(".post-card").First).ToBeVisibleAsync();
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
            await Expect(corner.GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToHaveAttributeAsync("href", "/login?returnUrl=%2Fsaloon");
            await Expect(corner.GetByRole(AriaRole.Link)).ToHaveCountAsync(2);   // tylko wyjście i logowanie
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

            await Zone(page, "music").ClickAsync();
            await Expect(page.Locator("#hall-note .hall-note")).ToBeVisibleAsync();
            await Expect(Helper(page)).ToHaveClassAsync(Compact);
            await Expect(Caption(page)).ToHaveCountAsync(0);
            Assert.Equal("1", await page.EvaluateAsync<string>("() => sessionStorage.getItem('sp-hall-onboarded')"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("#hall-note .hall-note")).ToHaveCountAsync(0);

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

        // Obiekt w sali (canvas) i przycisk paska prowadzą do tej samej ścieżki: kamera podchodzi, "Wkrótce", przycisk
        // strefy aria-expanded. Po kliknięciu w scenę fokus nie jest przerzucany na pasek (zostaje w sali).
        [Theory]
        [InlineData("wanted", "Tablica Wanted")]
        [InlineData("game", "Stół gry")]
        [InlineData("music", "Kącik muzyczny")]
        public async Task PlaceholderZones_FromSceneObject_SameFlowAsHelperBar(string zone, string title)
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await Debug(page, "cameraZ");

            // Wskazanie myszą podświetla strefę w sali i odpowiadający jej przycisk paska.
            var point = await page.EvaluateAsync<double[]>($"() => window.__sansPostHall.points.{zone}");
            var box = (await page.Locator(".saloon-hall canvas").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync((float)(box.X + point[0]), (float)(box.Y + point[1]));
            await Expect(Helper(page)).ToHaveAttributeAsync("data-hover", zone);

            await ClickZoneInSceneAsync(page, zone);
            var note = page.Locator("#hall-note .hall-note");
            await Expect(note.GetByRole(AriaRole.Heading, new() { Name = title })).ToBeVisibleAsync();
            await Expect(Zone(page, zone)).ToHaveAttributeAsync("aria-expanded", "true");
            Assert.Equal(zone, await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            await WaitCameraStillAsync(page);
            Assert.NotEqual(mainZ, await Debug(page, "cameraZ"), 1);
            await Expect(Helper(page)).ToHaveClassAsync(Compact);

            await note.GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
            await Expect(note).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
            Assert.False(await FocusOnZoneButton(page), "Po otwarciu ze sceny fokus nie skacze na pasek stref.");
            await WaitCameraStillAsync(page);
            Assert.Equal(mainZ, await Debug(page, "cameraZ"), 2);
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
            await Expect(page.Locator("#hall-note .hall-note")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("#hall-note .hall-note")).ToHaveCountAsync(0);
            await Expect(Zone(page, "game")).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // Tylko klawiatura: Tab do strefy BAR, Enter → panel z feedem (fokus w panelu), Shift+Tab w panelu zostaje
        // w oknie, "Zamknij" → fokus wraca na BAR. Sortowanie w panelu działa (istniejący feed).
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

            await Expect(Panel(page).Locator(".post-card").First).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('dialog[open]')"), "Fokus w panelu.");
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('dialog[open]')"), "Shift+Tab nie wychodzi z panelu.");

            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?sort=popular"));
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne" })).ToHaveAttributeAsync("aria-pressed", "true");

            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(bar).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // ---- WANTED / GAME / MUSIC ---------------------------------------------------------------------------

        [Theory]
        [InlineData("wanted", "Tablica Wanted")]
        [InlineData("game", "Stół gry")]
        [InlineData("music", "Kącik muzyczny")]
        public async Task PlaceholderZones_ShowComingSoon_ThenBackToHall(string zone, string title)
        {
            await using var context = await _env.NewContextAsync(width: 1280, height: 800);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await Debug(page, "cameraZ");

            await Zone(page, zone).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            var note = page.Locator("#hall-note .hall-note");
            await Expect(note).ToBeVisibleAsync();
            await WaitCameraStillAsync(page);
            Assert.NotEqual(mainZ, await Debug(page, "cameraZ"), 1);   // kamera podeszła do strefy
            await Expect(note.GetByRole(AriaRole.Heading, new() { Name = title })).ToBeVisibleAsync();
            await Expect(note).ToContainTextAsync("Wkrótce");
            await Expect(Zone(page, zone)).ToHaveAttributeAsync("aria-expanded", "true");
            Assert.Equal(zone, await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);

            await note.GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
            await Expect(note).ToHaveCountAsync(0);
            await Expect(Zone(page, zone)).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
            await WaitCameraStillAsync(page);
            Assert.Equal(mainZ, await Debug(page, "cameraZ"), 2);

            // Escape też zamyka zapowiedź.
            await page.Keyboard.PressAsync("Enter");
            await Expect(note).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(note).ToHaveCountAsync(0);
        }

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
            await Expect(Panel(hall).Locator(".post-card").First).ToBeVisibleAsync();
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

            foreach (var zone in new[] { "wanted", "game", "music" })
            {
                await Zone(page, zone).ClickAsync();
                var note = page.Locator("#hall-note .hall-note");
                await Expect(note).ToBeVisibleAsync();
                var noteBox = (await note.BoundingBoxAsync())!;
                nav = (await Helper(page).BoundingBoxAsync())!;
                Assert.True(noteBox.X >= 0 && noteBox.X + noteBox.Width <= width && noteBox.Y >= hall.Y, $"Zapowiedź {zone} poza ekranem.");
                Assert.True(noteBox.Y + noteBox.Height <= nav.Y, $"Zapowiedź {zone} zasłania pasek stref.");
                await Expect(Caption(page)).ToHaveCountAsync(0);
                foreach (var z in AllZones) Assert.True((await Zone(page, z).BoundingBoxAsync())!.Height >= 44, "Pasek na mobile zostaje pełny.");
                await Ui.AssertNoHorizontalOverflowAsync(page, $"{zone} {width}");
                await note.GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
                await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
                await WaitCameraStillAsync(page);
            }

            await ClickZoneInSceneAsync(page, "bar", dx: 30, dy: 40);
            await Expect(Panel(page).Locator(".post-card").First).ToBeVisibleAsync();
            await Panel(page).EvaluateAsync("d => Promise.all(d.getAnimations({ subtree: true }).map(a => a.finished))");
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
    }
}
