using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Powiadomienia w przeglądarce: dwa niezależne konteksty (A — autor, B — komentujący).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Critical")]
    public class NotificationsE2ETests
    {
        private readonly E2EEnvironment _env;

        public NotificationsE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Bell(IPage page) => page.Locator("#notif-trigger");

        [Fact]
        public async Task CommentByB_ShowsBadgeForA_ClickMarksReadAndOpensComment()
        {
            await using var a = await Session.UserAsync(_env);
            await using var b = await Session.UserAsync(_env);
            var title = $"Post A {Guid.NewGuid():N}"[..22];

            // A publikuje post (UI).
            await Ui.GotoAsync(a.Page, "/new");
            await a.Page.Locator("#post-title").FillAsync(title);
            await a.Page.Locator("#post-category").SelectOptionAsync("General");
            await a.Page.Locator("#post-content").FillAsync("Czekam na komentarze.");
            await a.Page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj" }).ClickAsync();
            await Expect(a.Page.Locator("h1#post-title")).ToHaveTextAsync(title);
            var postPath = new Uri(a.Page.Url).AbsolutePath;
            await Expect(Bell(a.Page)).ToHaveAttributeAsync("aria-label", "Powiadomienia");

            // B komentuje (UI, osobny kontekst).
            await Ui.GotoAsync(b.Page, postPath);
            await b.Page.Locator("#new-comment").FillAsync("Komentarz od B");
            await b.Page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(b.Page.Locator(".comment", new() { HasText = "Komentarz od B" })).ToBeVisibleAsync();
            Assert.Equal(0, await b.Page.Locator(".notif-badge").CountAsync());   // własny komentarz nie powiadamia B

            // A odświeża: licznik 1 i poprawna nazwa dostępna.
            await Ui.GotoAsync(a.Page, "/saloon");
            await Expect(a.Page.Locator(".notif-badge")).ToHaveTextAsync("1");
            await Expect(Bell(a.Page)).ToHaveAttributeAsync("aria-label", "Powiadomienia, nieprzeczytane: 1");

            // Panel: w granicach viewportu, alias B, post, stan nieprzeczytany.
            await Bell(a.Page).ClickAsync();
            var panel = a.Page.Locator("#notif-panel");
            await Expect(panel).ToBeVisibleAsync();
            await AssertInsideViewportAsync(a.Page, panel);
            var item = panel.Locator(".notif-item").First;
            await Expect(item).ToContainTextAsync(b.User!.Alias);
            await Expect(item).ToContainTextAsync(title);
            await Expect(item).ToHaveClassAsync(new Regex("is-unread"));

            // Klik: przeczytane → właściwy post → kotwica komentarza → licznik spada.
            await item.ClickAsync();
            await Expect(a.Page).ToHaveURLAsync(new Regex(Regex.Escape(postPath) + "#comment-\\d+$"));
            var anchor = new Uri(a.Page.Url).Fragment.TrimStart('#');
            await Expect(a.Page.Locator($"#{anchor}")).ToBeInViewportAsync();
            await Expect(a.Page.Locator($"#{anchor}")).ToContainTextAsync("Komentarz od B");
            await Expect(a.Page.Locator(".notif-badge")).ToHaveCountAsync(0);
            await Expect(Bell(a.Page)).ToHaveAttributeAsync("aria-label", "Powiadomienia");

            await Bell(a.Page).ClickAsync();
            await Expect(a.Page.Locator("#notif-panel .notif-item").First).Not.ToHaveClassAsync(new Regex("is-unread"));
        }

        // 390 px: panel w granicach ekranu, bez poziomego overflow, Escape zamyka i przywraca fokus na dzwonek.
        [Fact]
        public async Task NotificationPanel_Mobile_FitsViewport_EscapeRestoresFocus()
        {
            await using var a = await Session.UserAsync(_env, width: 390, height: 844);
            var b = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, a.User!, "Mobilny post z bardzo długim tytułem, który trzeba zawinąć w panelu");
            await Api.CommentAsync(_env.Main, b, postId, "Komentarz na telefonie");

            await Ui.GotoAsync(a.Page, "/saloon");
            await Expect(a.Page.Locator(".notif-badge")).ToHaveTextAsync("1");
            await Bell(a.Page).ClickAsync();
            var panel = a.Page.Locator("#notif-panel");
            await Expect(panel).ToBeVisibleAsync();

            await AssertInsideViewportAsync(a.Page, panel);
            await Ui.AssertNoHorizontalOverflowAsync(a.Page, "panel powiadomień 390");
            await Expect(a.Page.Locator(".bottom-nav")).ToBeVisibleAsync();   // nawigacja dostępna, panel jej nie zasłania

            await a.Page.Keyboard.PressAsync("Escape");
            await Expect(panel).ToHaveCountAsync(0);
            await Expect(Bell(a.Page)).ToBeFocusedAsync();
        }

        // Cel ukryty po powstaniu powiadomienia: produktowy komunikat, bez 404/ProblemDetails i bez wycieku tytułu.
        [Fact]
        public async Task HiddenTarget_ShowsNotAvailable_WithoutLeakingTitle()
        {
            await using var a = await Session.UserAsync(_env);
            var b = await _env.Main.CreateUserAsync();
            var secretTitle = $"Tytul do ukrycia {Guid.NewGuid():N}"[..30];
            var postId = await Api.CreatePostAsync(_env.Main, a.User!, secretTitle);
            await Api.CommentAsync(_env.Main, b, postId, "Komentarz przed ukryciem");
            await Api.AdminAsync(_env.Main, $"/api/moderation/posts/{postId}/hide");

            await Ui.GotoAsync(a.Page, "/saloon");
            await Bell(a.Page).ClickAsync();
            var item = a.Page.Locator("#notif-panel .notif-item").First;
            await Expect(item).ToContainTextAsync("ta treść nie jest już dostępna");
            await Expect(a.Page.Locator("#notif-panel")).Not.ToContainTextAsync(secretTitle);

            var urlBefore = a.Page.Url;
            await item.ClickAsync();
            await Expect(a.Page.GetByText("Ta treść nie jest już dostępna.")).ToBeVisibleAsync();
            Assert.Equal(urlBefore, a.Page.Url);
            Assert.DoesNotContain("ProblemDetails", await a.Page.ContentAsync());
            Assert.DoesNotContain(secretTitle, await a.Page.Locator("body").InnerTextAsync());
        }

        private static async Task AssertInsideViewportAsync(IPage page, ILocator element)
        {
            var box = await element.BoundingBoxAsync();
            var viewport = page.ViewportSize!;
            Assert.NotNull(box);
            Assert.True(box!.X >= 0 && box.X + box.Width <= viewport.Width + 0.5, $"Panel poza viewportem: x={box.X} w={box.Width} vw={viewport.Width}");
        }
    }
}
