using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Dwie karty tego samego autora edytują tę samą wersję: druga dostaje produktowy konflikt (412 pod spodem),
    // pierwsza zmiana nie zostaje nadpisana.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class ConcurrencyUxE2ETests
    {
        private readonly E2EEnvironment _env;

        public ConcurrencyUxE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private async Task<IPage> SecondTabAsync(Session session)
        {
            // Osobny kontekst = osobne cookie i circuit (jak druga przeglądarka).
            var context = await _env.NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.LoginAsync(page, session.User!.Email, E2EEnvironment.UserPassword);
            return page;
        }

        [Fact]
        public async Task PostEdit_StaleVersion_ShowsProductConflict_AndDoesNotOverwrite()
        {
            await using var session = await Session.UserAsync(_env);
            var postId = await Api.CreatePostAsync(_env.Main, session.User!, "Wersja pierwsza");
            var tabA = session.Page;
            var tabB = await SecondTabAsync(session);

            await Ui.GotoAsync(tabA, $"/edit-post/{postId}");
            await Ui.GotoAsync(tabB, $"/edit-post/{postId}");

            await tabA.Locator("#post-title").FillAsync("Zmiana z karty A");
            await tabA.GetByRole(AriaRole.Button, new() { Name = "Zapisz zmiany" }).ClickAsync();
            await Expect(tabA.Locator("h1#post-title")).ToHaveTextAsync("Zmiana z karty A");

            await tabB.Locator("#post-title").FillAsync("Zmiana z karty B");
            await tabB.GetByRole(AriaRole.Button, new() { Name = "Zapisz zmiany" }).ClickAsync();
            var alert = tabB.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Post zmienił się od czasu otwarcia edytora" });
            await Expect(alert).ToBeVisibleAsync();
            var text = await tabB.Locator("main").InnerTextAsync();
            Assert.DoesNotMatch(new Regex("412|Precondition", RegexOptions.IgnoreCase), text);

            // Twoja wersja do skopiowania + wczytanie aktualnej.
            await Expect(tabB.GetByText("Twoja niezapisana wersja")).ToBeVisibleAsync();
            await tabB.GetByRole(AriaRole.Button, new() { Name = "Wczytaj aktualną wersję" }).ClickAsync();
            await Expect(tabB.Locator("#post-title")).ToHaveValueAsync("Zmiana z karty A");

            await Ui.GotoAsync(tabB, $"/post-view/{postId}");
            await Expect(tabB.Locator("h1#post-title")).ToHaveTextAsync("Zmiana z karty A");
            await tabB.Context.DisposeAsync();
        }

        [Fact]
        public async Task CommentEdit_StaleVersion_ShowsProductConflict_AndDoesNotOverwrite()
        {
            await using var session = await Session.UserAsync(_env);
            var postId = await Api.CreatePostAsync(_env.Main, session.User!, "Post z komentarzem");
            await Api.CommentAsync(_env.Main, session.User!, postId, "Komentarz oryginalny");
            var tabA = session.Page;
            var tabB = await SecondTabAsync(session);

            foreach (var tab in new[] { tabA, tabB })
            {
                await Ui.GotoAsync(tab, $"/post-view/{postId}");
                await tab.Locator(".comment").GetByRole(AriaRole.Button, new() { Name = "Edytuj" }).ClickAsync();
            }

            await tabA.Locator("textarea[id^=edit-comment-]").FillAsync("Edycja z karty A");
            await tabA.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
            await Expect(tabA.Locator(".comment-body")).ToHaveTextAsync("Edycja z karty A");

            await tabB.Locator("textarea[id^=edit-comment-]").FillAsync("Edycja z karty B");
            await tabB.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
            await Expect(tabB.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Komentarz zmienił się w międzyczasie" })).ToBeVisibleAsync();
            Assert.DoesNotMatch(new Regex("412|Precondition", RegexOptions.IgnoreCase), await tabB.Locator("main").InnerTextAsync());

            await tabB.GetByRole(AriaRole.Button, new() { Name = "Wczytaj aktualną wersję" }).ClickAsync();
            await Expect(tabB.Locator(".comment-body")).ToHaveTextAsync("Edycja z karty A");
            await tabB.Context.DisposeAsync();
        }
    }
}
