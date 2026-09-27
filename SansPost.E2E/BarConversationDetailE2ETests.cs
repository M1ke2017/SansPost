using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 16B — rozmowa w BAR (Conversation Detail): kliknięcie rozmowy na liście nie opuszcza Main Hall 3D —
    // post, reakcje i dyskusja otwierają się w oknie BAR (/saloon?bar=post&id=…&from=…), a powrót prowadzi dokładnie do
    // listy, z której rozmowę otwarto. Klasyczny /post-view/{id} zostaje dla linków z zewnątrz.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "BarConversation")]
    public class BarConversationDetailE2ETests
    {
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

        public BarConversationDetailE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].bar-panel");
        private static ILocator Item(IPage page, string id) => page.Locator($"dialog[open] [data-bar-item='{id}']");
        private static ILocator LevelTitle(IPage page) => page.Locator("#bar-level-title");
        private static ILocator ConversationTitle(IPage page) => page.Locator("#bar-conversation-title");
        private static ILocator BackToList(IPage page) => Panel(page).Locator(".bar-conversation > .bar-back");
        private static ILocator CardLink(IPage page, int postId) => page.Locator($"#post-{postId}-title a");
        private static ILocator BarButton(IPage page) => page.Locator(".hall-zone[data-zone='bar']");
        private static Task<int> Canvases(IPage page) => page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length");
        private static Task<int> Frames(IPage page) => page.EvaluateAsync<int>("() => window.__sansPostHall.frames");
        private static Task<bool> FocusNotInBackground(IPage page) =>
            page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }");

        private static string Word() => "rozm" + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(8).ToArray()) + "x";

        private async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800)
        {
            var context = await _env.NewContextAsync(width: width, height: height);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
        }

        private static async Task OpenBarAsync(IPage page)
        {
            await BarButton(page).ClickAsync();
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
        }

        // Rozmowa z karty na liście: nadal /saloon (sala 3D, jeden canvas), okno BAR z tytułem rozmowy (fokus na nim).
        private static async Task OpenConversationAsync(IPage page, int postId, string title)
        {
            await CardLink(page, postId).ClickAsync();
            await Expect(ConversationTitle(page)).ToHaveTextAsync(title);
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();
            await Expect(page).ToHaveURLAsync(new Regex($@"/saloon\?bar=post&id={postId}&from="));
            Assert.Equal("/saloon", await page.EvaluateAsync<string>("() => location.pathname"));
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1);
            Assert.Equal(1, await Canvases(page));
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
        }

        private static Task WaitSearchSettledAsync(IPage page) =>
            page.WaitForFunctionAsync("() => { const h = document.querySelector('#bar-search-hint'); return h && !h.querySelector('.spinner') && document.querySelector('dialog[open] .search-results'); }");

        // ---- 1, 5, 6, 7. Popularne → rozmowa w BAR → ta sama lista ---------------------------------------------

        [Fact]
        public async Task Popular_OpensConversationInsideBar_AndReturnsToSameList()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Popularna rozmowa {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Ideas", "Pełna treść rozmowy przy barze.\nDruga linia treści.");
            var fan = await _env.Main.CreateUserAsync();
            await Api.CommentAsync(_env.Main, fan, postId, "Pierwszy komentarz przy barze.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "popular").ClickAsync();
            await Expect(CardLink(page, postId)).ToBeVisibleAsync();
            await page.EvaluateAsync("() => document.querySelector('dialog[open] .bar-list').__spMarker = 'ta sama lista'");

            await OpenConversationAsync(page, postId, title);
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=popular"));
            var panel = Panel(page);
            await Expect(panel.Locator(".bar-conversation .category-badge")).ToContainTextAsync("Pomysły");
            await Expect(panel.Locator(".bar-conversation .author-line")).ToContainTextAsync(author.Alias);
            await Expect(panel.Locator(".bar-conversation .author-line time")).ToHaveAttributeAsync("datetime", new Regex("Z$"));
            await Expect(panel.Locator(".bar-conversation .prose-plain")).ToContainTextAsync("Druga linia treści.");   // pełna treść
            await Expect(panel.Locator("#bar-comments-title")).ToHaveTextAsync("Komentarze · 1");
            await Expect(panel.Locator(".bar-conversation .comment-body")).ToHaveTextAsync("Pierwszy komentarz przy barze.");
            await Expect(page.Locator(".bar-list")).ToBeHiddenAsync();   // lista czeka pod rozmową

            await BackToList(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=popular"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Popularne rozmowy");
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();   // fokus wraca na kartę, która otworzyła rozmowę
            Assert.Equal("ta sama lista", await page.EvaluateAsync<string?>("() => document.querySelector('dialog[open] .bar-list').__spMarker"));
            Assert.Equal(1, await Canvases(page));
        }

        // ---- 2. Temat → rozmowa → ten sam temat i sortowanie ----------------------------------------------------

        [Fact]
        public async Task Topic_Conversation_BackToSameTopicAndSort()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa o grach {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Games");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "topic-games").ClickAsync();
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=games&sort=popular"));
            await Expect(CardLink(page, postId)).ToBeVisibleAsync();

            await OpenConversationAsync(page, postId, title);
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=topic&category=games&sort=popular"));

            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=games&sort=popular"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Gry");
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();
        }

        // ---- 3, 4. Wyszukiwanie (+ temat) → rozmowa → dokładnie te same wyniki i filtr ---------------------------

        [Fact]
        public async Task SearchInTopic_Conversation_BackToExactResultsAndFilter()
        {
            var word = Word();
            var author = await _env.Main.CreateUserAsync();
            var title = $"Technika {word}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Technology", $"Słowo {word} w technologii.");
            await Api.CreatePostAsync(_env.Main, author, $"Podróż {word}", "Travel", $"Słowo {word} w podróżach.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            // Samo wyszukiwanie: wyniki → rozmowa → te same wyniki.
            await page.Locator("#bar-search-input").FillAsync(word);
            await page.Locator("#bar-search-input").PressAsync("Enter");
            await WaitSearchSettledAsync(page);
            await Expect(Panel(page).Locator(".search-results .post-card")).ToHaveCountAsync(2);
            await OpenConversationAsync(page, postId, title);
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=search&q={word}"));
            await BackToList(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&q={word}"));
            await Expect(Panel(page).Locator(".search-results .post-card")).ToHaveCountAsync(2);
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();

            // Z filtrem tematu: dokładnie ten sam filtr i fraza po powrocie.
            await Panel(page).GetByLabel("Temat").SelectOptionAsync("technology");
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&category=technology&q={word}"));
            await Expect(Panel(page).Locator(".search-results .post-card")).ToHaveCountAsync(1);
            await OpenConversationAsync(page, postId, title);
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=search&category=technology&q={word}"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&category=technology&q={word}"));
            await Expect(Panel(page).GetByLabel("Temat")).ToHaveValueAsync("technology");
            await Expect(page.Locator("#bar-search-input")).ToHaveValueAsync(word);
            await Expect(Panel(page).Locator(".search-results .post-card")).ToHaveCountAsync(1);
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();
        }

        // ---- 8, 9, 10, 22. Escape: rozmowa → lista → Karta → sala; fokus wraca na element, który otworzył poziom ---

        [Fact]
        public async Task EscapeHierarchy_ConversationListMenuHall_WithFocusReturn()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa na Escape {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "General");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await OpenConversationAsync(page, postId, title);

            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=newest"));
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(BarButton(page)).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // ---- 11, 12. Wstecz / dalej: deterministyczne poziomy (Menu → Popularne → Post i z powrotem) -------------

        [Fact]
        public async Task BrowserBackForward_MenuPopularPost_Deterministic()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa na wstecz {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Ideas");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "popular").ClickAsync();
            await Expect(CardLink(page, postId)).ToBeVisibleAsync();
            await OpenConversationAsync(page, postId, title);

            async Task ExpectPopularAsync()
            {
                await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=popular"));
                await Expect(LevelTitle(page)).ToHaveTextAsync("Popularne rozmowy");
                await Expect(page.Locator(".bar-conversation")).ToHaveCountAsync(0);
                await Expect(CardLink(page, postId)).ToBeVisibleAsync();
            }

            async Task ExpectPostAsync()
            {
                await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=popular"));
                await Expect(ConversationTitle(page)).ToHaveTextAsync(title);
                await Expect(page.Locator(".bar-list")).ToBeHiddenAsync();
            }

            async Task ExpectMenuAsync()
            {
                await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
                await Expect(Item(page, "popular")).ToBeVisibleAsync();
                await Expect(page.Locator(".bar-conversation")).ToHaveCountAsync(0);
            }

            await page.GoBackAsync();
            await ExpectPopularAsync();
            await page.GoBackAsync();
            await ExpectMenuAsync();
            await page.GoForwardAsync();
            await ExpectPopularAsync();
            await page.GoForwardAsync();
            await ExpectPostAsync();

            // I jeszcze raz w dół do sali i z powrotem — bez losowych stanów okna i kamery.
            await page.GoBackAsync();
            await ExpectPopularAsync();
            await page.GoBackAsync();
            await ExpectMenuAsync();
            await page.GoBackAsync();
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            await page.GoForwardAsync();
            await ExpectMenuAsync();
            await page.GoForwardAsync();
            await ExpectPopularAsync();
            await page.GoForwardAsync();
            await ExpectPostAsync();
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            Assert.Equal(1, await Canvases(page));
        }

        // ---- 13. Bezpośredni /post-view/{id} i link do rozmowy w BAR ---------------------------------------------

        [Fact]
        public async Task DirectPostView_StaysClassic_AndBarConversationLinkOpensInHall()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa z linku {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Travel");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            await Expect(Ui.Heading(page, title)).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
            await Expect(page.Locator(".back-link")).ToHaveTextAsync("Wróć do odkrywania");
            await Expect(page.Locator("#comments")).ToBeVisibleAsync();
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
            await Expect(page).ToHaveTitleAsync($"{title} · SansPost");

            // Udostępniony adres rozmowy w BAR: sala od razu przy barze, rozmowa otwarta; w górę → jej lista (bez historii).
            await OpenHallAsync(page, $"/saloon?bar=post&id={postId}&from=topic&category=travel&scene=3d");
            await Expect(ConversationTitle(page)).ToHaveTextAsync(title);
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=travel"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Podróże");

            // Rozmowa niedostępna (nieistniejące id) — komunikat w oknie i powrót do listy, bez wyjścia z sali.
            await OpenHallAsync(page, "/saloon?bar=post&id=999999999&from=newest&scene=3d");
            await Expect(Panel(page).GetByText("Ten post nie jest dostępny")).ToBeVisibleAsync();
            await Panel(page).Locator(".bar-conversation .state .btn").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=newest"));
        }

        // ---- 14. Gość ----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Guest_ReadsConversationAndComments_LoginReturnsToBarConversation()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa dla gościa {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "General");
            await Api.CommentAsync(_env.Main, author, postId, "Komentarz widoczny dla gościa.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await OpenConversationAsync(page, postId, title);

            var panel = Panel(page);
            var conversationUrl = $"/saloon?bar=post&id={postId}&from=newest";
            await Expect(panel.Locator(".comment-body")).ToHaveTextAsync("Komentarz widoczny dla gościa.");
            await Expect(panel.Locator("textarea")).ToHaveCountAsync(0);
            await Expect(panel.GetByRole(AriaRole.Link, new() { Name = "Zaloguj się", Exact = true })).ToHaveAttributeAsync("href", $"/login?returnUrl={Uri.EscapeDataString(conversationUrl)}");
            await Expect(panel.Locator(".like-button")).ToHaveAttributeAsync("href", $"/login?returnUrl={Uri.EscapeDataString(conversationUrl)}");
            await Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Zgłoś" })).ToHaveCountAsync(0);
        }

        // ---- 15–18. Zalogowany: reakcja, komentarz (zostaje w rozmowie), powiadomienie, Escape przy pisaniu -------

        [Fact]
        public async Task LoggedUser_ReactsAndComments_StaysInConversation_NotifiesAuthor()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa z reakcją {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Projects");

            await using var session = await Session.UserAsync(_env);
            await session.Context.AddInitScriptAsync(CapableGpu);
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await OpenConversationAsync(page, postId, title);
            var panel = Panel(page);
            var url = page.Url;

            // Reakcja: istniejący LikeButton (PUT/DELETE) — bez zamykania BAR i bez przeładowania.
            var like = panel.Locator(".bar-conversation .like-button");
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "false");
            await like.ClickAsync();
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(like).ToContainTextAsync("1");
            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(author.Email, E2EEnvironment.UserPassword))
            {
                var likes = await api.GetStringAsync($"/api/posts/{postId}/likes");
                Assert.Contains("\"likeCount\":1", likes.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
            }

            // Escape w polu z pisanym tekstem nie cofa poziomu (tekst nie ginie).
            var textarea = panel.Locator("#bar-new-comment");
            await textarea.FillAsync("Komentarz dodany przy barze.");
            await textarea.PressAsync("Escape");
            await Expect(ConversationTitle(page)).ToBeVisibleAsync();
            await Expect(textarea).ToHaveValueAsync("Komentarz dodany przy barze.");

            await panel.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(panel.Locator(".comment-body").Last).ToHaveTextAsync("Komentarz dodany przy barze.");
            await Expect(panel.Locator("#bar-comments-title")).ToHaveTextAsync("Komentarze · 1");
            await Expect(textarea).ToHaveValueAsync("");
            Assert.Equal(url, page.Url);   // nadal ta sama rozmowa w sali
            Assert.Equal(1, await Canvases(page));

            // Powiadomienie autora o komentarzu — istniejący NotificationService.
            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(author.Email, E2EEnvironment.UserPassword))
            {
                var json = await api.GetStringAsync("/api/notifications");
                Assert.Contains($"\"postId\":{postId}", json.Replace(" ", ""));
                Assert.Contains(session.User!.Alias, json);
            }

            // Zgłoszenie w oknie zagnieżdżonym: Escape zamyka zgłoszenie, rozmowa zostaje.
            await panel.GetByRole(AriaRole.Button, new() { Name = "Zgłoś" }).First.ClickAsync();
            await Expect(page.Locator("dialog[open]:not(.bar-panel)")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]:not(.bar-panel)")).ToHaveCountAsync(0);
            await Expect(ConversationTitle(page)).ToBeVisibleAsync();
            Assert.Equal(url, page.Url);
        }

        // ---- 19, 20. Mobile 390 / 360 ------------------------------------------------------------------------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_ConversationIsFullWidthSheet_Readable(int width, int height)
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa na telefonie z dość długim tytułem, który musi się zawinąć {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Travel", string.Join("\n\n", Enumerable.Repeat("Akapit rozmowy o podróżach, który na telefonie zajmuje kilka linii.", 8)));
            for (var i = 0; i < 4; i++)
                await Api.CommentAsync(_env.Main, author, postId, $"Komentarz numer {i + 1} czytelny na telefonie.");

            await using var session = await Session.UserAsync(_env, width: width, height: height);
            await session.Context.AddInitScriptAsync(CapableGpu);
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await OpenConversationAsync(page, postId, title);
            var panel = Panel(page);
            await panel.EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

            var box = (await panel.BoundingBoxAsync())!;
            Assert.True(Math.Abs(box.X) < 0.5 && Math.Abs(box.Width - width) < 0.5 && box.Height <= height + 0.5, "Rozmowa na pełnym arkuszu.");
            Assert.False(await panel.EvaluateAsync<bool>("d => [...d.querySelectorAll('*')].some(e => e.getBoundingClientRect().right > innerWidth + 0.5)"), "Element wychodzi poza ekran.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"rozmowa {width}");
            Assert.True(await panel.EvaluateAsync<bool>("d => d.scrollHeight > d.clientHeight"), "Rozmowa przewija się pionowo.");

            foreach (var (name, locator) in new[] {
                ("powrót", BackToList(page)), ("zamknięcie", panel.GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" })),
                ("reakcja", panel.Locator(".bar-conversation .like-button")), ("wyślij", panel.GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" })) })
            {
                await locator.ScrollIntoViewIfNeededAsync();
                var target = (await locator.BoundingBoxAsync())!;
                Assert.True(target.Height >= 44 && target.Width >= 44, $"{name}: cel dotyku {target.Width}x{target.Height}.");
            }

            // Zamknięcie i powrót dostępne także po przewinięciu na dół (przyklejony nagłówek okna).
            await panel.EvaluateAsync("d => d.scrollTop = d.scrollHeight");
            var close = (await panel.GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).BoundingBoxAsync())!;
            Assert.True(close.Y >= 0 && close.Y + close.Height <= height, "Zamknięcie widoczne po przewinięciu.");
            Assert.True(await page.EvaluateAsync<double>("() => parseFloat(getComputedStyle(document.querySelector('#bar-conversation-title')).fontSize)") >= 20, "Tytuł czytelny.");
            await Expect(panel.Locator(".comment-body")).ToHaveCountAsync(4);
        }

        // ---- 21. Klawiatura ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Keyboard_ListToConversationAndBack_FocusTrappedInBar()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa z klawiatury {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Ideas");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await BarButton(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Space");
            await Expect(LevelTitle(page)).ToHaveTextAsync("Najnowsze rozmowy");
            await Expect(CardLink(page, postId)).ToBeVisibleAsync();

            await CardLink(page, postId).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();

            for (var i = 0; i < 20; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await FocusNotInBackground(page), $"Tab #{i + 1} trafił do sali pod oknem.");
            }
            for (var i = 0; i < 20; i++)
            {
                await page.Keyboard.PressAsync("Shift+Tab");
                Assert.True(await FocusNotInBackground(page), $"Shift+Tab #{i + 1} trafił do sali pod oknem.");
            }

            await BackToList(page).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=newest"));
            await Expect(CardLink(page, postId)).ToBeFocusedAsync();
        }

        // ---- 23. Reduced motion ------------------------------------------------------------------------------------

        [Fact]
        public async Task ReducedMotion_ConversationOpensWithoutAnimation()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa bez animacji {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "General");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await OpenConversationAsync(page, postId, title);
            Assert.Equal("none", await Panel(page).EvaluateAsync<string>("d => getComputedStyle(d).animationName"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => document.querySelector('dialog[open]').getAnimations({ subtree: true }).filter(a => a.playState === 'running').length"));
        }

        // ---- 24. Wielokrotne otwarcie/zamknięcie rozmowy: DOM, canvas, historia, sala w spoczynku --------------------

        [Fact]
        public async Task RepeatedConversation_ListPostList_StableDomCanvasHistoryAndIdleHall()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa w pętli {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "General");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            var cdp = await context.NewCDPSessionAsync(page);
            async Task<int> NodesAsync()
            {
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                return (await cdp.SendAsync("Memory.getDOMCounters"))!.Value.GetProperty("nodes").GetInt32();
            }

            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await Expect(CardLink(page, postId)).ToBeVisibleAsync();
            int? length = null;
            var nodes = new List<int>();

            for (var round = 0; round < 10; round++)
            {
                await CardLink(page, postId).ClickAsync();
                await Expect(ConversationTitle(page)).ToHaveTextAsync(title);
                var frames = await Frames(page);
                await page.WaitForTimeoutAsync(400);
                Assert.Equal(frames, await Frames(page));   // czytanie nie budzi renderu sali

                await page.Keyboard.PressAsync("Escape");
                await Expect(CardLink(page, postId)).ToBeFocusedAsync();
                Assert.Equal(1, await Canvases(page));

                nodes.Add(await NodesAsync());
                var historyLength = await page.EvaluateAsync<int>("() => history.length");
                length ??= historyLength;
                Assert.Equal(length, historyLength);   // rozmowa → powrót nie mnoży wpisów historii
            }

            // Trend, nie pojedyncza próbka: przeciek okna rozmowy (~280 węzłów na cykl) dałby w drugiej połowie ≥ 1400 węzłów
            // przyrostu; jednorazowe wahania (GC, animacja listy) mieszczą się w kilkudziesięciu.
            Assert.True(nodes[^1] - nodes[4] < 150, $"Węzły DOM rosną: {string.Join(" → ", nodes)}.");
        }
    }
}
