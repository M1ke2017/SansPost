using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 19 — Stół gry "Śladem Rewolwerowca". Okno nad salą (kamera przy stole), zasady i karty komend; zalogowany
    // gracz rozgrywa pojedynek treningowy z manekinem na prawdziwym silniku F# (SansPost.Game.Core) przez serwer —
    // ten sam stan widzi REST /api/duels. Manekin gra jawnym scenariuszem: Przeładowanie, Strzał, Blok, Prowokacja, Unik, Strzał…
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Game")]
    public class GameTableE2ETests
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
        private readonly ITestOutputHelper _output;

        public GameTableE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].game-panel");
        private static ILocator GameButton(IPage page) => page.Locator(".hall-zone[data-zone='game']");
        private static ILocator Card(IPage page, string card) => page.Locator($"dialog[open] .game-card[data-card='{card}']");
        private static ILocator Status(IPage page) => page.Locator("dialog[open] .game-status");
        private static ILocator Start(IPage page) => Panel(page).GetByRole(AriaRole.Button, new() { Name = "Rozpocznij pojedynek treningowy" });

        private async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800, ColorScheme scheme = ColorScheme.Light)
        {
            var context = await _env.NewContextAsync(width: width, height: height, colorScheme: scheme);
            await context.AddInitScriptAsync(CapableGpu);
            return context;
        }

        private async Task<Session> SignedInAsync(int width = 1280, int height = 800, ColorScheme scheme = ColorScheme.Light)
        {
            var session = await Session.UserAsync(_env, width: width, height: height);
            await session.Context.AddInitScriptAsync(CapableGpu);
            if (scheme == ColorScheme.Dark)
                await session.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            return session;
        }

        private static async Task OpenHallAsync(IPage page, string path = "/saloon?scene=3d")
        {
            await Ui.GotoAsync(page, path);
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
        }

        private static async Task OpenTableAsync(IPage page)
        {
            await GameButton(page).ClickAsync();
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?game=table"));
            await Expect(Panel(page)).ToBeVisibleAsync();
            Assert.Equal("game", await page.EvaluateAsync<string>("() => window.__sansPostHall.area"));
        }

        private static async Task PlayAsync(IPage page, string card, int expectedHistory)
        {
            await Card(page, card).ClickAsync();
            await Expect(page.Locator("dialog[open] .game-history-list > li")).ToHaveCountAsync(expectedHistory);
        }

        // ---- Pasek stref → kamera przy stole → okno; gość: zasady, karty komend (podgląd), logowanie w oknie ----------

        [Fact]
        public async Task Guest_OpensTable_SeesRulesAndCommandCards_LoginInsidePanel()
        {
            await using var context = await NewContextAsync(1440, 900);
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);
            var mainZ = await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ");

            await Expect(GameButton(page)).ToHaveAttributeAsync("aria-haspopup", "dialog");
            await Expect(GameButton(page).Locator(".hall-zone-hint")).ToHaveTextAsync("pojedynek");
            await OpenTableAsync(page);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");
            Assert.NotEqual(mainZ, await page.EvaluateAsync<double>("() => window.__sansPostHall.cameraZ"), 1);

            await Expect(Panel(page).Locator(".dialog-title")).ToHaveTextAsync("Śladem Rewolwerowca");
            await Expect(Panel(page).Locator(".game-rules")).ToContainTextAsync("3 prestiżu");
            await Expect(Panel(page).Locator(".game-engine")).ToContainTextAsync("F#");
            await Expect(Panel(page).Locator(".game-card-name")).ToHaveTextAsync(new[] { "Strzał", "Unik", "Przeładowanie", "Blok", "Prowokacja" });
            await Expect(Card(page, "dodge")).ToContainTextAsync("Unika strzału, ale prowokacja go karze.");
            foreach (var card in new[] { "shoot", "dodge", "reload", "block", "taunt" })
                await Expect(Card(page, card)).ToBeDisabledAsync();                   // gość — tylko podgląd
            var text = await Panel(page).InnerTextAsync();
            foreach (var forbidden in new[] { "♠", "♥", "♦", "♣", "żeton", "stawk", "zakład", "$" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);

            // Logowanie gościa w tym samym oknie (bez klasycznej strony), powrót Escape do stołu.
            await Panel(page).GetByRole(AriaRole.Link, new() { Name = "Zaloguj się, aby zagrać" }).ClickAsync();
            await Expect(page.Locator("#bar-login-email")).ToBeFocusedAsync();
            await Expect(page).ToHaveURLAsync(new Regex(@"/saloon\?game=login&next="));
            await Expect(Panel(page).Locator(".bar-auth .bar-back")).ToContainTextAsync("Śladem Rewolwerowca");
            await page.Keyboard.PressAsync("Escape");
            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?game=table"));
            await Expect(Panel(page).Locator(".game-cards")).ToBeVisibleAsync();

            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(GameButton(page)).ToBeFocusedAsync();
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null");
        }

        [Fact]
        public async Task TableInScene_Click_OpensSameFlow()
        {
            await using var context = await NewContextAsync();
            var page = await context.NewPageAsync();
            await OpenHallAsync(page);

            var point = await page.EvaluateAsync<double[]>("() => window.__sansPostHall.points.game");
            var canvas = (await page.Locator(".saloon-hall canvas").BoundingBoxAsync())!;
            await page.Mouse.ClickAsync((float)(canvas.X + point[0]), (float)(canvas.Y + point[1]));

            await Expect(page).ToHaveURLAsync(Ui.Path("/saloon?game=table"));
            await Expect(Panel(page)).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
        }

        // ---- Zalogowany: trening z manekinem na silniku F# — rundy, efekty, historia, koniec; ten sam stan w REST ------

        [Fact]
        public async Task SignedIn_TrainingDuel_ResolvedByServerEngine_SameStateInApi()
        {
            await using var session = await SignedInAsync();
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenTableAsync(page);

            await Start(page).ClickAsync();
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
            await Expect(Panel(page).Locator(".game-you .game-player-alias")).ToHaveTextAsync(session.User!.Alias);
            await Expect(Panel(page).Locator(".game-opponent .game-player-alias")).ToHaveTextAsync("Manekin treningowy");
            await Expect(Card(page, "shoot")).ToBeFocusedAsync();                      // pierwsza dostępna karta
            await Expect(Status(page)).ToHaveTextAsync("Wybierz kartę — przeciwnik wybiera w tym samym czasie.");

            // Runda 1: Strzał vs Przeładowanie manekina → trafienie, przeładowanie przerwane.
            await PlayAsync(page, "shoot", 1);
            await Expect(Panel(page).Locator(".game-reveal-cards")).ToContainTextAsync("Ty: Strzał");
            await Expect(Panel(page).Locator(".game-reveal-cards")).ToContainTextAsync("Przeciwnik: Przeładowanie");
            await Expect(Panel(page).Locator(".game-effects")).ToContainTextAsync("Twój strzał trafił.");
            await Expect(Panel(page).Locator(".game-effects")).ToContainTextAsync("Twój strzał przerwał przeładowanie przeciwnika.");
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Runda 2 z 12");

            // Bez naboju strzał jest niedostępny (silnik: NotEnoughAmmo) — z powodem dla czytnika.
            await Expect(Card(page, "shoot")).ToBeDisabledAsync();
            await Expect(Card(page, "shoot")).ToContainTextAsync("Brak naboju.");

            // Ten sam pojedynek przez REST: serwer jest źródłem prawdy (UI niczego nie liczy).
            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(session.User.Email, E2EEnvironment.UserPassword))
            {
                var active = await api.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/duels/active");
                Assert.Equal(2, active.GetProperty("round").GetInt32());
                Assert.Equal(2, active.GetProperty("opponent").GetProperty("prestige").GetInt32());
                Assert.Equal("shoot", active.GetProperty("history")[0].GetProperty("yourCard").GetString());
            }

            // Do końca: prowokacja co rundę — R2 Strzał manekina trafia (2 : 2), R3 Blok ukarany (2 : 1), R4 remis prowokacji,
            // R5 Unik ukarany (2 : 0) — wygrana w rundzie 5.
            for (var round = 2; round <= 12; round++)
            {
                if (await Panel(page).Locator(".game-status.is-final").CountAsync() > 0)
                    break;
                await PlayAsync(page, "taunt", round);
            }
            await Expect(Status(page)).ToHaveTextAsync("Wygrana! Przeciwnik schodzi ze sceny.");
            await Expect(page.Locator("dialog[open] .game-history-list > li")).ToHaveCountAsync(5);
            await Expect(Panel(page).Locator(".game-you .game-player-stat").First).ToContainTextAsync("Prestiż: 2.");
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Pojedynek zakończony");
            foreach (var card in new[] { "shoot", "dodge", "reload", "block", "taunt" })
                await Expect(Card(page, card)).ToBeDisabledAsync();

            // Powrót do stołu po ponownym otwarciu — ten sam pojedynek; potem nowy trening.
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await page.WaitForFunctionAsync("() => window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            await OpenTableAsync(page);
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Pojedynek zakończony");
            await Panel(page).GetByRole(AriaRole.Button, new() { Name = "Nowy pojedynek treningowy" }).ClickAsync();
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
        }

        // ---- Klawiatura: Tab / Enter / Spacja / Escape, status dla czytnika, fokus wraca na "Stół gry" ------------------

        [Fact]
        public async Task Keyboard_StartPlayWithSpace_EscapeReturnsFocus()
        {
            await using var session = await SignedInAsync();
            var page = session.Page;
            await OpenHallAsync(page);

            await page.Locator(".hall-zone[data-zone='wanted']").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(GameButton(page)).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Panel(page)).ToBeVisibleAsync();
            await Start(page).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Expect(Card(page, "shoot")).ToBeFocusedAsync();
            await Expect(Status(page)).ToHaveAttributeAsync("role", "status");

            await page.Keyboard.PressAsync("Tab");
            await Expect(Card(page, "dodge")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Expect(Card(page, "reload")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Expect(Card(page, "dodge")).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Space");                                   // Unik vs Przeładowanie manekina
            await Expect(page.Locator("dialog[open] .game-history-list > li")).ToHaveCountAsync(1);
            await Expect(Status(page)).ToHaveTextAsync("Runda 1 rozstrzygnięta. Wybierz kartę na rundę 2.");
            await Expect(Card(page, "dodge")).ToHaveAccessibleDescriptionAsync(new Regex("Unika strzału"));

            for (var i = 0; i < 10; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                Assert.True(await page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }"),
                    $"Tab #{i + 1} trafił do sali pod oknem.");
            }

            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(GameButton(page)).ToBeFocusedAsync();
        }

        // ---- Mobile 390 / 360: arkusz na całą szerokość, karty 2 kolumny, cele ≥ 44 px -----------------------------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_SheetAndCardsFit(int width, int height)
        {
            await using var session = await SignedInAsync(width, height);
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenTableAsync(page);
            await Start(page).ClickAsync();
            await Expect(Card(page, "shoot")).ToBeEnabledAsync();
            await Panel(page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

            var sheet = (await Panel(page).BoundingBoxAsync())!;
            Assert.True(Math.Abs(sheet.X) < 0.5 && Math.Abs(sheet.Width - width) < 0.5, $"Arkusz na całą szerokość: {sheet.X} + {sheet.Width}.");
            await Ui.AssertNoHorizontalOverflowAsync(page, $"stół gry {width}");
            // Jeden odczyt obu komórek siatki (li — bez uniesienia karty pod kursorem); fokus przewija arkusz, więc dwa osobne
            // pomiary łapałyby różne chwile.
            var row = await page.EvaluateAsync<double[]>(
                "() => ['shoot', 'dodge'].flatMap(c => { const r = document.querySelector(`dialog[open] .game-card[data-card='${c}']`).closest('li').getBoundingClientRect(); return [r.x, r.y]; })");
            Assert.True(Math.Abs(row[1] - row[3]) < 1 && row[2] > row[0], $"Dwie karty w rzędzie: {string.Join(", ", row)}.");
            var small = await page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('dialog[open] button, dialog[open] a, dialog[open] summary')].filter(e => e.getClientRects().length && e.getBoundingClientRect().height < 44).map(e => e.className || e.tagName)");
            Assert.True(small.Length == 0, "Cele dotyku < 44 px: " + string.Join(", ", small));

            await PlayAsync(page, "reload", 1);
            await Ui.AssertNoHorizontalOverflowAsync(page, $"stół gry po rundzie {width}");
        }

        // ---- axe: gość, trwający trening i koniec pojedynku — 0 critical / 0 serious --------------------------------------

        [Fact]
        public async Task Axe_GameTable_NoCriticalOrSerious()
        {
            var blocking = new List<string>();

            async Task AuditAsync(IPage page, string name)
            {
                await page.EvaluateAsync("() => Promise.allSettled(document.getAnimations().map(a => a.finished))");
                var result = await page.RunAxe();
                foreach (var violation in result.Violations)
                {
                    var line = $"{name}: [{violation.Impact}] {violation.Id} — {violation.Help} ({string.Join(" | ", violation.Nodes.Take(3).Select(n => string.Join(" ", n.Target)))})";
                    _output.WriteLine(line);
                    if (violation.Impact is "critical" or "serious")
                        blocking.Add(line);
                }
            }

            await using (var guest = await NewContextAsync(scheme: ColorScheme.Dark))
            {
                var page = await guest.NewPageAsync();
                await OpenHallAsync(page);
                await OpenTableAsync(page);
                await AuditAsync(page, "Stół gry — gość (dark)");
            }

            foreach (var (scheme, width, height) in new[] { (ColorScheme.Light, 1280, 800), (ColorScheme.Dark, 1280, 800), (ColorScheme.Light, 390, 844) })
            {
                await using var session = await SignedInAsync(width, height, scheme);
                var page = session.Page;
                await OpenHallAsync(page);
                await OpenTableAsync(page);
                await AuditAsync(page, $"Stół gry — start {scheme} {width}");
                await Start(page).ClickAsync();
                await PlayAsync(page, "shoot", 1);
                await AuditAsync(page, $"Stół gry — po rundzie {scheme} {width}");
                for (var round = 2; round <= 12 && await Panel(page).Locator(".game-status.is-final").CountAsync() == 0; round++)
                    await PlayAsync(page, "taunt", round);
                await AuditAsync(page, $"Stół gry — koniec {scheme} {width}");
            }

            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));
        }
            // ---- Sprint 19-FIX: konto zawieszone — Stół gry widoczny, udział w pojedynkach niedostępny (z wyjaśnieniem) ------

        [Fact]
        public async Task Suspended_SeesTableAndOwnDuel_ActionsUnavailableWithExplanation()
        {
            await using var session = await SignedInAsync();
            var page = session.Page;
            await OpenHallAsync(page);
            await OpenTableAsync(page);
            await Start(page).ClickAsync();
            await PlayAsync(page, "reload", 1);

            await Api.AdminAsync(_env.Main, $"/api/moderation/users/{session.User!.Id}/suspend");
            await Ui.LoginAsync(page, session.User.Email, E2EEnvironment.UserPassword);   // stara sesja unieważniona
            await OpenHallAsync(page);
            await Expect(GameButton(page)).ToBeVisibleAsync();                             // strefa nie znika
            await OpenTableAsync(page);

            var message = Panel(page).Locator(".game-restricted");
            await Expect(message).ToHaveTextAsync("Możesz przeglądać Stół gry, ale udział w pojedynkach jest obecnie niedostępny.");
            await Expect(message).ToHaveAttributeAsync("role", "status");
            await Expect(Panel(page).Locator(".game-rules")).ToBeVisibleAsync();
            await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Runda 2 z 12");     // własny pojedynek — odczyt
            await Expect(page.Locator("dialog[open] .game-history-list > li")).ToHaveCountAsync(1);
            await Expect(Panel(page).Locator(".game-start")).ToHaveCountAsync(0);
            foreach (var card in new[] { "shoot", "dodge", "reload", "block", "taunt" })
                await Expect(Card(page, card)).ToBeDisabledAsync();
            await Expect(Card(page, "dodge")).ToContainTextAsync("Udział w pojedynkach jest niedostępny.");

            // Serwer rozstrzyga niezależnie od UI: ruch przez API odrzucony, stan bez zmian.
            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(session.User.Email, E2EEnvironment.UserPassword))
            {
                var active = await api.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/duels/active");
                var id = active.GetProperty("duelId").GetString();
                var move = await api.PostAsJsonAsync($"/api/duels/{id}/moves", new { card = "dodge", round = 2 });
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, move.StatusCode);
                Assert.Contains("account-suspended", await move.Content.ReadAsStringAsync());
                Assert.Equal(2, (await api.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/duels/active")).GetProperty("round").GetInt32());
            }

            var result = await page.RunAxe();
            var blocking = result.Violations.Where(v => v.Impact is "critical" or "serious").Select(v => $"{v.Id}: {v.Help}").ToList();
            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));

            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(GameButton(page)).ToBeFocusedAsync();
        }
    }
}
