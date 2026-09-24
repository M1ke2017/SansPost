using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Gość: publiczne widoki bez logowania, treści startowe z PostgreSQL, kategorie, wyszukiwarka, motyw.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class GuestJourneyTests
    {
        private readonly E2EEnvironment _env;

        public GuestJourneyTests(E2EEnvironment env)
        {
            _env = env;
        }

        [Fact]
        public async Task Guest_BrowsesFeedCategoriesSearchPostAndProfile_WithoutLogin()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();

            // Home: treści startowe (seed w PostgreSQL), post przypięty.
            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            Assert.True(await page.Locator(".post-card").CountAsync() >= 10);
            await Expect(page.Locator(".featured-label")).ToBeVisibleAsync();
            await Expect(page.GetByText("Nie ma jeszcze post")).ToHaveCountAsync(0);

            // Newest ↔ Popular (stan w adresie).
            await page.GetByRole(AriaRole.Button, new() { Name = "Popularne" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/?sort=popular"));
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Popularne" })).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Najnowsze" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));

            // Kategorie → feed kategorii (liczniki > 0).
            await page.Locator(".primary-nav").GetByRole(AriaRole.Link, new() { Name = "Kategorie" }).ClickAsync();
            await Expect(Ui.Heading(page, "Kategorie")).ToBeVisibleAsync();
            await Expect(page.Locator(".category-tile")).ToHaveCountAsync(7);
            await Expect(page.GetByText("Jeszcze bez postów")).ToHaveCountAsync(0);
            await page.Locator(".category-tile", new() { HasText = "Gry" }).ClickAsync();
            await Expect(Ui.Heading(page, "Gry")).ToBeVisibleAsync();
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();

            // Wyszukiwarka (pole w nagłówku → /search?q=).
            await page.Locator("#header-search").FillAsync("planszówka");
            await page.Locator("#header-search").PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/search[?]q="));
            await Expect(page.Locator(".post-card-title", new() { HasText = "Planszówka na dwie osoby" })).ToBeVisibleAsync();

            // Szczegóły posta → profil autora.
            await page.Locator(".post-card-title a", new() { HasText = "Planszówka na dwie osoby" }).ClickAsync();
            await Expect(page.Locator("h1#post-title")).ToHaveTextAsync("Planszówka na dwie osoby, która nie trwa trzech godzin");
            await Expect(page.Locator(".comment").First).ToBeVisibleAsync();
            await Expect(page.GetByText("Zaloguj się, aby dołączyć do dyskusji.")).ToBeVisibleAsync();

            var author = await page.Locator(".author-line .who a").InnerTextAsync();
            await page.Locator(".author-line .who a").ClickAsync();
            await Expect(page.Locator("h1")).ToHaveTextAsync(author);
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            Assert.DoesNotContain("@", await page.Locator("main").InnerTextAsync());   // żadnych emaili na profilu
        }

        [Fact]
        public async Task ThemeToggle_SwitchesAndPersists()
        {
            await using var context = await _env.NewContextAsync(colorScheme: ColorScheme.Light);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

            await page.GetByRole(AriaRole.Button, new() { Name = "Przełącz jasny lub ciemny motyw" }).ClickAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

            await page.ReloadAsync();
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        }

        [Fact]
        public async Task SystemDarkPreference_IsRespectedWithoutStoredChoice()
        {
            await using var context = await _env.NewContextAsync(colorScheme: ColorScheme.Dark);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/categories");

            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        }

        // Pasek kategorii: strzałka w prawo dociera do Feedback (desktop), swipe/scroll na mobile.
        [Theory]
        [InlineData(1280, 800)]
        [InlineData(1024, 768)]
        public async Task CategoryStrip_ArrowsReachLastCategory(int width, int height)
        {
            await using var context = await _env.NewContextAsync(width: width, height: height, reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            // Nieaktywna strzałka jest ukryta (display: none) — IncludeHidden, żeby sprawdzić jej stan.
            var right = page.GetByRole(AriaRole.Button, new() { Name = "Przewiń kategorie w prawo", IncludeHidden = true });
            var left = page.GetByRole(AriaRole.Button, new() { Name = "Przewiń kategorie w lewo", IncludeHidden = true });
            var feedback = page.Locator(".sign-row a[href='/c/feedback']");

            await Expect(left).ToBeDisabledAsync();
            await Expect(right).ToBeEnabledAsync();
            Assert.False(await IsFullyVisibleInStripAsync(page, feedback));

            // Po kliknięciu czekamy na faktyczne przewinięcie i przeliczenie stanu strzałek (zdarzenie scroll),
            // zamiast sprawdzać przycisk, który za chwilę zostanie ukryty.
            for (var i = 0; i < 5 && await right.IsEnabledAsync(); i++)
            {
                var before = await page.Locator(".sign-row").EvaluateAsync<double>("t => t.scrollLeft");
                await right.ClickAsync();
                await page.WaitForFunctionAsync("before => document.querySelector('.sign-row').scrollLeft > before", before);
                await page.WaitForFunctionAsync(@"() => { const t = document.querySelector('.sign-row'), r = document.querySelector('.strip-btn-right');
                    return r.disabled === !(t.scrollLeft < t.scrollWidth - t.clientWidth - 1); }");
            }

            Assert.True(await IsFullyVisibleInStripAsync(page, feedback));
            await Expect(right).ToBeDisabledAsync();
            await Expect(left).ToBeEnabledAsync();
            await feedback.ClickAsync();
            await Expect(Ui.Heading(page, "Feedback")).ToBeVisibleAsync();
        }

        [Fact]
        public async Task CategoryStrip_Mobile_UsesNativeScroll()
        {
            await using var context = await _env.NewContextAsync(width: 390, height: 844);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Przewiń kategorie w prawo" })).ToBeHiddenAsync();
            var feedback = page.Locator(".sign-row a[href='/c/feedback']");
            await feedback.ScrollIntoViewIfNeededAsync();
            Assert.True(await IsFullyVisibleInStripAsync(page, feedback));
            await Ui.AssertNoHorizontalOverflowAsync(page, "home 390 po przewinięciu paska");
        }

        private static Task<bool> IsFullyVisibleInStripAsync(IPage page, ILocator item) =>
            item.EvaluateAsync<bool>(@"el => { const t = el.closest('.sign-row').getBoundingClientRect(), r = el.getBoundingClientRect();
                return r.left >= t.left - 1 && r.right <= t.right + 1; }");
    }
}
