using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 16 — BAR w Main Hall 3D: Karta rozmów (menu) → lista (Najnowsze / Popularne / Wszystkie / temat) albo wyniki
    // wyszukiwania (PostgreSQL FTS) → post. Stan w adresie (/saloon?bar=…), poziomy jako wpisy historii: Escape i "wstecz"
    // cofają o poziom, zamknięcie wraca do sali. Chromium headless ma WebGL programowy — sala jest wymuszona przez
    // "?scene=3d" (pierwsze wejście) i symulację sprzętowego GPU (kolejne wejścia bez parametru, np. powrót z posta).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Bar")]
    public class BarConversationsE2ETests
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

        private static readonly string[] Topics = { "Ogólne", "Technologia", "Gry", "Podróże", "Pomysły", "Projekty" };

        private readonly E2EEnvironment _env;

        public BarConversationsE2ETests(E2EEnvironment env)
        {
            _env = env;
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open]");
        private static ILocator Item(IPage page, string id) => page.Locator($"dialog[open] [data-bar-item='{id}']");
        private static ILocator LevelTitle(IPage page) => page.Locator("#bar-level-title");
        private static ILocator Cards(IPage page) => page.Locator("dialog[open] .post-card");
        private static ILocator SearchInput(IPage page) => page.Locator("#bar-search-input");
        private static ILocator BarButton(IPage page) => page.Locator(".hall-zone[data-zone='bar']");
        private static Task<bool> FocusInPanel(IPage page) => page.EvaluateAsync<bool>("() => !!document.activeElement?.closest('dialog[open]')");

        // Pułapka fokusu natywnego okna modalnego: Tab za ostatnim elementem przechodzi do interfejsu przeglądarki
        // (document.activeElement = body), nigdy do treści sali pod oknem (inert).
        private static Task<bool> FocusNotInBackground(IPage page) =>
            page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }");
        private static Task<int> Frames(IPage page) => page.EvaluateAsync<int>("() => window.__sansPostHall.frames");

        private static string Word() => "bar" + new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(8).ToArray()) + "x";

        private async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800, bool reducedMotion = false)
        {
            var context = await _env.NewContextAsync(width: width, height: height, reducedMotion: reducedMotion);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await WaitCameraStillAsync(page);
        }

        private static Task WaitCameraStillAsync(IPage page) =>
            page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");

        // BAR z paska stref: kamera podchodzi, otwiera się Karta rozmów (fokus na pierwszej pozycji).
        private static async Task OpenBarAsync(IPage page)
        {
            await BarButton(page).ClickAsync();
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
        }

        // Status wyszukiwania ustalony (bez "Szukanie…") — wyniki lub pusty stan gotowe do kliknięcia.
        private static Task WaitSearchSettledAsync(IPage page) =>
            page.WaitForFunctionAsync("() => { const h = document.querySelector('#bar-search-hint'); return h && !h.querySelector('.spinner') && (document.querySelector('dialog[open] .search-results') || document.querySelector('dialog[open] .alert') || /Wpisz/.test(h.textContent)); }");

        private static async Task<List<int>> ApiFeedIdsAsync(IBrowserContext context, string query)
        {
            var json = await (await context.APIRequest.GetAsync($"/api/posts?{query}")).JsonAsync();
            return json!.Value.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        }

        private static Task<int[]> ListIdsAsync(IPage page) =>
            page.EvaluateAsync<int[]>("() => [...document.querySelectorAll('dialog[open] .post-list > li[id^=\"feed-item-\"]')].map(li => +li.id.slice(10))");

        // ---- 1. BAR → Karta rozmów ------------------------------------------------------------------------------

        [Fact]
        public async Task Bar_OpensConversationMenu_NotTheFeed()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");

            await OpenBarAsync(page);
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            Assert.True(await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ") < mainZ - 2, "Kamera podeszła do baru.");

            var panel = Panel(page);
            await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Karta rozmów", Level = 2 })).ToBeVisibleAsync();
            await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Na początek" })).ToBeVisibleAsync();
            await Expect(panel.GetByRole(AriaRole.Heading, new() { Name = "Tematy" })).ToBeVisibleAsync();
            foreach (var name in new[] { "Najnowsze", "Popularne", "Wszystkie rozmowy" })
                await Expect(panel.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^" + name) })).ToBeVisibleAsync();
            foreach (var topic in Topics)
                await Expect(panel.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^" + topic) })).ToBeVisibleAsync();
            await Expect(panel.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Feedback") })).ToHaveCountAsync(0);   // nie jest tematem baru
            await Expect(panel.GetByLabel("Szukaj rozmowy")).ToBeVisibleAsync();
            await Expect(Cards(page)).ToHaveCountAsync(0);   // najpierw menu, nie cały feed
            Assert.Equal(1, await page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length"));
        }

        // ---- 2–4. Najnowsze / Popularne / Wszystkie --------------------------------------------------------------

        [Fact]
        public async Task NewestPopularAll_UseExistingFeedSorting_InCompactCards()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Najświeższa rozmowa {Word()}";
            var newest = await Api.CreatePostAsync(_env.Main, author, title, "Ideas", "Krótki fragment rozmowy do listy w barze.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            await Item(page, "newest").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=newest"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Najnowsze rozmowy");
            await Expect(LevelTitle(page)).ToBeFocusedAsync();
            var first = Cards(page).First;
            await Expect(first.Locator(".post-card-title")).ToHaveTextAsync(title);
            await Expect(first.Locator(".post-card-author")).ToHaveTextAsync(author.Alias);
            await Expect(first.Locator("time")).ToBeVisibleAsync();
            await Expect(first.Locator(".category-badge")).ToContainTextAsync("Pomysły");
            await Expect(first.Locator(".post-card-preview")).ToContainTextAsync("Krótki fragment");
            await Expect(first.Locator(".post-card-stats")).ToContainTextAsync("0 komentarzy");
            await Expect(first.Locator(".post-card-stats")).ToContainTextAsync("0 reakcji");
            await Expect(first.Locator(".like-button")).ToHaveCountAsync(0);   // kompaktowo: bez przycisku reakcji
            Assert.Equal((await ApiFeedIdsAsync(context, "sort=newest")).Take(5), (await ListIdsAsync(page)).Take(5));
            Assert.Equal(newest, (await ListIdsAsync(page))[0]);

            // Powrót do Karty rozmów — fokus na pozycji, z której wyszliśmy.
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Karta rozmów" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(Item(page, "newest")).ToBeFocusedAsync();

            await Item(page, "popular").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=popular"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Popularne rozmowy");
            await Expect(Cards(page).First).ToBeVisibleAsync();
            Assert.Equal((await ApiFeedIdsAsync(context, "sort=popular")).Take(5), (await ListIdsAsync(page)).Take(5));

            await page.Keyboard.PressAsync("Escape");
            await Expect(Item(page, "popular")).ToBeFocusedAsync();
            await Item(page, "all").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=all"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Wszystkie rozmowy");
            await Expect(Cards(page).First).ToBeVisibleAsync();

            // Sortowanie w "Wszystkich" — ten sam poziom: parametr adresu, bez nowego wpisu historii.
            var historyLength = await page.EvaluateAsync<int>("() => history.length");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=all&sort=popular"));
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Popularne", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
            Assert.Equal(historyLength, await page.EvaluateAsync<int>("() => history.length"));
            await Expect(Cards(page).First).ToBeVisibleAsync();
        }

        // ---- 5. Temat --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Topic_ShowsConversationsOfCategory_InsideBar_WithBackToMenu()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa o grach {Word()}";
            await Api.CreatePostAsync(_env.Main, author, title, "Games");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            await Item(page, "topic-games").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=games"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Gry");
            await Expect(Panel(page).GetByText("Gry, rekomendacje i wspólne sesje.")).ToBeVisibleAsync();
            await Expect(Cards(page).First.Locator(".post-card-title")).ToHaveTextAsync(title);
            Assert.All(await Cards(page).Locator(".category-badge").AllInnerTextsAsync(), badge => Assert.Contains("Gry", badge));
            Assert.Equal((await ApiFeedIdsAsync(context, "sort=newest&category=Games")).Take(5), (await ListIdsAsync(page)).Take(5));
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));   // nadal w sali, nie klasyczna strona
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(1);

            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Karta rozmów" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(Item(page, "topic-games")).ToBeFocusedAsync();
        }

        // ---- 6–8. Wyszukiwanie, wyszukiwanie w temacie, pusty wynik ------------------------------------------------

        [Fact]
        public async Task Search_FromMenu_UsesFts_WithShortEmptyAndResultStates()
        {
            var word = Word();
            var author = await _env.Main.CreateUserAsync();
            var title = $"Szukana rozmowa {word}";
            await Api.CreatePostAsync(_env.Main, author, title, "Travel", $"Treść ze słowem {word} do wyszukiwarki.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            await SearchInput(page).FocusAsync();
            await page.Keyboard.TypeAsync(word, new() { Delay = 30 });
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&q={word}"));
            await Expect(SearchInput(page)).ToBeFocusedAsync();   // pisanie trwa w tym samym polu
            await WaitSearchSettledAsync(page);
            await Expect(LevelTitle(page)).ToHaveTextAsync($"Wyniki: „{word}”");
            await Expect(Cards(page)).ToHaveCountAsync(1);
            await Expect(Cards(page).First.Locator(".post-card-title")).ToHaveTextAsync(title);
            await Expect(page.Locator("#bar-search-hint")).ToContainTextAsync("1 wynik");

            // Za krótka fraza (stan "invalid") i brak wyników (stan "empty") — bez zapytań po stronie klienta.
            await SearchInput(page).FillAsync("a");
            await Expect(page.Locator("#bar-search-hint")).ToContainTextAsync("Wpisz co najmniej");
            await Expect(Cards(page)).ToHaveCountAsync(0);
            await SearchInput(page).FillAsync("zzqqxnicniepasuje");
            await Expect(Panel(page).Locator(".state-title")).ToHaveTextAsync("Brak rozmów dla „zzqqxnicniepasuje”");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Wyczyść wyszukiwanie" }).Last.ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=search"));
            await Expect(page.Locator("#bar-search-hint")).ToContainTextAsync("Wpisz, czego szukasz");

            // Escape z wyników → Karta rozmów, fokus wraca do wyszukiwarki.
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(SearchInput(page)).ToBeFocusedAsync();
        }

        [Fact]
        public async Task Search_WithinTopic_FiltersByCategory_AndCanWiden()
        {
            var word = Word();
            var author = await _env.Main.CreateUserAsync();
            await Api.CreatePostAsync(_env.Main, author, $"Technika {word}", "Technology", $"Słowo {word} w technologii.");
            await Api.CreatePostAsync(_env.Main, author, $"Podróż {word}", "Travel", $"Słowo {word} w podróżach.");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "topic-technology").ClickAsync();
            await Expect(Panel(page).GetByLabel("Szukaj w temacie Technologia")).ToBeVisibleAsync();

            await SearchInput(page).FillAsync(word);
            await SearchInput(page).PressAsync("Enter");
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&category=technology&q={word}"));
            await WaitSearchSettledAsync(page);
            await Expect(Cards(page)).ToHaveCountAsync(1);
            await Expect(Cards(page).First.Locator(".post-card-title")).ToHaveTextAsync($"Technika {word}");
            await Expect(Panel(page).GetByLabel("Temat")).ToHaveValueAsync("technology");

            await Panel(page).GetByLabel("Temat").SelectOptionAsync("");
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=search&q={word}"));
            await Expect(Cards(page)).ToHaveCountAsync(2);

            // W górę: wyniki → temat, z którego przyszliśmy (historia), potem Karta rozmów.
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=technology"));
            await Expect(LevelTitle(page)).ToBeFocusedAsync();
        }

        // ---- 9–10. Poziomy: post → lista → Karta → sala; "wstecz" / "dalej" -------------------------------------------

        // Sprint 16B: rozmowa otwiera się w oknie BAR (nie na /post-view) — pełne przejścia rozmowy: BarConversationDetailE2ETests.
        [Fact]
        public async Task Levels_PostToListToMenuToHall_EscapeAndBackLink()
        {
            var author = await _env.Main.CreateUserAsync();
            var title = $"Rozmowa z posta do baru {Word()}";
            var postId = await Api.CreatePostAsync(_env.Main, author, title, "Projects");

            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "topic-projects").ClickAsync();
            await Expect(Cards(page).First.Locator(".post-card-title")).ToHaveTextAsync(title);

            await Cards(page).First.Locator(".post-card-title a").ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path($"/saloon?bar=post&id={postId}&from=topic&category=projects"));
            await Expect(page.Locator("#bar-conversation-title")).ToHaveTextAsync(title);
            await Expect(Panel(page).GetByRole(AriaRole.Button, new() { Name = "Wróć do rozmów w barze" }).First).ToBeVisibleAsync();

            // Rozmowa → Escape → lista (ta sama karta z fokusem).
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=topic&category=projects"));
            await Expect(LevelTitle(page)).ToHaveTextAsync("Projekty");
            await Expect(page.Locator($"#post-{postId}-title a")).ToBeFocusedAsync();

            // Lista → Escape → Karta rozmów → Escape → sala (fokus na BAR, kamera w kadrze głównym).
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(Item(page, "topic-projects")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(new Regex(@"/saloon(\?scene=3d)?$"));
            await Expect(BarButton(page)).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");

            // Klasyczny /post-view/{id} (link z zewnątrz) — zwykły widok i zwykły powrót.
            await Ui.GotoAsync(page, $"/post-view/{postId}");
            await Expect(page.Locator(".back-link")).ToHaveTextAsync("Wróć do odkrywania");
            await Expect(page.Locator(".saloon-hall")).ToHaveCountAsync(0);
        }

        [Fact]
        public async Task BrowserBackForward_MoveBetweenLevels_NotRandomPanelStates()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Item(page, "newest").ClickAsync();
            await Expect(LevelTitle(page)).ToHaveTextAsync("Najnowsze rozmowy");

            await page.GoBackAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await Expect(Item(page, "newest")).ToBeVisibleAsync();
            await page.GoBackAsync();
            await Expect(Panel(page)).ToHaveCountAsync(0);
            // Kamera wraca do kadru głównego — kolejny krok dopiero po zakończonym przejeździe (warunek, nie opóźnienie):
            // w headless z programowym WebGL klatki animacji blokują wątek strony i odsuwają obsługę nawigacji.
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");

            await page.GoForwardAsync();
            await Expect(Item(page, "newest")).ToBeVisibleAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await page.GoForwardAsync();
            await Expect(LevelTitle(page)).ToHaveTextAsync("Najnowsze rozmowy");

            // "Zamknij" z głębokiego poziomu: od razu do sali (cofnięcie do wpisu sali, bez nowych wpisów historii).
            var length = await page.EvaluateAsync<int>("() => history.length");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(new Regex(@"/saloon(\?scene=3d)?$"));
            Assert.Equal(length, await page.EvaluateAsync<int>("() => history.length"));
        }

        // Link prosto do poziomu BAR (udostępniony adres) — sala otwiera się od razu przy barze z tym poziomem;
        // w górę: rodzic (bez historii w sesji).
        [Fact]
        public async Task DeepLink_OpensLevelDirectly_UpGoesToParent()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page, "/saloon?bar=topic&category=travel&scene=3d");
            await Expect(LevelTitle(page)).ToHaveTextAsync("Podróże");
            Assert.Equal("bar", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?bar=menu"));
            await page.Keyboard.PressAsync("Escape");
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon"));

            // Nieprawidłowy temat → Karta rozmów (nie pusty panel).
            await OpenHallAsync(page, "/saloon?bar=topic&category=nieistnieje&scene=3d");
            await Expect(Item(page, "newest")).ToBeVisibleAsync();
        }

        // ---- 11–13. Gość, zalogowany, Rozpocznij rozmowę ---------------------------------------------------------

        [Fact]
        public async Task Guest_ReadsConversations_AndGetsLoginCta()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            var cta = Panel(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się, aby rozpocząć rozmowę" });
            await Expect(cta).ToHaveAttributeAsync("href", "/login?returnUrl=%2Fnew");
            await Expect(Panel(page).GetByRole(AriaRole.Link, new() { Name = "Załóż konto" })).ToHaveAttributeAsync("href", "/register?returnUrl=%2Fnew");

            await Item(page, "topic-ideas").ClickAsync();
            await Expect(Panel(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się, aby rozpocząć rozmowę" }).First)
                .ToHaveAttributeAsync("href", "/login?returnUrl=%2Fnew%3Fcategory%3Dideas");
            await Expect(Panel(page).Locator(".like-button")).ToHaveCountAsync(0);
        }

        [Fact]
        public async Task LoggedUser_StartsConversation_WithTopicPreselected()
        {
            await using var session = await Session.UserAsync(_env);
            await session.Context.AddInitScriptAsync(CapableGpu);
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenBarAsync(page);

            await Expect(Panel(page).GetByRole(AriaRole.Link, new() { Name = "Rozpocznij rozmowę", Exact = true })).ToHaveAttributeAsync("href", "/new");
            await Item(page, "topic-travel").ClickAsync();
            var start = Panel(page).GetByRole(AriaRole.Link, new() { Name = "Rozpocznij rozmowę: Podróże" }).First;
            await Expect(start).ToHaveAttributeAsync("href", "/new?category=travel");

            await start.ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/new?category=travel"));
            await Expect(page.Locator("#post-category")).ToHaveValueAsync("Travel");
        }

        // ---- 14. Klawiatura --------------------------------------------------------------------------------------

        [Fact]
        public async Task Keyboard_TabShiftTabEnterSpaceEscape_WholeBarWithoutMouse()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            await BarButton(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Item(page, "newest")).ToBeFocusedAsync();

            // Tab i Shift+Tab zostają w oknie (pułapka fokusu natywnego dialogu).
            for (var i = 0; i < 25; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await FocusNotInBackground(page), $"Tab #{i + 1} trafił do sali pod oknem.");
            }
            for (var i = 0; i < 25; i++)
            {
                await page.Keyboard.PressAsync("Shift+Tab");
                Assert.True(await FocusNotInBackground(page), $"Shift+Tab #{i + 1} trafił do sali pod oknem.");
            }

            // Spacja na pozycji menu → lista; Escape → Karta (fokus na tej pozycji); Enter na temacie → temat.
            await Item(page, "popular").FocusAsync();
            Assert.Equal("solid", await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle"));
            await page.Keyboard.PressAsync("Space");
            await Expect(LevelTitle(page)).ToHaveTextAsync("Popularne rozmowy");
            await Expect(LevelTitle(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await FocusInPanel(page));
            await page.Keyboard.PressAsync("Escape");
            await Expect(Item(page, "popular")).ToBeFocusedAsync();

            await Item(page, "topic-general").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(LevelTitle(page)).ToHaveTextAsync("Ogólne");
            await page.Keyboard.PressAsync("Escape");
            await Expect(Item(page, "topic-general")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(Panel(page)).ToHaveCountAsync(0);
            await Expect(BarButton(page)).ToBeFocusedAsync();
        }

        // ---- 15–16. Mobile 390 / 360 -----------------------------------------------------------------------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_BarIsFullWidthSheet_ScrollableWithoutOverflow(int width, int height)
        {
            await using var context = await NewContextAsync(width, height);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await OpenBarAsync(page);
            await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

            async Task AssertSheetAsync(string context)
            {
                var box = (await Panel(page).BoundingBoxAsync())!;
                Assert.True(Math.Abs(box.X) < 0.5 && Math.Abs(box.Width - width) < 0.5, $"{context}: okno nie na całą szerokość ({box.X}, {box.Width}).");
                Assert.True(box.Height <= height + 0.5, $"{context}: okno wyższe niż ekran.");
                var close = (await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).BoundingBoxAsync())!;
                Assert.True(close.Y >= 0 && close.Y + close.Height <= height && close.Height >= 40, $"{context}: przycisk zamknięcia poza ekranem.");
                Assert.False(await Panel(page).EvaluateAsync<bool>("d => [...d.querySelectorAll('*')].some(e => e.getBoundingClientRect().right > innerWidth + 0.5)"), $"{context}: element wychodzi poza ekran.");
                await Ui.AssertNoHorizontalOverflowAsync(page, $"{context} {width}");
            }

            await AssertSheetAsync("Karta rozmów");
            foreach (var item in await Panel(page).Locator("[data-bar-item]").AllAsync())
                Assert.True((await item.BoundingBoxAsync())!.Height >= 44, "Pozycja Karty rozmów za niska na dotyk.");

            await Item(page, "all").ClickAsync();
            await Expect(Cards(page).First).ToBeVisibleAsync();
            await AssertSheetAsync("lista");
            Assert.True(await Panel(page).EvaluateAsync<bool>("d => d.scrollHeight > d.clientHeight"), "Lista przewija się w oknie.");
            await Panel(page).EvaluateAsync("d => d.scrollTop = d.scrollHeight");
            await AssertSheetAsync("lista przewinięta");

            await page.Keyboard.PressAsync("Escape");
            await SearchInput(page).FillAsync("rozmowa");
            await SearchInput(page).PressAsync("Enter");
            await WaitSearchSettledAsync(page);
            await AssertSheetAsync("wyniki");
        }

        // ---- 17. Reduced motion ----------------------------------------------------------------------------------

        [Fact]
        public async Task ReducedMotion_BarOpensWithoutCameraAnimation()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });

            await BarButton(page).ClickAsync();
            await Expect(Item(page, "newest")).ToBeFocusedAsync();
            var z = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");
            await page.WaitForTimeoutAsync(300);
            Assert.Equal(z, await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ"));   // kamera już na miejscu
            Assert.Equal("none", await Panel(page).EvaluateAsync<string>("d => getComputedStyle(d).animationName"));
        }

        // ---- 18. Wielokrotne wejście do BAR ----------------------------------------------------------------------

        // Pięć razy: Karta → lista → zamknięcie. Jeden canvas, sala nie rysuje klatek przy czytaniu, historia nie rośnie
        // (zamknięcie cofa do wpisu sali), kamera wraca do kadru głównego.
        [Fact]
        public async Task ReEntry_BarSeveralTimes_StableHistoryCanvasAndIdleHall()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");
            int? length = null;

            for (var round = 0; round < 5; round++)
            {
                await OpenBarAsync(page);
                await Item(page, round % 2 == 0 ? "newest" : "topic-games").ClickAsync();
                await Expect(LevelTitle(page)).ToBeFocusedAsync();
                await WaitCameraStillAsync(page);

                var frames = await Frames(page);
                await Panel(page).EvaluateAsync("d => d.scrollTop = d.scrollHeight");
                await page.WaitForTimeoutAsync(700);
                Assert.Equal(frames, await Frames(page));   // czytanie rozmów nie budzi renderu sali

                await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
                await Expect(Panel(page)).ToHaveCountAsync(0);
                await Expect(BarButton(page)).ToBeFocusedAsync();
                await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
                await WaitCameraStillAsync(page);
                Assert.Equal(mainZ, await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ"), 2);
                Assert.Equal(1, await page.EvaluateAsync<int>("() => document.querySelectorAll('canvas').length"));

                var now = await page.EvaluateAsync<int>("() => history.length");
                length ??= now;
                Assert.Equal(length, now);
            }
        }
    }
}
