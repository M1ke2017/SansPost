using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Regresja (Sprint 9 P2, naprawione w Sprincie 10): ServerPrerendered — przy podłączeniu circuitu Blazor zastępuje
    // prerenderowany DOM, więc tekst wpisany wcześniej przepadał. Teraz formularze są zablokowane (FormGate) do chwili
    // podłączenia. Test deterministycznie wydłuża okno prerenderu (opóźniony blazor.server.js), próbuje pisać od razu
    // i sprawdza, że po podłączeniu wpisane dane nadal są w polach.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    public class HydrationE2ETests
    {
        private readonly E2EEnvironment _env;

        public HydrationE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static async Task DelayCircuitAsync(IPage page)
        {
            await page.RouteAsync("**/_framework/blazor.server.js", async route =>
            {
                await Task.Delay(1500);
                await route.ContinueAsync();
            });
        }

        // Pisanie zaraz po wejściu: w prerenderze pola są zablokowane, Playwright czeka na ich odblokowanie
        // (jak człowiek, który nie może pisać w nieaktywne pole) — po podłączeniu wartość zostaje.
        private static async Task TypeDuringPrerenderAsync(IPage page, string path, params (string Selector, string Value)[] fields)
        {
            await DelayCircuitAsync(page);
            await page.GotoAsync(path, new() { WaitUntil = WaitUntilState.Commit });
            await page.Locator(fields[0].Selector).WaitForAsync(new() { State = WaitForSelectorState.Attached });

            Assert.Equal(0, await page.Locator("html[data-interactive]").CountAsync());
            await Expect(page.Locator(fields[0].Selector)).ToBeDisabledAsync();
            await Expect(page.Locator("fieldset.form-gate").First).ToHaveAttributeAsync("aria-busy", "true");

            foreach (var (selector, value) in fields)
                await page.Locator(selector).FillAsync(value);

            await Ui.WaitInteractiveAsync(page);
            foreach (var (selector, value) in fields)
                await Expect(page.Locator(selector)).ToHaveValueAsync(value);
            await Expect(page.Locator("fieldset.form-gate").First).ToHaveAttributeAsync("aria-busy", "false");
        }

        [Fact]
        public async Task LoginAndRegister_TextTypedBeforeCircuitConnects_IsNotLost()
        {
            await using var context = await _env.NewContextAsync();

            var login = await context.NewPageAsync();
            await TypeDuringPrerenderAsync(login, "/login", ("#email", "wczesny@example.test"), ("#password", "haslo-wpisane-wczesnie"));

            var register = await context.NewPageAsync();
            await TypeDuringPrerenderAsync(register, "/register", ("#email", "rejestracja@example.test"), ("#password", "haslo-wpisane-wczesnie"));
        }

        [Fact]
        public async Task PostAndCommentEditors_TextTypedBeforeCircuitConnects_IsNotLost()
        {
            await using var user = await Session.UserAsync(_env);
            var author = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, author, "Post do testu hydratacji");

            var create = await user.Context.NewPageAsync();
            await TypeDuringPrerenderAsync(create, "/new", ("#post-title", "Tytuł wpisany wcześnie"), ("#post-content", "Treść wpisana wcześnie."));

            var edit = await user.Context.NewPageAsync();
            var ownPostId = await Api.CreatePostAsync(_env.Main, user.User!, "Mój post do edycji");
            await TypeDuringPrerenderAsync(edit, $"/edit-post/{ownPostId}", ("#post-title", "Nowy tytuł wpisany wcześnie"));

            var comment = await user.Context.NewPageAsync();
            await TypeDuringPrerenderAsync(comment, $"/post-view/{postId}", ("#new-comment", "Komentarz wpisany wcześnie"));

            // Zachowana wartość jest tą, którą zna serwer: publikacja komentarza wysyła wpisany tekst.
            await comment.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(comment.Locator(".comment", new() { HasText = "Komentarz wpisany wcześnie" })).ToBeVisibleAsync();
        }
    }
}
