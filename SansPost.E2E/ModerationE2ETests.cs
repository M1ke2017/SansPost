using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Zgłoszenia (użytkownik), moderacja (admin), status konta (zawieszenie, przywrócenie, blokada).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class ModerationE2ETests
    {
        private readonly E2EEnvironment _env;

        public ModerationE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private async Task ReportAsync(SansPostServer.TestUser reporter, string targetType, int targetId)
        {
            using var api = await _env.Main.CreateAuthenticatedApiClientAsync(reporter.Email, E2EEnvironment.UserPassword);
            (await api.PostAsJsonAsync("/api/reports", new { targetType, targetId, reason = "Spam", details = "E2E" })).EnsureSuccessStatusCode();
        }

        private static ILocator QueueItem(IPage admin, string badge) =>
            admin.Locator(".queue-item").Filter(new() { Has = admin.Locator(".badge", new() { HasTextRegex = new Regex($"^{Regex.Escape(badge)}$") }) });

        private static async Task ConfirmAsync(IPage admin, string confirmLabel, string toast)
        {
            var dialog = admin.Locator("dialog[open]");
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = confirmLabel, Exact = true }).ClickAsync();
            await Expect(admin.Locator(".toast", new() { HasText = toast })).ToBeVisibleAsync();
            await Expect(admin.Locator("dialog[open]")).ToHaveCountAsync(0);
        }

        [Fact]
        public async Task User_ReportsPost_ThroughDialog_GetsConfirmation()
        {
            var author = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, author, "Post do zgłoszenia");
            await using var reporter = await Session.UserAsync(_env);
            var page = reporter.Page;

            await Ui.GotoAsync(page, $"/post-view/{postId}");
            await page.Locator(".post-actions-bar").GetByRole(AriaRole.Button, new() { Name = "Zgłoś" }).ClickAsync();
            var dialog = page.Locator("dialog[open]");
            await Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = "Zgłoś post", Exact = true })).ToBeVisibleAsync();
            await dialog.GetByText("Nękanie", new() { Exact = true }).ClickAsync();
            await dialog.Locator("#report-details").FillAsync("Uporczywe zaczepki w komentarzach.");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Wyślij zgłoszenie" }).ClickAsync();

            await Expect(page.Locator(".toast", new() { HasText = "Dziękujemy. Zgłoszenie trafiło do moderacji." })).ToBeVisibleAsync();
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            Assert.DoesNotMatch(new Regex("Exception|ProblemDetails|\\b4\\d\\d\\b|\\b5\\d\\d\\b"), await page.Locator("body").InnerTextAsync());
        }

        [Fact]
        public async Task Admin_HidesReportedPost_PublicViewDisappears_AuditRestore_ReturnsIt()
        {
            var author = await _env.Main.CreateUserAsync();
            var reporter = await _env.Main.CreateUserAsync();
            var title = $"Do moderacji {Guid.NewGuid():N}"[..26];
            var postId = await Api.CreatePostAsync(_env.Main, author, title);
            await ReportAsync(reporter, "Post", postId);

            await using var admin = await Session.AdminAsync(_env);
            await using var guestContext = await _env.NewContextAsync();
            var guest = await guestContext.NewPageAsync();

            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            var item = QueueItem(admin.Page, $"Post #{postId}");
            await Expect(item).ToBeVisibleAsync();
            await Expect(item).ToContainTextAsync(reporter.Alias);

            await item.GetByRole(AriaRole.Button, new() { Name = "Ukryj treść" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Ukryj treść", "Treść ukryta, zgłoszenia rozstrzygnięte.");
            await Expect(QueueItem(admin.Page, $"Post #{postId}")).ToHaveCountAsync(0);

            await Ui.GotoAsync(guest, $"/post-view/{postId}");
            await Expect(Ui.Heading(guest, "Ten post nie jest dostępny")).ToBeVisibleAsync();
            Assert.DoesNotContain(title, await guest.ContentAsync());

            // Dziennik działań → przywrócenie.
            await admin.Page.GetByRole(AriaRole.Tab, new() { Name = "Historia działań" }).ClickAsync();
            var row = admin.Page.Locator("tr", new() { HasText = $"Post #{postId}" }).Filter(new() { HasText = "Ukrycie posta" });
            await Expect(row).ToBeVisibleAsync();
            await row.GetByRole(AriaRole.Button, new() { Name = "Przywróć post" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Przywróć", "Treść przywrócona.");
            await Expect(admin.Page.Locator("tr", new() { HasText = $"Post #{postId}" }).Filter(new() { HasText = "Przywrócenie posta" })).ToBeVisibleAsync();

            await Ui.GotoAsync(guest, $"/post-view/{postId}");
            await Expect(guest.Locator("h1#post-title")).ToHaveTextAsync(title);
        }

        [Fact]
        public async Task Admin_HidesReportedComment_CommentDisappearsForGuest()
        {
            var author = await _env.Main.CreateUserAsync();
            var commenter = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, author, "Post z niegrzecznym komentarzem");
            var commentId = await Api.CommentAsync(_env.Main, commenter, postId, "Komentarz do ukrycia");
            await ReportAsync(author, "Comment", commentId);

            await using var admin = await Session.AdminAsync(_env);
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            var item = QueueItem(admin.Page, $"Komentarz #{commentId}");
            await item.GetByRole(AriaRole.Button, new() { Name = "Ukryj treść" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Ukryj treść", "Treść ukryta, zgłoszenia rozstrzygnięte.");

            await using var guestContext = await _env.NewContextAsync();
            var guest = await guestContext.NewPageAsync();
            await Ui.GotoAsync(guest, $"/post-view/{postId}");
            await Expect(guest.Locator("h1#post-title")).ToBeVisibleAsync();
            await Expect(guest.GetByText("Komentarz do ukrycia")).ToHaveCountAsync(0);
        }

        // Zawieszenie → czyta, nie pisze (komunikat); przywrócenie → pisze; blokada → sesja kończy się, UI gościa.
        [Fact]
        public async Task Suspend_Reactivate_Ban_UserExperience()
        {
            await using var user = await Session.UserAsync(_env);
            var reporter = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, user.User!, "Post użytkownika do moderacji konta");
            await ReportAsync(reporter, "Post", postId);

            await using var admin = await Session.AdminAsync(_env);
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            var item = QueueItem(admin.Page, $"Post #{postId}");
            await item.GetByRole(AriaRole.Button, new() { Name = "Szczegóły i autor" }).ClickAsync();
            await item.GetByRole(AriaRole.Button, new() { Name = "Zawieś autora" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Zawieś konto", "Konto zawieszone.");

            // Regresja (Sprint 9 P2, naprawione w Sprincie 10): zawieszenie podbija AuthVersion — otwarta sesja kończy się
            // z jasnym komunikatem (logowanie z "Twoja sesja została zakończona."), a nie po cichu jako gość.
            var page = user.Page;
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            await Expect(page).ToHaveURLAsync(new Regex("/login[?]ended=1&returnUrl="));
            await Expect(page.GetByText("Sesja zakończona")).ToBeVisibleAsync();
            await Expect(page.GetByText("Twoja sesja została zakończona.", new() { Exact = false })).ToBeVisibleAsync();
            await Expect(page.Locator(".menu-trigger")).ToHaveCountAsync(0);

            // Po ponownym zalogowaniu: czyta, nie pisze, widzi produktowy komunikat.
            await Ui.LoginAsync(page, user.User!.Email, E2EEnvironment.UserPassword, $"/post-view/{postId}");
            await Expect(page.Locator(".account-banner")).ToContainTextAsync("Konto zawieszone.");
            await Expect(page.Locator(".account-banner")).ToContainTextAsync("Możesz przeglądać SansPost, ale publikowanie jest obecnie niedostępne");
            await Expect(page.Locator("h1#post-title")).ToBeVisibleAsync();
            await Expect(page.Locator("#new-comment")).ToHaveCountAsync(0);
            await Expect(page.GetByText("Twoje konto jest zawieszone")).ToBeVisibleAsync();
            await Ui.GotoAsync(page, "/new");
            await Expect(page.GetByText("Publikowanie jest wyłączone")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj" })).ToBeDisabledAsync();

            // Przywrócenie → zapis znowu działa.
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            item = QueueItem(admin.Page, $"Post #{postId}");
            await item.GetByRole(AriaRole.Button, new() { Name = "Szczegóły i autor" }).ClickAsync();
            await item.GetByRole(AriaRole.Button, new() { Name = "Przywróć konto" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Przywróć konto", "Konto przywrócone.");

            // Przywrócenie też kończy sesję (AuthVersion) — logowanie ponownie, potem zapis działa.
            await Ui.LoginAsync(page, user.User!.Email, E2EEnvironment.UserPassword, $"/post-view/{postId}");
            await Expect(page.Locator(".account-banner")).ToHaveCountAsync(0);
            await page.Locator("#new-comment").FillAsync("Po przywróceniu mogę pisać");
            await page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(page.Locator(".comment", new() { HasText = "Po przywróceniu mogę pisać" })).ToBeVisibleAsync();

            // Blokada → istniejąca sesja przestaje działać przy następnej nawigacji.
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            item = QueueItem(admin.Page, $"Post #{postId}");
            await item.GetByRole(AriaRole.Button, new() { Name = "Szczegóły i autor" }).ClickAsync();
            await item.GetByRole(AriaRole.Button, new() { Name = "Zablokuj autora" }).ClickAsync();
            await ConfirmAsync(admin.Page, "Zablokuj konto", "Konto zablokowane.");

            await page.Locator(".primary-nav").GetByRole(AriaRole.Link, new() { Name = "Kategorie" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex("/login[?]ended=1"));
            await Expect(page.GetByText("Sesja zakończona")).ToBeVisibleAsync();
            await Expect(page.Locator(".menu-trigger")).ToHaveCountAsync(0);
            await page.GotoAsync("/me");
            await Expect(page).ToHaveURLAsync(new Regex("/login"));
        }

        // Regresja (Sprint 9 P3, naprawione w Sprincie 10): już przy pierwszym otwarciu tytuł okna wskazuje cel —
        // "Zgłoś post" / "Zgłoś komentarz" — także po przełączeniu między postem a komentarzem.
        [Fact]
        public async Task ReportDialog_TitleMatchesTarget_FromFirstOpen()
        {
            var author = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, author, "Tytuł okna zgłoszenia");
            await Api.CommentAsync(_env.Main, author, postId, "Komentarz do zgłoszenia");
            await using var reporter = await Session.UserAsync(_env);
            var page = reporter.Page;
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            var title = page.Locator("dialog[open] .dialog-title");

            await page.Locator(".post-actions-bar").GetByRole(AriaRole.Button, new() { Name = "Zgłoś" }).ClickAsync();
            await Expect(title).ToHaveTextAsync("Zgłoś post");
            await Expect(page.Locator("dialog[open]")).ToHaveAttributeAsync("aria-labelledby", new Regex("^dialog-"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);

            await page.Locator(".comment").First.GetByRole(AriaRole.Button, new() { Name = "Zgłoś" }).ClickAsync();
            await Expect(title).ToHaveTextAsync("Zgłoś komentarz");
            await page.Keyboard.PressAsync("Escape");

            await page.Locator(".post-actions-bar").GetByRole(AriaRole.Button, new() { Name = "Zgłoś" }).ClickAsync();
            await Expect(title).ToHaveTextAsync("Zgłoś post");
        }

        // Zwykły użytkownik nie wchodzi do panelu admina (produktowy "Brak dostępu").
        [Fact]
        public async Task RegularUser_CannotOpenModeration()
        {
            await using var user = await Session.UserAsync(_env);
            await Ui.GotoAsync(user.Page, "/admin/moderation");

            await Expect(Ui.Heading(user.Page, "Brak dostępu")).ToBeVisibleAsync();
            await Expect(user.Page.Locator(".queue-item")).ToHaveCountAsync(0);
        }
    }
}
