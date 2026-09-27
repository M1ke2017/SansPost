using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 17 — tablica Wanted w Main Hall 3D. Tablica jest globalna (ranking całego Saloonu), więc testy mają własną
    // instancję z czystą bazą i znanym rankingiem (bez treści startowych), wspólną dla klasy:
    //   #1 A "Most Wanted" 3 reakcje + 2 komentarze = 5 · #2 B 2 + 1 = 3 · #3 C 1 + 1 = 2 · #4 D 1 + 0 = 1 · #5 E 0,
    //   a do tego druga, najnowsza rozmowa A (0) — jeden autor = jeden plakat, więc nie trafia na tablicę.
    // Pusta tablica i pojedyncza rozmowa — osobna instancja. Przepływ: sala → Wanted → rozmowa → Wanted → sala,
    // adres /saloon?wanted=board | post&id=…, Escape i wstecz/dalej jak w BAR.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Wanted")]
    public class WantedBoardE2ETests
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

        private sealed record Seeded(SansPostServer Server, SansPostServer.TestUser A, SansPostServer.TestUser B, SansPostServer.TestUser C,
            SansPostServer.TestUser D, SansPostServer.TestUser E, int[] Posts, string[] Titles, int SecondPostOfA);

        private static readonly SemaphoreSlim SeedLock = new(1, 1);
        private static Seeded? _seeded;

        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public WantedBoardE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private async Task<Seeded> SeedAsync()
        {
            await SeedLock.WaitAsync();
            try
            {
                if (_seeded is not null)
                    return _seeded;

                var server = await _env.StartServerAsync("wanted", new Dictionary<string, string?>());
                var a = await server.CreateUserAsync();
                var b = await server.CreateUserAsync();
                var c = await server.CreateUserAsync();
                var d = await server.CreateUserAsync();
                var e = await server.CreateUserAsync();
                var fans = new[] { await server.CreateUserAsync(), await server.CreateUserAsync(), await server.CreateUserAsync() };
                var titles = new[]
                {
                    "Która rozmowa rozpaliła cały Saloon?",
                    "Najlepszy skrót klawiszowy tygodnia",
                    "Gra, do której wracam co roku",
                    "Mały pomysł na wielki projekt",
                    "Cicha rozmowa przy kominku"
                };
                var categories = new[] { "Ideas", "Technology", "Games", "Projects", "General" };
                var authors = new[] { a, b, c, d, e };
                var posts = new int[5];
                for (var i = 0; i < 5; i++)   // najwyżej w rankingu — najstarsza: kolejność wynika z zaangażowania, nie z wieku
                    posts[i] = await Api.CreatePostAsync(server, authors[i], titles[i], categories[i], $"Treść rozmowy numer {i + 1} na tablicy Wanted.");

                async Task LikeAsync(SansPostServer.TestUser fan, int postId)
                {
                    using var api = await server.CreateAuthenticatedApiClientAsync(fan.Email, E2EEnvironment.UserPassword);
                    (await api.PutAsync($"/api/posts/{postId}/like", null)).EnsureSuccessStatusCode();
                }

                foreach (var fan in fans)
                    await LikeAsync(fan, posts[0]);
                await Api.CommentAsync(server, b, posts[0], "Pierwszy komentarz pod Most Wanted.");
                await Api.CommentAsync(server, c, posts[0], "Drugi komentarz pod Most Wanted.");
                await LikeAsync(fans[0], posts[1]);
                await LikeAsync(fans[1], posts[1]);
                await Api.CommentAsync(server, a, posts[1], "Komentarz do skrótu.");
                await LikeAsync(fans[2], posts[2]);
                await Api.CommentAsync(server, a, posts[2], "Komentarz o grze.");
                await LikeAsync(fans[0], posts[3]);
                var secondOfA = await Api.CreatePostAsync(server, a, "Druga rozmowa autora Most Wanted", "General", "Nowsza, ale słabsza rozmowa tego samego autora.");

                return _seeded = new Seeded(server, a, b, c, d, e, posts, titles, secondOfA);
            }
            finally
            {
                SeedLock.Release();
            }
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].wanted-panel");
        private static ILocator WantedButton(IPage page) => page.Locator(".hall-zone[data-zone='wanted']");
        private static ILocator Poster(IPage page, int postId) => page.Locator($"dialog[open] [data-wanted-post='{postId}']");
        private static ILocator ConversationTitle(IPage page) => page.Locator("#bar-conversation-title");
        private static Task<int> Canvases(IPage page) => page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length");
        // Fokus w oknie albo poza stroną (Tab za ostatnim elementem okna modalnego trafia do UI przeglądarki) — nigdy w sali.
        private static Task<bool> FocusNotInBackground(IPage page) =>
            page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }");

        private async Task<IBrowserContext> NewContextAsync(SansPostServer server, int width = 1280, int height = 800)
        {
            var context = await _env.NewContextAsync(server, width, height);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
        }

        private static Task WaitCameraStillAsync(IPage page) =>
            page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");

        // Okno tablicy: kamera przy tablicy (strefa "wanted", scena nadal pod oknem), fokus na głównym plakacie.
        private static async Task ExpectBoardAsync(IPage page, Seeded seeded)
        {
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?wanted=board"));
            await Expect(Panel(page).Locator(".dialog-title")).ToHaveTextAsync("Tablica Wanted");
            await Expect(Poster(page, seeded.Posts[0])).ToBeVisibleAsync();
            Assert.Equal("wanted", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            Assert.Equal(1, await Canvases(page));
            Assert.Equal("/saloon", await page.EvaluateAsync<string>("() => location.pathname"));
        }

        private static async Task ExpectConversationAsync(IPage page, int postId, string title)
        {
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?wanted=post&id={postId}"));
            await Expect(ConversationTitle(page)).ToHaveTextAsync(title);
            await Expect(Panel(page).Locator(".wanted-board")).ToBeHiddenAsync();   // tablica czeka pod rozmową
            await Expect(page.Locator("dialog[open] .bar-menu")).ToHaveCountAsync(0);  // to nie jest BAR
            Assert.Equal(1, await Canvases(page));
        }

        // ---- 9, 10. Pasek stref → kamera przy tablicy → okno Wanted; ranking, prywatność, tablica 3D -------------------

        [Fact]
        public async Task HelperDock_OpensWanted_CameraAtBoard_RankedPosters_AndBoardInScene()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server, 1440, 900);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");

            // Tablica w scenie: przydomek Most Wanted na głównym plakacie 3D bez otwierania okna.
            await page.WaitForFunctionAsync("() => Array.isArray(window.__sansPostHall.wanted)");
            var inScene = await page.EvaluateAsync<string[]>("() => window.__sansPostHall.wanted");
            Assert.Equal(new[] { seeded.A.Alias, seeded.B.Alias, seeded.C.Alias, seeded.D.Alias, seeded.E.Alias }, inScene);

            await Expect(WantedButton(page)).ToHaveAttributeAsync("aria-haspopup", "dialog");
            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, seeded.Posts[0])).ToBeFocusedAsync();
            await WaitCameraStillAsync(page);
            Assert.NotEqual(mainZ, await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ"), 1);

            // Główny plakat: MOST WANTED, przydomek, tytuł, kategoria, czas, reakcje, komentarze, fragment.
            var main = Panel(page).Locator(".wanted-poster.is-featured");
            await Expect(main.Locator(".wanted-poster-kicker")).ToHaveTextAsync("Most Wanted");
            await Expect(main.Locator(".wanted-poster-alias")).ToHaveTextAsync(seeded.A.Alias);
            await Expect(main.Locator(".wanted-poster-reason")).ToHaveTextAsync("za rozmowę, która rozpaliła Saloon");
            await Expect(main.GetByRole(AriaRole.Heading, new() { Name = seeded.Titles[0], Level = 3 })).ToBeVisibleAsync();
            await Expect(main.Locator(".wanted-poster-preview")).ToHaveTextAsync("Treść rozmowy numer 1 na tablicy Wanted.");
            await Expect(main.Locator(".wanted-poster-meta")).ToContainTextAsync("Pomysły");
            // Liczniki #1 mogą urosnąć o komentarz z testu zalogowanego (kolejność testów dowolna) — dokładne liczby na #2.
            await Expect(main.Locator(".wanted-poster-meta")).ToContainTextAsync(new Regex(@"[3-9] reakcj"));
            await Expect(main.Locator(".wanted-poster-meta")).ToContainTextAsync(new Regex(@"\d+ komentarz"));
            await Expect(main.Locator("time")).ToHaveAttributeAsync("datetime", new Regex("Z$"));

            // Pozostali poszukiwani #2–#5 w kolejności rankingu; bez tabeli, bez danych prywatnych.
            var others = Panel(page).Locator(".wanted-list > li");
            await Expect(others).ToHaveCountAsync(4);
            await Expect(others.Locator(".wanted-rank")).ToHaveTextAsync(new[] { "Miejsce #2", "Miejsce #3", "Miejsce #4", "Miejsce #5" });
            await Expect(others.Locator(".wanted-open")).ToHaveTextAsync(seeded.Titles.Skip(1).ToArray());
            await Expect(others.Locator(".wanted-poster-alias")).ToHaveTextAsync(new[] { seeded.B.Alias, seeded.C.Alias, seeded.D.Alias, seeded.E.Alias });
            await Expect(Poster(page, seeded.SecondPostOfA)).ToHaveCountAsync(0);   // jeden autor = jeden plakat
            await Expect(others.Nth(0).Locator(".wanted-poster-meta")).ToContainTextAsync("2 reakcje");
            await Expect(others.Nth(0).Locator(".wanted-poster-meta")).ToContainTextAsync("1 komentarz");
            await Expect(others.Nth(0).Locator(".wanted-poster-meta")).ToContainTextAsync("Technologia");
            await Expect(Panel(page).Locator("table")).ToHaveCountAsync(0);
            var html = await Panel(page).InnerHTMLAsync();
            foreach (var user in new[] { seeded.A, seeded.B, seeded.C, seeded.D, seeded.E })
                Assert.DoesNotContain(user.Email, html);
            Assert.DoesNotContain("@example.test", html);

            // Desktop: okno z prawej, tablica 3D w lewej części kadru zostaje widoczna obok.
            var panel = (await Panel(page).BoundingBoxAsync())!;
            var board = await page.EvaluateAsync<double[]>("() => window.__sansPostHall.points.wanted");
            var camera = await page.EvaluateAsync<string>("() => { const d = window.__sansPostHall; return `area ${d.area}, moving ${d.moving}, camera ${d.cameraX.toFixed(2)} ${d.cameraY.toFixed(2)} ${d.cameraZ.toFixed(2)}, canvas ${document.querySelector('.saloon-hall canvas').clientWidth}x${document.querySelector('.saloon-hall canvas').clientHeight}`; }");
            Assert.True(panel.X > 1440 / 2, $"Okno tablicy z prawej (x {panel.X}).");
            Assert.True(board[0] > 0 && board[0] < panel.X, $"Tablica 3D w kadrze obok okna (x {board[0]}, okno od {panel.X}; {camera}).");
        }

        // ---- 9. Tablica w scenie (canvas) — ta sama ścieżka co pasek stref ------------------------------------------

        [Fact]
        public async Task BoardInScene_Click_OpensSameWantedFlow()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var point = await page.EvaluateAsync<double[]>("() => window.__sansPostHall.points.wanted");
            var canvas = (await page.Locator(".saloon-hall canvas").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync((float)(canvas.X + point[0]), (float)(canvas.Y + point[1]));
            await Expect(page.Locator("nav.hall-zones")).ToHaveAttributeAsync("data-hover", "wanted");
            await page.Mouse.ClickAsync((float)(canvas.X + point[0]), (float)(canvas.Y + point[1]));

            await ExpectBoardAsync(page, seeded);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?scene=3d"));
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // ---- 11, 12. Plakat → rozmowa (PostConversation w oknie) → powrót do tablicy, nie do BAR ----------------------

        [Fact]
        public async Task Poster_OpensConversationInWanted_BackReturnsToBoard_FocusOnPoster()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);

            await Poster(page, seeded.Posts[1]).ClickAsync();
            await ExpectConversationAsync(page, seeded.Posts[1], seeded.Titles[1]);
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();
            var conversation = Panel(page).Locator(".bar-conversation");
            await Expect(conversation.Locator(".author-line")).ToContainTextAsync(seeded.B.Alias);
            await Expect(conversation.Locator(".comment-body")).ToHaveTextAsync("Komentarz do skrótu.");
            await Expect(conversation.Locator(".like-button")).ToContainTextAsync("2");
            Assert.Equal("wanted", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));

            var back = conversation.Locator(".bar-back");
            await Expect(back).ToHaveTextAsync("Wróć do tablicy Wanted");
            await back.ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, seeded.Posts[1])).ToBeFocusedAsync();   // plakat, który otworzył rozmowę
            await Expect(page.Locator("dialog[open] .bar-menu")).ToHaveCountAsync(0);
        }

        // ---- 13, 19. Klawiatura: Tab / Shift+Tab / Enter / Spacja; Escape: rozmowa → tablica → sala ----------------

        [Fact]
        public async Task Keyboard_EnterSpaceTabs_EscapeHierarchy_FocusReturnsToWantedButton()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await page.Locator(".hall-zone[data-zone='bar']").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(WantedButton(page)).ToBeFocusedAsync();
            Assert.Equal("wanted", await page.EvaluateAsync<string>("() => window.__sansPostHall.hover"));
            await page.Keyboard.PressAsync("Enter");
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, seeded.Posts[0])).ToBeFocusedAsync();

            // Tab po plakatach w kolejności rankingu; Shift+Tab wraca; fokus nie wychodzi z okna.
            for (var i = 1; i < 5; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                await Expect(Poster(page, seeded.Posts[i])).ToBeFocusedAsync();
            }
            await page.Keyboard.PressAsync("Shift+Tab");
            await Expect(Poster(page, seeded.Posts[3])).ToBeFocusedAsync();
            for (var i = 0; i < 12; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await FocusNotInBackground(page), $"Tab #{i + 1} trafił do sali pod oknem tablicy.");
            }

            // Spacja na plakacie otwiera rozmowę; Escape → tablica (fokus na plakacie) → sala (fokus na "Tablica Wanted").
            await Poster(page, seeded.Posts[2]).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await ExpectConversationAsync(page, seeded.Posts[2], seeded.Titles[2]);
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, seeded.Posts[2])).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await ExpectConversationAsync(page, seeded.Posts[2], seeded.Titles[2]);
            await page.Keyboard.PressAsync("Escape");
            await Expect(Poster(page, seeded.Posts[2])).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(WantedButton(page)).ToBeFocusedAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?scene=3d"));
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        // ---- 14. Wstecz / dalej: sala ↔ tablica ↔ rozmowa, deterministycznie ----------------------------------------

        [Fact]
        public async Task BrowserBackForward_HallBoardConversation_Deterministic()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Poster(page, seeded.Posts[0]).ClickAsync();
            await ExpectConversationAsync(page, seeded.Posts[0], seeded.Titles[0]);

            async Task ExpectHallAsync()
            {
                await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?scene=3d"));
                await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
                await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            }

            for (var round = 0; round < 2; round++)
            {
                await page.GoBackAsync();
                await ExpectBoardAsync(page, seeded);
                await Expect(page.Locator(".bar-conversation")).ToHaveCountAsync(0);
                await page.GoBackAsync();
                await ExpectHallAsync();
                await page.GoForwardAsync();
                await ExpectBoardAsync(page, seeded);
                await WaitCameraStillAsync(page);
                await page.GoForwardAsync();
                await ExpectConversationAsync(page, seeded.Posts[0], seeded.Titles[0]);
            }
            Assert.Equal("wanted", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));

            // Udostępniony adres rozmowy z tablicy: sala od razu przy tablicy; w górę — tablica (bez historii w sesji).
            await OpenHallAsync(page, $"/saloon?wanted=post&id={seeded.Posts[3]}&scene=3d");
            await Expect(ConversationTitle(page)).ToHaveTextAsync(seeded.Titles[3]);
            await Expect(ConversationTitle(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await ExpectBoardAsync(page, seeded);
        }

        // ---- 15. Gość: czyta rozmowę z tablicy; logowanie w oknie i powrót do tej samej rozmowy ---------------------

        [Fact]
        public async Task Guest_ReadsConversation_LoginInsideWanted_ReturnsToConversation()
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await WantedButton(page).ClickAsync();
            await Poster(page, seeded.Posts[0]).ClickAsync();
            await ExpectConversationAsync(page, seeded.Posts[0], seeded.Titles[0]);

            var conversationUrl = $"/saloon?wanted=post&id={seeded.Posts[0]}";
            var panel = Panel(page);
            await Expect(panel.Locator(".comment-body").Nth(0)).ToHaveTextAsync("Pierwszy komentarz pod Most Wanted.");
            await Expect(panel.Locator(".comment-body").Nth(1)).ToHaveTextAsync("Drugi komentarz pod Most Wanted.");
            await Expect(panel.Locator("textarea")).ToHaveCountAsync(0);
            await Expect(panel.Locator(".like-button")).ToHaveAttributeAsync("href", $"/login?returnUrl={Uri.EscapeDataString(conversationUrl)}");

            await panel.GetByRole(AriaRole.Link, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
            await Expect(page.Locator("#bar-login-email")).ToBeFocusedAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"/saloon\?wanted=login&next="));
            await Expect(panel.Locator(".bar-auth .bar-back")).ToContainTextAsync("Wróć do rozmowy");
            await page.Keyboard.PressAsync("Escape");
            await ExpectConversationAsync(page, seeded.Posts[0], seeded.Titles[0]);

            await panel.GetByRole(AriaRole.Link, new() { Name = "Zaloguj się", Exact = true }).ClickAsync();
            await page.Locator("#bar-login-email").FillAsync(seeded.C.Email);
            await page.Locator("#bar-login-password").FillAsync(E2EEnvironment.UserPassword);
            await panel.Locator(".bar-auth .card-submit").ClickAsync();

            await Expect(page).ToHaveURLAsync(Ui.Path(conversationUrl), new() { Timeout = 15_000 });
            await Expect(ConversationTitle(page)).ToHaveTextAsync(seeded.Titles[0], new() { Timeout = 20_000 });
            await Expect(page.Locator("#bar-new-comment")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await ExpectBoardAsync(page, seeded);
        }

        // ---- 16. Zalogowany: reakcja i komentarz w rozmowie z tablicy; powrót — liczniki odświeżone ------------------

        [Fact]
        public async Task LoggedUser_ReactsAndComments_FromWanted_BoardRefreshesOnReturn()
        {
            var seeded = await SeedAsync();
            await using var session = await Session.UserAsync(_env, seeded.Server);
            await session.Context.AddInitScriptAsync(CapableGpu);
            var page = session.Page;
            await OpenHallAsync(page);
            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);
            var meta = Panel(page).Locator(".wanted-poster.is-featured .wanted-poster-meta");
            var before = await meta.InnerTextAsync();
            int Count(string text, string noun) => int.Parse(Regex.Match(text, $@"(\d+) {noun}").Groups[1].Value);

            await Poster(page, seeded.Posts[0]).ClickAsync();
            await ExpectConversationAsync(page, seeded.Posts[0], seeded.Titles[0]);
            var url = page.Url;
            var like = Panel(page).Locator(".bar-conversation .like-button");
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "false");
            await like.ClickAsync();
            await Expect(like).ToHaveAttributeAsync("aria-pressed", "true");
            await page.Locator("#bar-new-comment").FillAsync("Komentarz dopisany z tablicy Wanted.");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Opublikuj komentarz" }).ClickAsync();
            await Expect(Panel(page).Locator(".comment-body").Last).ToHaveTextAsync("Komentarz dopisany z tablicy Wanted.");
            Assert.Equal(url, page.Url);

            await page.Keyboard.PressAsync("Escape");
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, seeded.Posts[0])).ToBeFocusedAsync();
            await Expect(meta).ToContainTextAsync($"{Count(before, "reakcj") + 1} reakcj");
            await Expect(meta).ToContainTextAsync($"{Count(before, "komentarz") + 1} komentarz");
            await Expect(Poster(page, seeded.Posts[0])).ToBeFocusedAsync();

            // Sprzątanie: reakcja cofnięta (komentarz zostaje — #1 i tak pozostaje najwyżej).
            using var api = await seeded.Server.CreateAuthenticatedApiClientAsync(session.User!.Email, E2EEnvironment.UserPassword);
            (await api.DeleteAsync($"/api/posts/{seeded.Posts[0]}/like")).EnsureSuccessStatusCode();
        }

        // ---- 4. Ukrycie przez moderację: rozmowa znika z tablicy przy następnym otwarciu ----------------------------

        [Fact]
        public async Task HiddenByModeration_DisappearsFromBoard()
        {
            var seeded = await SeedAsync();
            var author = await seeded.Server.CreateUserAsync();
            var fan = await seeded.Server.CreateUserAsync();
            var title = "Gorąca rozmowa do ukrycia";
            var postId = await Api.CreatePostAsync(seeded.Server, author, title, "General");
            for (var i = 0; i < 12; i++)
                await Api.CommentAsync(seeded.Server, fan, postId, $"Komentarz {i}");

            await using var context = await NewContextAsync(seeded.Server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await WantedButton(page).ClickAsync();
            await Expect(Panel(page).Locator(".wanted-poster.is-featured .wanted-poster-alias")).ToHaveTextAsync(author.Alias);
            await Expect(Poster(page, postId)).ToHaveTextAsync(title);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await WaitCameraStillAsync(page);

            await Api.AdminAsync(seeded.Server, $"/api/moderation/posts/{postId}/hide");

            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Expect(Poster(page, postId)).ToHaveCountAsync(0);
            await Expect(Panel(page).Locator(".wanted-poster.is-featured .wanted-poster-alias")).ToHaveTextAsync(seeded.A.Alias);
            await page.WaitForFunctionAsync($"() => window.__sansPostHall.wanted[0] === {System.Text.Json.JsonSerializer.Serialize(seeded.A.Alias)}");
        }

        // ---- 17, 18. Mobile 390 / 360: pełnoszeroki arkusz, główny plakat pierwszy, przewijanie, cele ≥ 44 px --------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_FullWidthSheet_MainPosterFirst_ScrollAndTouchTargets(int width, int height)
        {
            var seeded = await SeedAsync();
            await using var context = await NewContextAsync(seeded.Server, width, height);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var dock = (await WantedButton(page).BoundingBoxAsync())!;
            Assert.True(dock.Height >= 44 && dock.Width >= 44, $"Przycisk tablicy w pasku stref: {dock.Width}x{dock.Height}.");
            await WantedButton(page).ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

            var sheet = (await Panel(page).BoundingBoxAsync())!;
            Assert.True(Math.Abs(sheet.X) < 0.5 && Math.Abs(sheet.Width - width) < 0.5, $"Arkusz na całą szerokość: {sheet.X} + {sheet.Width}.");
            var posters = Panel(page).Locator(".wanted-poster");
            await Expect(posters).ToHaveCountAsync(5);
            await Expect(posters.First).ToHaveClassAsync(new Regex("is-featured"));
            var first = (await posters.Nth(0).BoundingBoxAsync())!;
            var second = (await posters.Nth(1).BoundingBoxAsync())!;
            Assert.True(second.Y >= first.Y + first.Height, "Pozostali poszukiwani pod głównym plakatem, jedna kolumna.");
            Assert.True(first.X >= 0 && first.X + first.Width <= width, "Główny plakat mieści się w szerokości.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"tablica Wanted {width}");
            var small = await page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('dialog[open] button, dialog[open] a')].filter(e => e.getClientRects().length && e.getBoundingClientRect().height < 44).map(e => e.className)");
            Assert.True(small.Length == 0, "Cele dotyku < 44 px: " + string.Join(", ", small));

            // Naturalne przewijanie arkusza do ostatniego plakatu, rozmowa i powrót.
            var last = Poster(page, seeded.Posts[4]);
            await last.ScrollIntoViewIfNeededAsync();
            Assert.True(await Panel(page).EvaluateAsync<double>("d => d.scrollTop") > 0, "Arkusz przewija się w pionie.");
            await last.ClickAsync();
            await ExpectConversationAsync(page, seeded.Posts[4], seeded.Titles[4]);
            await Ui.AssertNoHorizontalOverflowAsync(page, $"rozmowa z tablicy {width}");
            await Panel(page).Locator(".bar-conversation .bar-back").ClickAsync();
            await ExpectBoardAsync(page, seeded);
            await Expect(last).ToBeFocusedAsync();
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(WantedButton(page)).ToBeVisibleAsync();
        }

        // ---- 20. axe: tablica i rozmowa z tablicy — 0 critical / 0 serious ------------------------------------------

        [Fact]
        public async Task Axe_BoardAndConversation_NoCriticalOrSerious()
        {
            var seeded = await SeedAsync();
            var blocking = new List<string>();
            foreach (var (scheme, width, height) in new[] { (ColorScheme.Light, 1280, 800), (ColorScheme.Dark, 1280, 800), (ColorScheme.Light, 390, 844) })
            {
                await using var context = await _env.NewContextAsync(seeded.Server, width, height, scheme);
                await context.AddInitScriptAsync(CapableGpu);
                var page = await context.NewPageAsync();
                await OpenHallAsync(page);
                await WantedButton(page).ClickAsync();
                await Expect(Poster(page, seeded.Posts[0])).ToBeFocusedAsync();
                await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

                async Task AuditAsync(string name)
                {
                    var result = await page.RunAxe();
                    foreach (var violation in result.Violations)
                    {
                        var line = $"{name}: [{violation.Impact}] {violation.Id} — {violation.Help} ({string.Join(" | ", violation.Nodes.Take(3).Select(n => string.Join(" ", n.Target)))})";
                        _output.WriteLine(line);
                        if (violation.Impact is "critical" or "serious")
                            blocking.Add(line);
                    }
                }

                await AuditAsync($"Wanted {scheme} {width}");
                await page.Keyboard.PressAsync("Enter");
                await Expect(ConversationTitle(page)).ToBeFocusedAsync();
                await AuditAsync($"Wanted → rozmowa {scheme} {width}");
            }

            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));
        }

        // ---- 7, 8. Pusta tablica, potem jedna rozmowa (osobna instancja, bez treści startowych) ---------------------

        [Fact]
        public async Task EmptyBoard_ThenSingleConversation_OneMainPoster()
        {
            var server = await _env.StartServerAsync("wanted-empty", new Dictionary<string, string?>());
            await using var context = await NewContextAsync(server);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await page.WaitForFunctionAsync("() => Array.isArray(window.__sansPostHall.wanted)");
            Assert.Empty(await page.EvaluateAsync<string[]>("() => window.__sansPostHall.wanted"));

            await WantedButton(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?wanted=board"));
            await Expect(Panel(page).Locator(".wanted-empty-title")).ToHaveTextAsync("Tablica czeka na pierwszą rozmowę.");
            await Expect(Panel(page).Locator(".wanted-poster")).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await WaitCameraStillAsync(page);

            var author = await server.CreateUserAsync();
            var postId = await Api.CreatePostAsync(server, author, "Pierwsza rozmowa w Saloonie", "General");

            await WantedButton(page).ClickAsync();
            await Expect(Panel(page).Locator(".wanted-poster")).ToHaveCountAsync(1);
            await Expect(Panel(page).Locator(".wanted-poster.is-featured .wanted-poster-alias")).ToHaveTextAsync(author.Alias);
            await Expect(Panel(page).Locator($"[data-wanted-post='{postId}']")).ToBeFocusedAsync();
            await Expect(Panel(page).Locator(".wanted-others")).ToHaveCountAsync(0);
            await Expect(Panel(page).Locator(".wanted-poster-meta")).ToContainTextAsync("0 reakcji");
            await page.WaitForFunctionAsync($"() => window.__sansPostHall.wanted.length === 1");
        }
    }
}
