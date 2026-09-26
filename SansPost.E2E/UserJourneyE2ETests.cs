using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Pełna ścieżka użytkownika — po każdej operacji sprawdzany jest rzeczywisty rezultat, nie tylko adres.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Critical")]
    public class UserJourneyE2ETests
    {
        private readonly E2EEnvironment _env;

        public UserJourneyE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        [Fact]
        public async Task CreatePost_Like_Comment_EditComment_EditPost_Profile_Logout()
        {
            await using var session = await Session.UserAsync(_env);
            var page = session.Page;
            var title = $"Wiadomość E2E {Guid.NewGuid():N}"[..28];

            // Create Post (nagłówek → kompozytor).
            await page.Locator(".app-header").GetByRole(AriaRole.Link, new() { Name = "Nowy post" }).ClickAsync();
            await Expect(Ui.Heading(page, "Nowy post")).ToBeVisibleAsync();
            await page.Locator("#post-title").FillAsync(title);
            await page.Locator("#post-category").SelectOptionAsync("Projects");
            await page.Locator("#post-content").FillAsync("Pierwsza linia.\n\nDruga linia posta.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj" }).ClickAsync();
            await Expect(page.Locator("h1#post-title")).ToHaveTextAsync(title);
            await Expect(page.GetByText("Post opublikowany.")).ToBeVisibleAsync();
            var postUrl = page.Url;

            // Feed: nowy post na górze Najnowszych.
            await page.Locator(".primary-nav").GetByRole(AriaRole.Link, new() { Name = "Odkrywaj" }).ClickAsync();
            await Expect(page.Locator(".post-list .post-card-title").First).ToHaveTextAsync(title);

            // Open Post → Like (stan i licznik).
            await page.Locator(".post-card-title a", new() { HasText = title }).First.ClickAsync();
            var like = page.Locator(".post-actions-bar .like-button");
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "false");
            await like.ClickAsync();
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(like).ToContainTextAsync("1");
            await page.ReloadAsync();
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".post-actions-bar .like-button")).ToHaveAttributeAsync("aria-pressed", "true");

            // Comment.
            await page.Locator("#new-comment").FillAsync("Mój pierwszy komentarz");
            await page.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            var comment = page.Locator(".comment", new() { HasText = "Mój pierwszy komentarz" });
            await Expect(comment).ToBeVisibleAsync();
            await Expect(page.Locator("#comments-title")).ToContainTextAsync("1");

            // Edit Comment.
            await comment.GetByRole(AriaRole.Button, new() { Name = "Edytuj" }).ClickAsync();
            var editor = page.Locator("textarea[id^=edit-comment-]");
            await editor.FillAsync("Komentarz po edycji");
            await page.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
            await Expect(page.Locator(".comment", new() { HasText = "Komentarz po edycji" })).ToContainTextAsync("edytowano");
            await page.ReloadAsync();
            await Ui.WaitInteractiveAsync(page);
            await Expect(page.Locator(".comment-body").First).ToHaveTextAsync("Komentarz po edycji");

            // Edit Post.
            await page.Locator(".post-actions-bar").GetByRole(AriaRole.Link, new() { Name = "Edytuj" }).ClickAsync();
            await Expect(Ui.Heading(page, "Edytuj post")).ToBeVisibleAsync();
            await page.Locator("#post-title").FillAsync(title + " (edytowany)");
            await page.GetByRole(AriaRole.Button, new() { Name = "Zapisz zmiany" }).ClickAsync();
            await Expect(page.Locator("h1#post-title")).ToHaveTextAsync(title + " (edytowany)");
            await Expect(page.Locator(".author-line .when")).ToContainTextAsync("edytowano");
            Assert.Equal(postUrl, page.Url);

            // Profile: post widoczny na publicznym profilu (przez menu użytkownika).
            await page.Locator(".menu-trigger").ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Mój profil" }).ClickAsync();
            await Expect(page.Locator("h1")).ToHaveTextAsync(session.User!.Alias);
            await Expect(page.Locator(".post-card-title", new() { HasText = title + " (edytowany)" })).ToBeVisibleAsync();

            // Logout → wejście do Saloonu jako gość (brak menu użytkownika, tabliczka "Zaloguj się").
            await page.Locator(".menu-trigger").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Wyloguj się" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/"));
            await Expect(page.Locator("nav.entrance-signs").GetByRole(AriaRole.Link, new() { Name = "Zaloguj się" })).ToBeVisibleAsync();
            await Expect(page.Locator(".menu-trigger")).ToHaveCountAsync(0);
            await page.GotoAsync("/me");
            await Expect(page).ToHaveURLAsync(new Regex("/login"));
        }
    }
}
