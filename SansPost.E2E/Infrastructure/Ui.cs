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

        public static Task WaitInteractiveAsync(IPage page) =>
            page.WaitForSelectorAsync("html[data-interactive]", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });

        public static async Task LoginAsync(IPage page, string email, string password, string returnUrl = "/")
        {
            // Najpierw circuit: prerenderowany formularz jest zastępowany po podłączeniu i wpisane wartości by przepadły
            // (znalezisko P2 — Sprint 9 Quality Report).
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

        public static async Task AssertNoHorizontalOverflowAsync(IPage page, string context)
        {
            var (scroll, client) = await PageWidthAsync(page);
            Assert.True(scroll <= client, $"Poziomy overflow strony ({context}): scrollWidth {scroll} > clientWidth {client}");
        }

        public static ILocator Heading(IPage page, string text) =>
            page.GetByRole(AriaRole.Heading, new() { Name = text, Exact = true });

        // Dokładna ścieżka (+ query) po hoście. Nawigacja Blazor jest po stronie klienta (pushState) — do asercji adresu
        // używamy Expect(page).ToHaveURLAsync (polling), nie WaitForURLAsync, który czasem czeka na zdarzenie load, które nie nadejdzie.
        public static Regex Path(string path) => new($"^https?://[^/]+{Regex.Escape(path)}(#.*)?$");
    }
}
