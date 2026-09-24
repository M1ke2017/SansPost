using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class KeyboardAndResponsiveE2ETests
    {
        private readonly E2EEnvironment _env;

        public KeyboardAndResponsiveE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static Task<string> FocusedAsync(IPage page) =>
            page.EvaluateAsync<string>("() => { const e = document.activeElement; return (e.getAttribute('aria-label') || e.id || e.textContent || e.tagName).trim().slice(0, 60); }");

        // Tylko klawiatura: skip link, nagłówek, wyszukiwarka, strzałki kategorii, polubienie, dzwonek + panel,
        // okno zgłoszenia, edytor posta i komentarza, okno moderacji. Tab / Shift+Tab / Enter / Space / Escape.
        [Fact]
        public async Task KeyboardOnly_CoreInteractions()
        {
            await using var user = await Session.UserAsync(_env, reducedMotion: true);
            var page = user.Page;
            var author = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, author, "Post do testu klawiatury");
            await Api.CommentAsync(_env.Main, author, postId, "Komentarz autora");
            await Api.CommentAsync(_env.Main, author, await Api.CreatePostAsync(_env.Main, user.User!, "Mój post"), "Powiadomienie dla klawiatury");

            // Skip link: pierwszy element w kolejności Tab (DOM); Enter przenosi fokus do <main>.
            // (Po nawigacji FocusOnNavigate stawia fokus na h1, więc zwykłe Tab zaczyna się od treści.)
            await Ui.GotoAsync(page, "/");
            var firstTabbable = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('a[href], button, input, select, textarea, [tabindex]')]
                .find(e => e.tabIndex >= 0 && !e.disabled).textContent.trim()");
            Assert.Equal("Przejdź do treści", firstTabbable);
            // Aktywacja skip linku: patrz KnownIssue_SkipLink_DoesNotMoveFocus.

            // Tab / Shift+Tab w nagłówku: wyszukiwarka ↔ poprzedni element.
            await page.Locator("#header-search").FocusAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            Assert.NotEqual("header-search", await FocusedAsync(page));
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal("header-search", await FocusedAsync(page));
            await page.Keyboard.TypeAsync("planszówka");
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator(".post-card-title", new() { HasText = "Planszówka" })).ToBeVisibleAsync();

            // Strzałki kategorii (Enter i Space).
            await Ui.GotoAsync(page, "/");
            var right = page.GetByRole(AriaRole.Button, new() { Name = "Przewiń kategorie w prawo" });
            await right.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => document.querySelector('.sign-row').scrollLeft > 0");
            await page.GetByRole(AriaRole.Button, new() { Name = "Przewiń kategorie w lewo" }).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await page.WaitForFunctionAsync("() => document.querySelector('.sign-row').scrollLeft === 0");

            // Dzwonek: Enter otwiera panel, Escape zamyka i przywraca fokus.
            await page.Locator("#notif-trigger").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("#notif-panel")).ToBeVisibleAsync();
            await Expect(page.Locator("#notif-panel")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(page.Locator("#notif-panel .notif-item, #notif-panel button").First).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("#notif-panel")).ToHaveCountAsync(0);
            await Expect(page.Locator("#notif-trigger")).ToBeFocusedAsync();

            // Polubienie Spacją.
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            var like = page.Locator(".post-actions-bar .like-button");
            await like.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "true");

            // Okno zgłoszenia: Enter otwiera (fokus w oknie), Escape zamyka i wraca na "Zgłoś".
            var report = page.Locator(".post-actions-bar").GetByRole(AriaRole.Button, new() { Name = "Zgłoś" });
            await report.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("dialog[open]")).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('dialog[open]')"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(report).ToBeFocusedAsync();

            // Edytor komentarza: pisanie + Tab do przycisku + Enter.
            await page.Locator("#new-comment").FocusAsync();
            await page.Keyboard.TypeAsync("Komentarz z klawiatury");
            await page.Keyboard.PressAsync("Tab");   // licznik znaków nie jest fokusowalny → od razu przycisk
            Assert.Equal("Opublikuj komentarz", await FocusedAsync(page));
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator(".comment", new() { HasText = "Komentarz z klawiatury" })).ToBeVisibleAsync();

            // Edytor posta: Tab przez pola, Enter wysyła.
            await Ui.GotoAsync(page, "/new");
            await page.Locator("#post-title").FocusAsync();
            await page.Keyboard.TypeAsync("Post z klawiatury");
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("ArrowDown");   // select kategorii
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.TypeAsync("Treść wpisana z klawiatury.");
            await page.Locator("main form button[type=submit]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(page.Locator("h1#post-title")).ToHaveTextAsync("Post z klawiatury");

            // Okno moderacji: Escape zamyka.
            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(author.Email, E2EEnvironment.UserPassword))
                await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(api, "/api/reports", new { targetType = "Post", targetId = postId, reason = "Other" });
            await using var admin = await Session.AdminAsync(_env);
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            await admin.Page.Locator(".queue-item").First.GetByRole(AriaRole.Button, new() { Name = "Odrzuć zgłoszenie" }).FocusAsync();
            await admin.Page.Keyboard.PressAsync("Enter");
            await Expect(admin.Page.Locator("dialog[open]")).ToBeVisibleAsync();
            await admin.Page.Keyboard.PressAsync("Escape");
            await Expect(admin.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
        }

        // KNOWN ISSUE (P2, a11y): Enter na "Przejdź do treści" nie przenosi fokusu do <main> — router Blazor przechwytuje
        // link "#main" jako nawigację i domyślne przeniesienie fokusu przez przeglądarkę nie następuje. Test opisuje OBECNE zachowanie.
        [Fact]
        [Trait("KnownIssue", "P2")]
        public async Task KnownIssue_SkipLink_DoesNotMoveFocus()
        {
            await using var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");

            await page.Locator(".skip-link").FocusAsync();
            await page.Keyboard.PressAsync("Enter");

            await Expect(page.Locator("main#main")).Not.ToBeFocusedAsync();
        }

        public static IEnumerable<object[]> Viewports() => new[]
        {
            new object[] { 360, 800 }, new object[] { 390, 844 }, new object[] { 768, 1024 },
            new object[] { 1024, 768 }, new object[] { 1280, 800 }, new object[] { 1440, 900 }
        };

        // Brak poziomego overflow strony (pasek kategorii przewija się we własnym kontenerze).
        [Theory]
        [MemberData(nameof(Viewports))]
        public async Task NoHorizontalOverflow_OnMainScreens(int width, int height)
        {
            await using var user = await Session.UserAsync(_env, width: width, height: height, reducedMotion: true);
            foreach (var path in new[] { "/", "/categories", "/c/games", "/search?q=gry", "/post-view/2", "/u/DustyRaven", "/me", "/new" })
            {
                await Ui.GotoAsync(user.Page, path);
                await Ui.AssertNoHorizontalOverflowAsync(user.Page, $"{path} @ {width}x{height}");
            }

            await using var guestContext = await _env.NewContextAsync(width: width, height: height);
            var guest = await guestContext.NewPageAsync();
            foreach (var path in new[] { "/login", "/register", "/nie-istnieje" })
            {
                await Ui.GotoAsync(guest, path);
                await Ui.AssertNoHorizontalOverflowAsync(guest, $"{path} @ {width}x{height}");
            }

            await using var admin = await Session.AdminAsync(_env, width: width, height: height);
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            await Ui.AssertNoHorizontalOverflowAsync(admin.Page, $"/admin/moderation @ {width}x{height}");
        }
    }
}
