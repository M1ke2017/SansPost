using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 25 — aplikacja działa pod własną CSP (bez 'unsafe-eval', skrypty inline tylko z nonce/hashem). Kontekst BEZ
    // BypassCSP. Sterowanie wyłącznie lokatorami i nawigacją: Playwright wykonuje WaitForFunction/EvaluateAsync przez
    // new Function, które ta CSP słusznie blokuje. Naruszenia polityki (securitypolicyviolation) skrypt init wypisuje na
    // konsolę — test zbiera je ze zdarzeń konsoli. Przepływy: Entrance 3D, rejestracja (losowanie przydomka), logowanie,
    // Main Hall 3D, BAR, Wanted, Kącik muzyczny z odtwarzaniem (atrapa strumienia https), stół gry (SignalR), post, strona błędu.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Security")]
    public class SecurityHeadersE2ETests
    {
        private const string Recorder = @"document.addEventListener('securitypolicyviolation', e =>
            console.error(`CSP-VIOLATION ${e.effectiveDirective} ${e.blockedURI || 'inline'} ${(e.sample || '').slice(0, 60)}`));";

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
        private readonly ITestOutputHelper _output;

        public SecurityHeadersE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private static async Task OpenAsync(IPage page, string path)
        {
            await page.GotoAsync(path);
            await page.Locator("html[data-interactive]").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        }

        [Fact]
        public async Task MainFlows_RunUnderCsp_WithoutViolations()
        {
            var user = await _env.Main.CreateUserAsync();
            await using var context = await _env.NewContextAsync(enforceCsp: true);
            await context.AddInitScriptAsync(Recorder);
            await context.AddInitScriptAsync(CapableGpu);
            await FakeRadio.RouteStreamsAsync(context);
            var page = await context.NewPageAsync();
            var violations = new List<string>();
            var where = "";
            page.Console += (_, message) => { if (message.Text.StartsWith("CSP-VIOLATION")) violations.Add($"{where}: {message.Text}"); };

            // Nagłówek CSP rzeczywiście obowiązuje w tym kontekście.
            where = "Entrance 3D";
            var response = await page.GotoAsync("/?scene=3d");
            Assert.Contains("script-src 'self' 'nonce-", response!.Headers["content-security-policy"]);
            await Expect(page.Locator(".entrance")).ToHaveAttributeAsync("data-renderer", "3d", new() { Timeout = 20_000 });

            where = "Register";
            await OpenAsync(page, "/register");
            var alias = await page.Locator("#alias-value").TextContentAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Losuj inny" }).ClickAsync();
            await Expect(page.Locator("#alias-value")).Not.ToHaveTextAsync(alias ?? "", new() { Timeout = 10_000 });

            where = "Login";
            await OpenAsync(page, "/login?returnUrl=%2Fcategories");
            await page.GetByLabel("Email").FillAsync(user.Email);
            await page.Locator("#password").FillAsync(E2EEnvironment.UserPassword);
            await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/categories"));

            where = "Main Hall 3D";
            await OpenAsync(page, "/saloon?scene=3d");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 30_000 });

            where = "BAR";
            await OpenAsync(page, "/saloon?scene=3d&bar=newest");
            await Expect(page.Locator("dialog[open]").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            where = "Wanted";
            await OpenAsync(page, "/saloon?scene=3d&wanted=board");
            await Expect(page.Locator("dialog[open]").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

            where = "Music";
            await OpenAsync(page, "/saloon?scene=3d&music=radio");
            await page.Locator("dialog[open] [data-music-toggle]").ClickAsync(new() { Timeout = 30_000 });
            await Expect(page.Locator(".mini-player")).ToBeVisibleAsync(new() { Timeout = 20_000 });

            where = "Game Table (SignalR)";
            await OpenAsync(page, "/saloon?scene=3d&game=table");
            await Expect(page.Locator("dialog[open].game-panel .game-start")).ToBeEnabledAsync(new() { Timeout = 30_000 });

            where = "Post";
            await OpenAsync(page, "/post-view/2");

            // Statyczna strona błędu: skrypty inline dozwolone hashem, przycisk bez inline onclick.
            where = "error.html";
            await page.GotoAsync("/error.html");
            await Expect(page.Locator("[data-reload]")).ToBeVisibleAsync();
            await page.WaitForTimeoutAsync(500);

            foreach (var v in violations) _output.WriteLine(v);
            Assert.True(violations.Count == 0, "Naruszenia CSP:\n" + string.Join("\n", violations));
        }
    }
}
