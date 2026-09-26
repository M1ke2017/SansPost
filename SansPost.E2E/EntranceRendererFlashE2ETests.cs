using System.Text.Json;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 14D-FIX — Entrance ma trzy stany renderera: Pending → ThreeD albo Pending → CssFallback.
    // Stara scena CSS/SVG to fallback, nie ekran ładowania: nie może się pokazać ani na jedną klatkę przed 3D.
    // Każda narysowana klatka (requestAnimationFrame od startu dokumentu) jest próbkowana: widoczność sceny CSS, warstwy
    // Pending, tabliczek, stan renderera, obecność canvas i wysokość dokumentu (brak skoku układu / scrollbara).
    // Chromium headless ma tylko WebGL programowy (SwiftShader): "dobry GPU" symuluje skrypt podmieniający nazwę renderera
    // i zdejmujący failIfMajorPerformanceCaveat — decyzja przechodzi wtedy tą samą ścieżką co na prawdziwym GPU.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Entrance3d")]
    public class EntranceRendererFlashE2ETests
    {
        private const string Sampler = @"(() => {
            const f = window.__flash = { samples: [] };
            const vis = el => { if (!el) return false; const c = getComputedStyle(el); return c.visibility !== 'hidden' && c.display !== 'none' && parseFloat(c.opacity) > 0.02; };
            const sample = t => {
                const e = document.querySelector('.entrance');
                if (e) f.samples.push({
                    t: Math.round(t),
                    css: vis(e.querySelector('.entrance-stage')) && vis(e.querySelector('.saloon')),
                    pending: vis(e.querySelector('.entrance-pending')),
                    plaques: vis(e.querySelector('.entrance-boardwalk')) && vis(e.querySelector('.entrance-sign')),
                    renderer: e.dataset.renderer || '',
                    canvas: !!e.querySelector('.entrance-3d canvas'),
                    height: document.documentElement.scrollHeight
                });
                if (!f.stop) requestAnimationFrame(sample);
            };
            requestAnimationFrame(sample);
        })()";

        // Sprzętowy renderer (jak Iris Xe) zamiast SwiftShadera — tylko dla decyzji; rysuje nadal SwiftShader.
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
        })()";

        // SwiftShader nie przejdzie próby płynności przy 1280 px — wynik próby "ok" w tej sesji (jak na dobrym GPU).
        private const string ProbePassed = "(() => { try { sessionStorage.setItem('sp-scene-probe', 'ok'); } catch { } })()";

        private const string NoWebGl = @"(() => {
            const original = HTMLCanvasElement.prototype.getContext;
            HTMLCanvasElement.prototype.getContext = function (type, ...rest) {
                return /webgl/i.test(type) ? null : original.call(this, type, ...rest);
            };
        })()";

        private readonly E2EEnvironment _env;

        public EntranceRendererFlashE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private sealed record Sample(int T, bool Css, bool Pending, bool Plaques, string Renderer, bool Canvas, int Height);

        private static async Task<IBrowserContext> ContextAsync(E2EEnvironment env, int width = 1280, int height = 800, bool reducedMotion = false, params string[] scripts)
        {
            var context = await env.NewContextAsync(width: width, height: height, reducedMotion: reducedMotion);
            foreach (var script in scripts.Prepend(Sampler))
                await context.AddInitScriptAsync(script);
            return context;
        }

        // Wejście na "/" bez czekania na decyzję (Ui.GotoAsync czeka) — próbki od pierwszej klatki do końca przejścia.
        private static async Task<List<Sample>> TraceAsync(IPage page, string path = "/")
        {
            await page.GotoAsync(path);
            await page.WaitForFunctionAsync("() => { const e = document.querySelector('.entrance'); return e && e.dataset.renderer !== 'pending' && e.dataset.rendererReason !== 'pending'; }");
            await page.WaitForTimeoutAsync(500);   // koniec przenikania warstwy Pending (320 ms)
            var json = await page.EvaluateAsync<string>("() => { window.__flash.stop = true; return JSON.stringify(window.__flash.samples); }");
            var samples = JsonSerializer.Deserialize<List<Sample>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.NotEmpty(samples);
            return samples;
        }

        private static string Sequence(IEnumerable<Sample> samples) =>
            string.Join(" → ", samples.Select(s => s.Renderer).Where((r, i) => i == 0 || r != samples.ElementAt(i - 1).Renderer));

        // Wspólne warunki "bez migania": scena CSS widoczna tylko w stanie css, tabliczki nigdy w Pending, stała wysokość.
        private static void AssertNoFlash(List<Sample> samples, string context)
        {
            var cssTooEarly = samples.Where(s => s.Css && s.Renderer != "css").ToList();
            Assert.True(cssTooEarly.Count == 0, $"{context}: stara scena CSS widoczna w {cssTooEarly.Count} klatkach przed decyzją fallback (pierwsza: {cssTooEarly.FirstOrDefault()}).");
            Assert.DoesNotContain(samples, s => s.Plaques && s.Renderer == "pending");
            Assert.Single(samples.Select(s => s.Height).Distinct());
        }

        private static async Task<string> ServerHtmlAsync(IBrowserContext context, string path = "/")
        {
            var response = await context.APIRequest.GetAsync(path);
            return await response.TextAsync();
        }

        private static async Task<string?> SceneCookieAsync(IBrowserContext context) =>
            (await context.CookiesAsync()).FirstOrDefault(c => c.Name == "sp-scene")?.Value;

        // A. Pierwsza wizyta + dobry GPU: serwer nie zna decyzji → Pending → 3D; stara scena ani razu, preferencja zapisana.
        [Fact]
        public async Task FirstVisit_CapableGpu_PendingThenThreeD_OldEntranceNeverVisible()
        {
            await using var context = await ContextAsync(_env, scripts: new[] { CapableGpu, ProbePassed });
            var html = await ServerHtmlAsync(context);
            Assert.Contains("data-renderer=\"pending\"", html);
            Assert.DoesNotContain("has-3d", html);

            var page = await context.NewPageAsync();
            var samples = await TraceAsync(page);

            Assert.Equal("pending → 3d", Sequence(samples));
            AssertNoFlash(samples, "Pierwsza wizyta");
            Assert.Contains(samples, s => s.Pending);
            Assert.DoesNotContain(samples, s => s.Css);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", "gpu");
            await Expect(page.Locator(".entrance-pending")).Not.ToBeVisibleAsync();
            await Expect(page.Locator("nav.entrance-signs").GetByRole(AriaRole.Button, new() { Name = "Wejdź jako gość" })).ToBeVisibleAsync();
            Assert.Equal("3d", await SceneCookieAsync(context));
        }

        // B. Zapisana preferencja 3D: prerender od razu w Pending (z preloadem Three.js), bez próby płynności, bez sceny CSS.
        [Fact]
        public async Task SavedThreeDPreference_NoCssFlash_NoProbe()
        {
            await using var context = await ContextAsync(_env, scripts: CapableGpu);
            await context.AddCookiesAsync(new[] { new Cookie { Name = "sp-scene", Value = "3d", Url = _env.Main.BaseUrl } });
            var html = await ServerHtmlAsync(context);
            Assert.Contains("data-renderer=\"pending\"", html);
            Assert.Contains("rel=\"modulepreload\" href=\"lib/three/three.module.min.js\"", html);

            var page = await context.NewPageAsync();
            var samples = await TraceAsync(page);

            Assert.Equal("pending → 3d", Sequence(samples));
            AssertNoFlash(samples, "Zapisana preferencja 3D");
            Assert.DoesNotContain(samples, s => s.Css);
            Assert.True(await page.EvaluateAsync<bool>("() => window.__sansPost3dTimings.probe === null"), "Znana preferencja 3D nie powtarza próby płynności.");

            // Odświeżenie: nadal bez sceny CSS.
            var again = await context.NewPageAsync();
            AssertNoFlash(await TraceAsync(again), "Drugie wejście");
        }

        // C. Wymuszony fallback (WebGL programowy): Pending → CSS; scena CSS dopiero po decyzji, Three.js nie jest pobierany.
        // Kolejne wejście z preferencją "css": od razu CSS z serwera (bez Pending), bez modułów 3D.
        [Theory]
        [InlineData(1280, 800)]
        [InlineData(360, 640)]
        public async Task SoftwareRenderer_PendingThenCss_ThenKnownCssWithoutPending(int width, int height)
        {
            await using var context = await ContextAsync(_env, width, height);
            var page = await context.NewPageAsync();
            var requests = new List<string>();
            page.Request += (_, request) => requests.Add(request.Url);
            var samples = await TraceAsync(page);

            Assert.Equal("pending → css", Sequence(samples));
            AssertNoFlash(samples, $"Fallback {width}");
            Assert.DoesNotContain(samples, s => s.Canvas);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", new System.Text.RegularExpressions.Regex("^(software-gl|performance-caveat)$"));
            Assert.DoesNotContain(requests, url => url.Contains("three.module"));
            Assert.Equal("css", await SceneCookieAsync(context));

            var html = await ServerHtmlAsync(context);
            Assert.Contains("data-renderer=\"css\"", html);
            Assert.DoesNotContain("modulepreload", html);
            var next = await context.NewPageAsync();
            var known = await TraceAsync(next);
            Assert.Equal("css", Sequence(known));
            Assert.DoesNotContain(known, s => s.Pending);
            await Expect(next.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", "known");
        }

        // D. Reduced motion: Pending → CSS bez migania; WebGL w ogóle nie startuje.
        [Theory]
        [InlineData(1280, 800)]
        [InlineData(390, 844)]
        public async Task ReducedMotion_PendingThenCss_WithoutFlicker(int width, int height)
        {
            await using var context = await ContextAsync(_env, width, height, reducedMotion: true);
            var page = await context.NewPageAsync();
            var samples = await TraceAsync(page);

            Assert.Equal("pending → css", Sequence(samples));
            AssertNoFlash(samples, "Reduced motion");
            Assert.DoesNotContain(samples, s => s.Canvas);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", "reduced-motion");
            await Expect(page.Locator("nav.entrance-signs").GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToBeVisibleAsync();
        }

        // E. Brak WebGL: Pending → CSS (świadomy fallback), bez canvas.
        [Fact]
        public async Task NoWebGl_PendingThenCss()
        {
            await using var context = await ContextAsync(_env, scripts: NoWebGl);
            var page = await context.NewPageAsync();
            var samples = await TraceAsync(page);

            Assert.Equal("pending → css", Sequence(samples));
            AssertNoFlash(samples, "Brak WebGL");
            Assert.DoesNotContain(samples, s => s.Canvas);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer-reason", "no-webgl");
        }

        // Bez JavaScriptu nie ma kto podjąć decyzji — <noscript> w <head> pokazuje scenę CSS z tabliczkami (linki działają).
        [Fact]
        public async Task WithoutJavaScript_ShowsCssEntranceWithSigns()
        {
            var context = await _env.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = _env.Main.BaseUrl,
                JavaScriptEnabled = false,
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });
            await using var _ = context;
            var page = await context.NewPageAsync();
            await page.GotoAsync("/");

            await Expect(page.Locator(".entrance-pending")).Not.ToBeVisibleAsync();
            await Expect(page.Locator(".saloon")).ToBeVisibleAsync();
            await Expect(page.Locator("nav.entrance-signs").GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToBeVisibleAsync();
        }
    }
}
