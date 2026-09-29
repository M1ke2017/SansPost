using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SansPost.E2E.Infrastructure
{
    public static class Ui
    {
        // Blazor Server: najpierw prerender, potem circuit. Klik przed podłączeniem nic nie robi —
        // czekamy na znacznik ustawiany po pierwszym interaktywnym renderze (html[data-interactive]).
        public static async Task GotoAsync(IPage page, string path)
        {
            await page.GotoAsync(path);
            await WaitInteractiveAsync(page);
        }

        // Entrance (14D-FIX): do decyzji renderera strona jest w stanie Pending (bez sceny i tabliczek) — czekamy na 3D/CSS.
        // Na pozostałych stronach nie ma elementu .entrance, więc warunek jest spełniony od razu.
        public static async Task WaitInteractiveAsync(IPage page)
        {
            await page.WaitForSelectorAsync("html[data-interactive]", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
            await page.WaitForFunctionAsync("() => !document.querySelector('.entrance[data-renderer=\"pending\"]')");
        }

        public static async Task LoginAsync(IPage page, string email, string password, string returnUrl = "/saloon")
        {
            // Najpierw circuit: do podłączenia formularz jest zablokowany (FormGate, Sprint 10 — patrz HydrationE2ETests).
            await GotoAsync(page, $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            await page.GetByLabel("Email").FillAsync(email);
            await page.Locator("#password").FillAsync(password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(url => !new Uri(url).AbsolutePath.StartsWith("/login", StringComparison.Ordinal));
            await WaitInteractiveAsync(page);
        }

        public static async Task<(int ScrollWidth, int ClientWidth)> PageWidthAsync(IPage page)
        {
            var result = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
            return (result[0], result[1]);
        }

        // Dwie klatki renderowania po zmianie viewportu (SetViewportSizeAsync). Zaraz po emulowanej zmianie rozmiaru
        // Chromium potrafi przez chwilę trzymać styl obliczony części elementów z poprzedniej szerokości (np. nagłówek
        // z odstępami desktopowymi) — pomiar musi dotyczyć wyrenderowanego układu, nie stanu przejściowego.
        public static Task SettleLayoutAsync(IPage page) =>
            page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))");

        public static async Task AssertNoHorizontalOverflowAsync(IPage page, string context)
        {
            await SettleLayoutAsync(page);
            var (scroll, client) = await PageWidthAsync(page);
            if (scroll <= client)
                return;
            // Diagnostyka: które elementy wystają poza szerokość strony (klasa, prawa krawędź, początek tekstu).
            var culprits = await page.EvaluateAsync<string>(@"() => {
                const w = document.documentElement.clientWidth;
                return [...document.querySelectorAll('body *')]
                    .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > w + 0.5; })
                    .slice(0, 6)
                    .map(e => `${e.tagName.toLowerCase()}.${String(e.className).trim().replace(/\s+/g, '.')} right=${Math.round(e.getBoundingClientRect().right)} '${(e.textContent || '').trim().slice(0, 40)}'`)
                    .join(' | ');
            }");
            Assert.Fail($"Poziomy overflow strony ({context}): scrollWidth {scroll} > clientWidth {client}; elementy: {culprits}");
        }

        public static ILocator Heading(IPage page, string text) =>
            page.GetByRole(AriaRole.Heading, new() { Name = text, Exact = true });

        // Dokładna ścieżka (+ query) po hoście. Nawigacja Blazor jest po stronie klienta (pushState) — do asercji adresu
        // używamy Expect(page).ToHaveURLAsync (polling), nie WaitForURLAsync, który czasem czeka na zdarzenie load, które nie nadejdzie.
        public static Regex Path(string path) => new($"^https?://[^/]+{Regex.Escape(path)}(#.*)?$");
    }
}
