using System.Net.Http.Json;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 18 — Kącik muzyczny (radio country). Deterministycznie, bez publicznego internetu: lista stacji z atrapy
    // Radio Browser (E2EEnvironment.Radio), strumienie stacji obsługuje Playwright (krótki, cichy WAV; "Broken Trail FM"
    // → 404). Przepływ: sala → Kącik (kamera, okno) → Graj → radio gra dalej w sali, w BAR i na tablicy Wanted.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Music")]
    public class MusicCornerE2ETests
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

        private static readonly FakeRadioBrowser.Station First = FakeRadioBrowser.Stations[0];
        private static readonly FakeRadioBrowser.Station Second = FakeRadioBrowser.Stations[1];
        private static readonly FakeRadioBrowser.Station Broken = FakeRadioBrowser.Stations[2];

        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public MusicCornerE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].music-panel");
        private static ILocator MusicButton(IPage page) => page.Locator(".hall-zone[data-zone='music']");
        private static ILocator Toggle(IPage page) => page.Locator("dialog[open] [data-music-toggle]");
        private static ILocator StationButton(IPage page, FakeRadioBrowser.Station station) =>
            page.Locator("dialog[open] .music-station").Filter(new() { HasText = station.Name });
        private static ILocator Status(IPage page) => page.Locator("dialog[open] .music-status");
        private static ILocator Mini(IPage page) => page.Locator(".mini-player");

        private static Task<string> RadioStatus(IPage page) => page.EvaluateAsync<string>("() => window.__sansPostMusic.status");
        private static Task WaitRadioAsync(IPage page, string status) =>
            page.WaitForFunctionAsync($"() => window.__sansPostMusic.status === '{status}'");
        private static Task<string?> Src(IPage page) => page.EvaluateAsync<string?>("() => document.getElementById('sp-radio')?.getAttribute('src') ?? null");
        private static Task<int> AudioElements(IPage page) => page.EvaluateAsync<int>("() => document.querySelectorAll('audio').length");
        private static Task WaitCameraStillAsync(IPage page) => page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

        private async Task<(IBrowserContext Context, FakeRadio.StreamLog Streams)> NewContextAsync(int width = 1280, int height = 800,
            SansPostServer? server = null, ColorScheme scheme = ColorScheme.Light)
        {
            var context = await _env.NewContextAsync(server, width, height, scheme);
            await context.AddInitScriptAsync(CapableGpu);
            return (context, await FakeRadio.RouteStreamsAsync(context));
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await WaitCameraStillAsync(page);
        }

        private static async Task OpenMusicAsync(IPage page)
        {
            await MusicButton(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?music=radio"));
            await Expect(Toggle(page)).ToBeFocusedAsync();
        }

        private static async Task CloseToHallAsync(IPage page)
        {
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
        }

        private static async Task PlayAsync(IPage page)
        {
            await Toggle(page).ClickAsync();
            await WaitRadioAsync(page, "playing");
            await Expect(Status(page)).ToHaveTextAsync("Radio gra");
        }

        // ---- 1, 2, 10. Pasek stref → kamera przy pianinie → okno "Muzyka w Saloonie"; lista stacji; nic nie gra samo ----

        [Fact]
        public async Task HelperDock_OpensMusicCorner_StationsListed_NothingAutoplays()
        {
            var (context, streams) = await NewContextAsync(1440, 900);
            await using var _ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");

            await Expect(MusicButton(page)).ToHaveAttributeAsync("aria-haspopup", "dialog");
            await OpenMusicAsync(page);
            await Expect(Panel(page).Locator(".dialog-title")).ToHaveTextAsync("Muzyka w Saloonie");
            Assert.Equal("music", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            await WaitCameraStillAsync(page);
            Assert.NotEqual(mainZ, await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ"), 1);

            // Lista z serwera (atrapa Radio Browser): trzy stacje, pierwsza wybrana; metadane bez udawanych tytułów utworów.
            await Expect(Panel(page).Locator(".music-station")).ToHaveCountAsync(3);
            await Expect(Panel(page).Locator(".music-station-title")).ToHaveTextAsync(FakeRadioBrowser.Stations.Select(s => s.Name).ToArray());
            await Expect(Panel(page).Locator("#music-station-name")).ToHaveTextAsync(First.Name);
            await Expect(Panel(page).Locator(".music-station-meta")).ToContainTextAsync("MP3 · 128 kbps");
            await Expect(Panel(page).Locator(".music-station-meta")).ToContainTextAsync(First.Country);
            var homepage = Panel(page).Locator(".music-homepage");
            await Expect(homepage).ToHaveAttributeAsync("href", First.Homepage);
            await Expect(homepage).ToHaveAttributeAsync("target", "_blank");
            await Expect(homepage).ToHaveAttributeAsync("rel", "noopener noreferrer");
            await Expect(page.Locator("dialog[open] iframe")).ToHaveCountAsync(0);

            // Brak autoplay: bez "Graj" nie powstaje element audio i nie ma żadnego pobrania strumienia.
            await Expect(Status(page)).ToHaveTextAsync("Radio zatrzymane");
            await Expect(Toggle(page)).ToContainTextAsync("Graj");
            await Expect(Panel(page).Locator(".music-live")).ToHaveCountAsync(0);
            await page.WaitForTimeoutAsync(500);
            Assert.Equal(0, await AudioElements(page));
            Assert.Equal("idle", await RadioStatus(page));
            Assert.Equal(0, streams[First.Stream[FakeRadio.StreamHost.Length..]]);
            await Expect(Mini(page)).ToHaveCountAsync(0);   // mini-player dopiero po uruchomieniu radia

            // Desktop: mała karta z lewej, kącik z pianinem i gramofonem po prawej zostaje odsłonięty.
            var panel = (await Panel(page).BoundingBoxAsync())!;
            Assert.True(panel.X < 100 && panel.Width <= 440, $"Karta Kącika z lewej: x {panel.X}, w {panel.Width}.");
        }

        // Obiekt w scenie (pianino / gramofon) — ta sama ścieżka co pasek stref.
        [Fact]
        public async Task PianoInScene_Click_OpensSameMusicFlow()
        {
            var (context, _) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var point = await page.EvaluateAsync<double[]>("() => window.__sansPostHall.points.music");
            var canvas = (await page.Locator(".saloon-hall canvas").BoundingBoxAsync())!;
            await page.Mouse.ClickAsync((float)(canvas.X + point[0]), (float)(canvas.Y + point[1]));

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?music=radio"));
            await Expect(Panel(page)).ToBeVisibleAsync();
            await CloseToHallAsync(page);
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?scene=3d"));
        }

        // ---- 2, 3. API: tylko pola UI, druga odpowiedź z pamięci podręcznej (bez zapytania do Radio Browser) ----------

        [Fact]
        public async Task StationsApi_ReturnsUiFields_AndServesFromCache()
        {
            using var http = _env.Main.CreateApiClient();
            var first = await http.GetAsync("/api/music/stations");
            var afterFirst = _env.Radio.Requests;
            var second = await http.GetAsync("/api/music/stations");

            Assert.True(first.IsSuccessStatusCode);
            Assert.Equal(afterFirst, _env.Radio.Requests);   // cache hit
            Assert.StartsWith("SansPost/", _env.Radio.LastUserAgent);
            using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            var stations = json.RootElement.GetProperty("stations");
            Assert.Equal(3, stations.GetArrayLength());
            Assert.Equal(new[] { "bitrate", "codec", "country", "homepage", "name", "stationId", "streamUrl" },
                stations[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n));
            Assert.Equal(First.Stream, stations[0].GetProperty("streamUrl").GetString());
        }

        // ---- 4. Radio Browser niedostępny: okno otwiera się, komunikat, bez wyjątku i bez nieskończonego ładowania ----

        [Fact]
        public async Task RadioBrowserDown_CornerStillOpens_WithFriendlyMessage()
        {
            var server = await _env.StartServerAsync("radio-down", new Dictionary<string, string?> { ["Music__RadioBrowserServers"] = "http://127.0.0.1:9" });
            var (context, _) = await NewContextAsync(server: server);
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await MusicButton(page).ClickAsync();
            await Expect(Panel(page)).ToBeVisibleAsync();
            await Expect(Panel(page).Locator(".music-unavailable-title")).ToHaveTextAsync("Radio jest chwilowo niedostępne.");
            await Expect(Panel(page).Locator(".music-loading")).ToHaveCountAsync(0);
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Spróbuj ponownie" })).ToBeVisibleAsync();

            using var http = server.CreateApiClient();
            var api = await http.GetAsync("/api/music/stations");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, api.StatusCode);

            await CloseToHallAsync(page);
            await Expect(MusicButton(page)).ToBeFocusedAsync();
        }

        // ---- 5, 6, 7, 16. Graj / Pauza / głośność; gramofon kręci się tylko przy grającym radiu ---------------------

        [Fact]
        public async Task PlayPauseVolume_AndGramophoneSpinsOnlyWhilePlaying()
        {
            var (context, streams) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenMusicAsync(page);
            await WaitCameraStillAsync(page);
            Assert.False(await page.EvaluateAsync<bool>("() => !!window.__sansPostHall.spinning"));

            await PlayAsync(page);
            Assert.Equal(First.Stream, await Src(page));
            Assert.True(streams["saloon-country.mp3"] > 0, "Strumień pobrany dopiero po Graj.");
            await Expect(Panel(page).Locator(".music-live")).ToHaveTextAsync("LIVE");
            await Expect(Toggle(page)).ToContainTextAsync("Pauza");
            await page.WaitForFunctionAsync("() => window.__sansPostHall.spinning === true");
            var angle = await page.EvaluateAsync<double>("() => window.__sansPostHall.recordAngle ?? 0");
            await page.WaitForFunctionAsync($"a => Math.abs((window.__sansPostHall.recordAngle ?? 0) - a) > 0.2", angle);

            // Głośność: prawdziwy suwak z etykietą; klawiatura zmienia głośność elementu audio.
            var volume = Panel(page).GetByLabel("Głośność");
            await Expect(volume).ToHaveAttributeAsync("type", "range");
            await volume.FocusAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            await page.Keyboard.PressAsync("ArrowLeft");
            await page.WaitForFunctionAsync("() => Math.abs(document.getElementById('sp-radio').volume - 0.6) < 0.001");
            await Expect(volume).ToHaveValueAsync("60");

            // Pauza: strumień zatrzymany (bez pobierania w tle), gramofon stoi, scena nie rysuje klatek.
            await Toggle(page).ClickAsync();
            await WaitRadioAsync(page, "paused");
            await Expect(Status(page)).ToHaveTextAsync("Radio zatrzymane");
            Assert.Null(await Src(page));
            await page.WaitForFunctionAsync("() => window.__sansPostHall.spinning === false");
            var frames = await page.EvaluateAsync<int>("() => window.__sansPostHall.frames");
            await page.WaitForTimeoutAsync(600);
            Assert.Equal(frames, await page.EvaluateAsync<int>("() => window.__sansPostHall.frames"));

            // Graj ponownie — ta sama stacja od nowa (radio na żywo), głośność zachowana.
            await PlayAsync(page);
            Assert.Equal(0.6, await page.EvaluateAsync<double>("() => document.getElementById('sp-radio').volume"), 3);
            Assert.Equal(1, await AudioElements(page));
        }

        // ---- 8. Zmiana stacji: w trakcie grania — od razu nowa; zatrzymane radio zostaje zatrzymane -----------------

        [Fact]
        public async Task StationChange_WhilePlayingSwitches_WhilePausedStaysSilent()
        {
            var (context, streams) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenMusicAsync(page);

            // Wybór stacji przy zatrzymanym radiu: tylko wybór, bez dźwięku i bez pobierania.
            await StationButton(page, Second).ClickAsync();
            await Expect(Panel(page).Locator("#music-station-name")).ToHaveTextAsync(Second.Name);
            await Expect(StationButton(page, Second)).ToHaveAttributeAsync("aria-current", "true");
            await page.WaitForTimeoutAsync(400);
            Assert.Equal("idle", await RadioStatus(page));
            Assert.Equal(0, streams["prairie.mp3"]);
            Assert.Equal(0, await AudioElements(page));

            await PlayAsync(page);
            Assert.Equal(Second.Stream, await Src(page));

            // W trakcie grania: "Następna stacja" — A zatrzymana, src podmienione, B gra.
            await StationButton(page, First).ClickAsync();
            await WaitRadioAsync(page, "playing");
            Assert.Equal(First.Stream, await Src(page));
            await Expect(Panel(page).Locator("#music-station-name")).ToHaveTextAsync(First.Name);
            await Panel(page).Locator(".music-next").ClickAsync();
            await page.WaitForFunctionAsync($"() => document.getElementById('sp-radio').getAttribute('src') === '{Second.Stream}'");
            await WaitRadioAsync(page, "playing");
            Assert.Equal(1, await AudioElements(page));

            // Pauza, potem zmiana stacji — nadal cisza (bez gestu "Graj" nic nie startuje).
            await Toggle(page).ClickAsync();
            await WaitRadioAsync(page, "paused");
            var before = streams["saloon-country.mp3"];
            await StationButton(page, First).ClickAsync();
            await page.WaitForTimeoutAsync(400);
            Assert.Equal("paused", await RadioStatus(page));
            Assert.Null(await Src(page));
            Assert.Equal(before, streams["saloon-country.mp3"]);
        }

        // ---- 9. Uszkodzony strumień: komunikat, bez pętli ponowień; "Spróbuj ponownie" i następna stacja ------------

        [Fact]
        public async Task BrokenStream_ShowsMessage_NoRetryLoop_RetryAndNextWork()
        {
            var (context, streams) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenMusicAsync(page);
            await PlayAsync(page);

            await StationButton(page, Broken).ClickAsync();
            await WaitRadioAsync(page, "error");
            await Expect(Status(page)).ToHaveTextAsync("Ta stacja jest chwilowo niedostępna.");
            await Expect(Toggle(page)).ToContainTextAsync("Spróbuj ponownie");
            Assert.False(await page.EvaluateAsync<bool>("() => window.__sansPostHall.spinning"));
            var attempts = streams["broken.mp3"];
            await page.WaitForTimeoutAsync(1500);
            Assert.Equal(attempts, streams["broken.mp3"]);   // bez automatycznych ponowień

            await Toggle(page).ClickAsync();                   // świadome ponowienie
            await page.WaitForFunctionAsync($"() => window.__sansPostMusic.playRequests >= 3");
            await WaitRadioAsync(page, "error");
            Assert.True(streams["broken.mp3"] > attempts);

            await StationButton(page, Second).ClickAsync();   // wybór innej stacji po błędzie gra od razu
            await WaitRadioAsync(page, "playing");
            Assert.Equal(Second.Stream, await Src(page));
        }

        // ---- 11, 12, 13, 14. Radio gra dalej: sala → BAR → Wanted → inna strona; jeden element audio; mini-player ----

        [Fact]
        public async Task RadioKeepsPlaying_AcrossHallBarWanted_MiniPlayerControlsIt()
        {
            var (context, _) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenMusicAsync(page);
            await PlayAsync(page);

            await CloseToHallAsync(page);
            Assert.Equal("playing", await RadioStatus(page));
            await Expect(Mini(page)).ToBeVisibleAsync();
            await Expect(Mini(page).Locator(".mini-player-name")).ToHaveTextAsync(First.Name);
            await Expect(Mini(page).Locator(".mini-player-live")).ToHaveTextAsync("LIVE");
            await Expect(Mini(page).GetByRole(AriaRole.Button, new() { Name = $"Pauza: {First.Name}" })).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sansPostHall.spinning"));

            await page.Locator(".hall-zone[data-zone='bar']").ClickAsync();
            await Expect(page.Locator("dialog[open] [data-bar-item='newest']")).ToBeFocusedAsync();
            Assert.Equal("playing", await RadioStatus(page));
            await Expect(Mini(page)).ToBeVisibleAsync();
            await CloseToHallAsync(page);

            await page.Locator(".hall-zone[data-zone='wanted']").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?wanted=board"));
            await Expect(page.Locator("dialog[open].wanted-panel")).ToBeVisibleAsync();
            Assert.Equal("playing", await RadioStatus(page));
            await CloseToHallAsync(page);

            // Zmiana strony w aplikacji (ten sam layout) — radio gra, mini-player zostaje; powrót do sali — gramofon się kręci.
            await page.EvaluateAsync("() => Blazor.navigateTo('/categories')");
            await Expect(page).ToHaveURLAsync(Ui.Path("/categories"));
            await page.WaitForFunctionAsync("() => window.__sansPostHall.disposed === true");
            Assert.Equal("playing", await RadioStatus(page));
            await Expect(Mini(page)).ToBeVisibleAsync();
            await page.EvaluateAsync("() => Blazor.navigateTo('/saloon?scene=3d')");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall.spinning === true");

            // Mini-player: pauza i głośność sterują tym samym elementem audio.
            await Mini(page).Locator(".mini-player-toggle").ClickAsync();
            await WaitRadioAsync(page, "paused");
            await Expect(Mini(page).GetByRole(AriaRole.Button, new() { Name = $"Graj: {First.Name}" })).ToBeVisibleAsync();
            var miniVolume = Mini(page).GetByLabel("Głośność radia");
            await miniVolume.FocusAsync();
            await page.Keyboard.PressAsync("End");
            await page.WaitForFunctionAsync("() => window.__sansPostMusic.status === 'paused' && document.getElementById('sp-radio').volume === 1");
            await Mini(page).Locator(".mini-player-toggle").ClickAsync();
            await WaitRadioAsync(page, "playing");
            Assert.Equal(1, await AudioElements(page));
        }

        // ---- 14, 15. Cykl życia: wiele wejść/wyjść i zmian stacji — jeden element audio, bez przyrostu nasłuchów ------

        [Fact]
        public async Task ManyVisits_OneAudioElement_NoListenerOrSubscriberLeak()
        {
            var (context, _) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenMusicAsync(page);
            await PlayAsync(page);
            await CloseToHallAsync(page);
            var baseline = await page.EvaluateAsync<string>("() => JSON.stringify({ a: window.__sansPostMusic.audioListeners, d: window.__sansPostMusic.documentListeners, s: window.__sansPostMusic.subscribers })");

            for (var i = 0; i < 8; i++)
            {
                await OpenMusicAsync(page);
                await StationButton(page, i % 2 == 0 ? Second : First).ClickAsync();
                await WaitRadioAsync(page, "playing");
                if (i % 3 == 0)
                {
                    await Toggle(page).ClickAsync();
                    await WaitRadioAsync(page, "paused");
                    await Toggle(page).ClickAsync();
                    await WaitRadioAsync(page, "playing");
                }
                Assert.Equal(2, await page.EvaluateAsync<int>("() => window.__sansPostMusic.subscribers"));   // mini-player + Kącik
                await CloseToHallAsync(page);
            }

            Assert.Equal(1, await AudioElements(page));
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__sansPostMusic.audioCreated"));
            Assert.Equal(baseline, await page.EvaluateAsync<string>("() => JSON.stringify({ a: window.__sansPostMusic.audioListeners, d: window.__sansPostMusic.documentListeners, s: window.__sansPostMusic.subscribers })"));
            Assert.Contains("\"s\":1", baseline);   // po zamknięciu Kącika — tylko mini-player
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sansPostHall.musicListener"));
            Assert.Equal("playing", await RadioStatus(page));
        }

        // ---- 17. Reduced motion: radio gra, gramofon stoi ------------------------------------------------------------

        [Fact]
        public async Task ReducedMotion_RadioPlays_GramophoneStill()
        {
            var (context, _) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await OpenMusicAsync(page);
            await PlayAsync(page);

            await page.WaitForTimeoutAsync(500);
            Assert.False(await page.EvaluateAsync<bool>("() => !!window.__sansPostHall.spinning"));
            var frames = await page.EvaluateAsync<int>("() => window.__sansPostHall.frames");
            await page.WaitForTimeoutAsync(600);
            Assert.Equal(frames, await page.EvaluateAsync<int>("() => window.__sansPostHall.frames"));
            Assert.Equal("playing", await RadioStatus(page));
        }

        // ---- 18. Mobile 390 / 360: pełnoszeroki arkusz, cele ≥ 44 px, mini-player bez kolizji z paskami ----------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_FullWidthSheet_MiniPlayerClearOfNavigation(int width, int height)
        {
            var (context, _) = await NewContextAsync(width, height);
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await MusicButton(page).ClickAsync();
            await Expect(Toggle(page)).ToBeFocusedAsync();
            await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
            var sheet = (await Panel(page).BoundingBoxAsync())!;
            Assert.True(Math.Abs(sheet.X) < 0.5 && Math.Abs(sheet.Width - width) < 0.5, $"Arkusz na całą szerokość: {sheet.X} + {sheet.Width}.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"Kącik muzyczny {width}");
            var small = await page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('dialog[open] button, dialog[open] a, dialog[open] input')].filter(e => e.getClientRects().length && e.getBoundingClientRect().height < 44).map(e => e.className || e.id)");
            Assert.True(small.Length == 0, "Cele dotyku < 44 px: " + string.Join(", ", small));

            await PlayAsync(page);
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await WaitCameraStillAsync(page);

            var mini = (await Mini(page).BoundingBoxAsync())!;
            var dock = (await page.Locator("nav.hall-zones").BoundingBoxAsync())!;
            var nav = (await page.Locator(".bottom-nav").BoundingBoxAsync())!;
            var header = (await page.Locator(".app-header").BoundingBoxAsync())!;
            Assert.True(mini.X >= 0 && mini.X + mini.Width <= width, $"Mini-player w szerokości ekranu ({mini.X} + {mini.Width}).");
            Assert.True(mini.Y >= header.Y + header.Height - 0.5, "Mini-player pod nagłówkiem.");
            Assert.True(mini.Y + mini.Height <= dock.Y, "Mini-player nie zasłania paska stref.");
            Assert.True(mini.Y + mini.Height <= nav.Y, "Mini-player nie zasłania dolnej nawigacji.");
            Assert.True((await Mini(page).Locator(".mini-player-toggle").BoundingBoxAsync())!.Height >= 44, "Przycisk mini-playera ≥ 44 px.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"mini-player {width}");
            Assert.Equal("playing", await RadioStatus(page));
            foreach (var zone in new[] { "bar", "wanted", "game", "music" })
                Assert.True((await page.Locator($".hall-zone[data-zone='{zone}']").BoundingBoxAsync())!.Height >= 44, "Pasek stref działa i ma pełne cele.");
        }

        // ---- 19. Klawiatura: Tab / Shift+Tab / Enter / Spacja / Escape; stan radia dla czytnika ----------------------

        [Fact]
        public async Task Keyboard_TabEnterSpaceEscape_FocusReturns_StatusAnnounced()
        {
            var (context, _) = await NewContextAsync();
            await using var __ = context;
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await page.Locator(".hall-zone[data-zone='game']").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(MusicButton(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Toggle(page)).ToBeFocusedAsync();
            await Expect(Toggle(page)).ToHaveAccessibleNameAsync($"Graj: {First.Name}");
            await Expect(Status(page)).ToHaveAttributeAsync("role", "status");

            await page.Keyboard.PressAsync("Space");
            await WaitRadioAsync(page, "playing");
            await Expect(Status(page)).ToHaveTextAsync("Radio gra");
            await Expect(Toggle(page)).ToHaveAccessibleNameAsync($"Pauza: {First.Name}");

            // Tab: następna stacja → głośność → lista stacji; Shift+Tab wraca; fokus nie wychodzi do sali.
            await page.Keyboard.PressAsync("Tab");
            await Expect(Panel(page).Locator(".music-next")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Panel(page).GetByLabel("Głośność")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await page.Keyboard.PressAsync("Tab");
            await Expect(StationButton(page, First)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Expect(Panel(page).GetByLabel("Głośność")).ToBeFocusedAsync();
            for (var i = 0; i < 8; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }"),
                    $"Tab #{i + 1} trafił do sali pod oknem.");
            }

            // Enter na stacji zmienia ją w trakcie grania; Escape zamyka okno — radio gra, fokus na "Kącik muzyczny".
            await StationButton(page, Second).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync($"() => document.getElementById('sp-radio').getAttribute('src') === '{Second.Stream}'");
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(MusicButton(page)).ToBeFocusedAsync();
            await WaitRadioAsync(page, "playing");

            // Mini-player z klawiatury: Enter na Pauzie.
            await Mini(page).Locator(".mini-player-toggle").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await WaitRadioAsync(page, "paused");
        }

        // ---- 20. axe: Kącik (zatrzymany, grający, błąd stacji) i mini-player — 0 critical / 0 serious ----------------

        [Fact]
        public async Task Axe_MusicCornerAndMiniPlayer_NoCriticalOrSerious()
        {
            var blocking = new List<string>();
            foreach (var (scheme, width, height) in new[] { (ColorScheme.Light, 1280, 800), (ColorScheme.Dark, 1280, 800), (ColorScheme.Light, 390, 844) })
            {
                var (context, _) = await NewContextAsync(width, height, scheme: scheme);
                await using var __ = context;
                var page = await context.NewPageAsync();
                await OpenHallAsync(page);

                async Task AuditAsync(string name)
                {
                    await page.EvaluateAsync("() => Promise.allSettled(document.getAnimations().map(a => a.finished))");
                    var result = await page.RunAxe();
                    foreach (var violation in result.Violations)
                    {
                        var line = $"{name}: [{violation.Impact}] {violation.Id} — {violation.Help} ({string.Join(" | ", violation.Nodes.Take(3).Select(n => string.Join(" ", n.Target)))})";
                        _output.WriteLine(line);
                        if (violation.Impact is "critical" or "serious")
                            blocking.Add(line);
                    }
                }

                await OpenMusicAsync(page);
                await AuditAsync($"Music {scheme} {width} — zatrzymane");
                await PlayAsync(page);
                await AuditAsync($"Music {scheme} {width} — gra");
                await StationButton(page, Broken).ClickAsync();
                await WaitRadioAsync(page, "error");
                await AuditAsync($"Music {scheme} {width} — błąd stacji");
                await StationButton(page, First).ClickAsync();
                await WaitRadioAsync(page, "playing");
                await CloseToHallAsync(page);
                await Expect(Mini(page)).ToBeVisibleAsync();
                await AuditAsync($"Main Hall + mini-player {scheme} {width}");
            }

            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));
        }
    }
}
