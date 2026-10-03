using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Sprint 20 — pojedynek 1v1 na żywo (SignalR) między dwoma zalogowanymi graczami w dwóch przeglądarkach (osobne konteksty):
    // gracze przy stole, wyzwanie / odrzucenie / wygaśnięcie, gotowość, ruchy równoczesne, ukryta karta (także na kablu),
    // odsłonięcie, czas rundy i kary, odświeżenie strony, druga karta, rozłączenie z oknem powrotu, oddanie, koniec gry,
    // rewanż, konto zawieszone, mobile, klawiatura, reduced motion, axe.
    // Testy czasu (wygaśnięcie, kary, walkower) — osobna instancja z krótkimi czasami (Duels__*), bez podnoszenia timeoutów testów.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Game")]
    public class LiveDuelE2ETests
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

        // Krótkie czasy tylko dla testów zegara: wyzwanie 5 s, runda 5 s, okno powrotu 4 s.
        private static readonly SemaphoreSlim FastGate = new(1, 1);
        private static SansPostServer? _fast;

        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public LiveDuelE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private async Task<SansPostServer> FastServerAsync()
        {
            await FastGate.WaitAsync();
            try
            {
                return _fast ??= await _env.StartServerAsync("duelfast", new Dictionary<string, string?>
                {
                    ["Duels__ChallengeSeconds"] = "5",
                    ["Duels__RoundSeconds"] = "5",
                    ["Duels__GraceSeconds"] = "4"
                });
            }
            finally
            {
                FastGate.Release();
            }
        }

        // Wejście stroną (odświeżenie, nowa karta, link) otwiera stół dopiero po pełnym starcie sceny 3D. W headless
        // Chromium z programowym WebGL zajmuje to ok. 5 s (zmierzone: kompilacja shaderów ~3,1–3,6 s + obwód Blazor),
        // czyli tyle, ile domyślny limit asercji — stąd wspólny limit dla tej ścieżki.
        private const float SceneStartTimeout = 20_000;

        private static ILocator Panel(IPage page) => page.Locator("dialog[open].game-panel");
        private static ILocator Card(IPage page, string card) => Panel(page).Locator($".game-card[data-card='{card}']");
        private static ILocator Status(IPage page) => Panel(page).Locator(".game-status");
        private static ILocator History(IPage page) => Panel(page).Locator(".game-history-list > li");
        private static ILocator Challenge(IPage page, string alias) =>
            Panel(page).GetByRole(AriaRole.Button, new() { Name = $"Wyzwij: {alias}", Exact = true });
        private static ILocator Invite(IPage page, string alias) => Panel(page).Locator(".game-invite").Filter(new() { HasText = alias });

        private async Task<Session> PlayerAsync(SansPostServer? server = null, int width = 1280, int height = 800, bool reducedMotion = false)
        {
            var session = await Session.UserAsync(_env, server, width: width, height: height, reducedMotion: reducedMotion);
            await session.Context.AddInitScriptAsync(CapableGpu);
            return session;
        }

        // Stół gry w sali, która jest już na stronie (np. po przekierowaniu z logowania).
        private static async Task OpenTableInLoadedHallAsync(IPage page)
        {
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = SceneStartTimeout });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
            await page.Locator(".hall-zone[data-zone='game']").ClickAsync();
            await Expect(Panel(page)).ToBeVisibleAsync();
            await ConnectedAsync(page);
        }

        private static async Task OpenTableAsync(IPage page)
        {
            await Ui.GotoAsync(page, "/saloon?scene=3d");
            await Expect(page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = SceneStartTimeout });
            await page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.moving === false");
            await page.Locator(".hall-zone[data-zone='game']").ClickAsync();
            await Expect(Panel(page)).ToBeVisibleAsync();
            await ConnectedAsync(page);
        }

        private static Task ConnectedAsync(IPage page) =>
            page.WaitForFunctionAsync("() => window.sansPostDuel && window.sansPostDuel.state() === 'Connected'");

        // A wyzywa B, B przyjmuje; opcjonalnie obaj "GOTOWY" — runda 1 z zegarem.
        private async Task<(Session A, Session B)> DuelAsync(SansPostServer? server = null, int width = 1280, int height = 800,
            bool ready = true, bool reducedMotion = false)
        {
            var a = await PlayerAsync(server, width, height, reducedMotion);
            var b = await PlayerAsync(server, width, height, reducedMotion);
            await OpenTableAsync(a.Page);
            await OpenTableAsync(b.Page);
            await Challenge(a.Page, b.User!.Alias).ClickAsync();
            await Invite(b.Page, a.User!.Alias).GetByRole(AriaRole.Button, new() { Name = "Przyjmij" }).ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-ready-button")).ToBeVisibleAsync();
            await Expect(Panel(b.Page).Locator(".game-ready-button")).ToBeVisibleAsync();
            if (ready)
            {
                await Panel(a.Page).Locator(".game-ready-button").ClickAsync();
                await Panel(b.Page).Locator(".game-ready-button:not([disabled])").ClickAsync();
                await Expect(Panel(a.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
                await Expect(Panel(b.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
            }
            return (a, b);
        }

        private static Task<string> DuelIdAsync(IPage page) =>
            page.EvaluateAsync<string>("async () => (await window.sansPostDuel.invoke('RequestState')).duel.duelId");

        private static async Task<string?> InvokeCodeAsync(IPage page, string script) =>
            await page.EvaluateAsync<string?>(script);

        // ---- 1–5: gracze przy stole, wyzwanie, odrzucenie ----------------------------------------------------------------

        [Fact]
        public async Task Lobby_SeesOtherPlayer_NotSelf_ChallengeDelivered_Rejected()
        {
            await using var a = await PlayerAsync();
            await using var b = await PlayerAsync();
            await OpenTableAsync(a.Page);
            await OpenTableAsync(b.Page);

            await Expect(Challenge(a.Page, b.User!.Alias)).ToBeVisibleAsync();
            await Expect(Challenge(a.Page, a.User!.Alias)).ToHaveCountAsync(0);          // nie sam siebie
            await Expect(Panel(a.Page).Locator(".game-online")).Not.ToContainTextAsync("@");  // bez e-maili

            // Własnego uchwytu gracz nie dostaje (lista bez niego), a nieznany uchwyt serwer odrzuca bez szczegółów.
            Assert.Equal("player-unavailable", await InvokeCodeAsync(a.Page, "async () => (await window.sansPostDuel.invoke('ChallengeUser', 'ffffffffffffffff')).code"));

            await Challenge(a.Page, b.User.Alias).ClickAsync();
            var invite = Invite(b.Page, a.User.Alias);
            await Expect(invite).ToContainTextAsync("wyzywa Cię na pojedynek");
            await Expect(invite.Locator(".game-countdown")).ToHaveTextAsync(new Regex(@"^\d+ s$"));
            await Expect(Panel(a.Page).Locator(".game-invite.is-outgoing")).ToContainTextAsync(b.User.Alias);
            await Expect(Challenge(a.Page, b.User.Alias)).ToBeDisabledAsync();            // jedno wysłane wyzwanie naraz

            await invite.GetByRole(AriaRole.Button, new() { Name = "Odrzuć" }).ClickAsync();

            await Expect(Panel(a.Page).Locator(".game-lobby-note")).ToHaveTextAsync($"{b.User.Alias} odrzuca wyzwanie.");
            await Expect(Panel(a.Page).Locator(".game-invite")).ToHaveCountAsync(0);
            await Expect(Panel(b.Page).Locator(".game-invite")).ToHaveCountAsync(0);
            await Expect(Panel(a.Page).Locator(".game-board")).ToHaveCountAsync(0);
        }

        // ---- 7–14: przyjęcie, gotowość, ruchy równoczesne, ukryta karta, dokładnie raz, stary pakiet ------------------------

        [Fact]
        public async Task Accept_Ready_HiddenCardOnTheWire_SimultaneousMoves_ResolveOnce()
        {
            var bFrames = new List<string>();
            var (a, b) = await DuelAsync(ready: false);
            await using var _a = a;
            await using var _b = b;
            b.Page.WebSocket += (_, socket) =>
            {
                if (socket.Url.Contains("/hubs/duel"))
                    socket.FrameReceived += (_, frame) => { lock (bFrames) bFrames.Add(frame.Text ?? ""); };
            };

            // Tylko A gotowy — gra nie startuje.
            await Panel(a.Page).Locator(".game-ready-button").ClickAsync();
            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik jest gotowy — kliknij „GOTOWY”.");
            await Expect(Panel(a.Page).Locator(".game-timer")).ToHaveCountAsync(0);
            await Expect(Card(a.Page, "dodge")).ToBeDisabledAsync();

            // B odświeża stronę przed startem — ten sam pojedynek wraca (ramki huba liczone od nowego połączenia).
            await b.Page.ReloadAsync();
            await Expect(Panel(b.Page)).ToBeVisibleAsync(new() { Timeout = SceneStartTimeout });
            await ConnectedAsync(b.Page);
            await Panel(b.Page).Locator(".game-ready-button:not([disabled])").ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-timer")).ToHaveTextAsync(new Regex(@"^\d+ s$"));
            await Expect(Panel(b.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");

            // A wybiera Strzał — B wie tylko, że przeciwnik wybrał.
            await Card(a.Page, "shoot").ClickAsync();
            await Expect(Card(a.Page, "shoot")).ToContainTextAsync("Twój wybór");
            await Expect(Panel(b.Page).Locator(".game-opponent .game-ready")).ToHaveTextAsync("Karta wybrana");
            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik wybrał — Twój ruch.");
            await Expect(Panel(b.Page).Locator(".game-reveal")).ToHaveCountAsync(0);
            string wire;
            lock (bFrames)
                wire = Regex.Replace(string.Join("\n", bFrames), "\"availableActions\":\\[.*?\\]", "");
            Assert.DoesNotContain("shoot", wire);   // także na kablu SignalR: żadnej karty przeciwnika przed odsłonięciem

            // Druga karta — runda rozstrzyga się raz, obaj widzą to samo.
            await Card(b.Page, "dodge").ClickAsync();
            foreach (var page in new[] { a.Page, b.Page })
            {
                await Expect(History(page)).ToHaveCountAsync(1);
                await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Runda 2 z 12");
            }
            await Expect(Panel(a.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Ty: Strzał");
            await Expect(Panel(a.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Przeciwnik: Unik");
            await Expect(Panel(b.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Przeciwnik: Strzał");

            // Runda 2: oba ruchy "jednocześnie".
            await Task.WhenAll(Card(a.Page, "reload").ClickAsync(), Card(b.Page, "reload").ClickAsync());
            await Expect(History(a.Page)).ToHaveCountAsync(2);
            await Expect(History(b.Page)).ToHaveCountAsync(2);
            await Expect(Panel(b.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 3 z 12");

            // Stary pakiet i powtórzony ruch nie zmieniają stanu.
            var id = await DuelIdAsync(a.Page);
            Assert.Equal("duel-stale-round", await InvokeCodeAsync(a.Page, $"async () => (await window.sansPostDuel.invoke('SubmitMove', '{id}', 1, 'dodge')).code"));
            Assert.Null(await InvokeCodeAsync(a.Page, $"async () => (await window.sansPostDuel.invoke('SubmitMove', '{id}', 3, 'block')).code"));
            Assert.Equal("duel-move-already-submitted", await InvokeCodeAsync(a.Page, $"async () => (await window.sansPostDuel.invoke('SubmitMove', '{id}', 3, 'dodge')).code"));
            await Expect(History(b.Page)).ToHaveCountAsync(2);
            await Expect(Panel(b.Page).Locator(".game-opponent .game-ready")).ToHaveTextAsync("Karta wybrana");
        }

        // ---- 18, 19, 26, 27: odświeżenie strony, druga karta, własna karta po powrocie ----------------------------------------

        [Fact]
        public async Task RefreshAfterOwnMove_AndSecondTab_RestoreTheSameDuel()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;

            await Card(a.Page, "block").ClickAsync();
            await Expect(Panel(b.Page).Locator(".game-opponent .game-ready")).ToHaveTextAsync("Karta wybrana");

            await a.Page.ReloadAsync();
            await Expect(Panel(a.Page)).ToBeVisibleAsync(new() { Timeout = SceneStartTimeout });
            await ConnectedAsync(a.Page);
            await Expect(Panel(a.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
            await Expect(Card(a.Page, "block")).ToContainTextAsync("Twój wybór");          // własna karta — tylko u siebie
            await Expect(Panel(b.Page).Locator(".game-opponent .game-connection-state")).ToHaveTextAsync("połączony");
            await Expect(Panel(b.Page).Locator(".game-reveal")).ToHaveCountAsync(0);

            // Druga karta przeglądarki tego samego gracza: ten sam stan; zamknięcie jej nie rozłącza gracza.
            var tab = await a.Context.NewPageAsync();
            await Ui.GotoAsync(tab, "/saloon?game=table&scene=3d");
            await Expect(Panel(tab)).ToBeVisibleAsync(new() { Timeout = SceneStartTimeout });
            await ConnectedAsync(tab);
            await Expect(Card(tab, "block")).ToContainTextAsync("Twój wybór");
            await tab.CloseAsync();
            await b.Page.WaitForTimeoutAsync(500);
            await Expect(Panel(b.Page).Locator(".game-opponent .game-connection-state")).ToHaveTextAsync("połączony");

            await Card(b.Page, "shoot").ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Przeciwnik: Strzał");
            await Expect(Panel(a.Page).Locator(".game-effects")).ToContainTextAsync("Twój blok zatrzymał strzał.");
        }

        // ---- 20, 21: rozłączenie → okno powrotu → powrót, gra trwa -------------------------------------------------------------

        [Fact]
        public async Task Disconnect_OpponentWaits_ReturnWithinGrace_DuelContinues()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;

            await a.Page.CloseAsync();

            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik utracił połączenie. Czekamy na powrót…");
            await Expect(Panel(b.Page).Locator(".game-opponent .game-connection-state")).ToHaveTextAsync("utracił połączenie");
            await Expect(Panel(b.Page).Locator(".game-grace .game-countdown")).ToHaveTextAsync(new Regex(@"^\d+ s$"));
            await Expect(Panel(b.Page).Locator(".game-timer")).ToHaveCountAsync(0);        // zegar rundy stoi

            var back = await a.Context.NewPageAsync();
            await Ui.GotoAsync(back, "/saloon?game=table&scene=3d");
            await Expect(Panel(back)).ToBeVisibleAsync(new() { Timeout = SceneStartTimeout });
            await ConnectedAsync(back);

            await Expect(Panel(b.Page).Locator(".game-opponent .game-connection-state")).ToHaveTextAsync("połączony");
            await Expect(Panel(b.Page).Locator(".game-timer")).ToBeVisibleAsync();
            await Expect(Panel(back).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
            await Card(back, "reload").ClickAsync();
            await Card(b.Page, "reload").ClickAsync();
            await Expect(History(b.Page)).ToHaveCountAsync(1);
        }

        // ---- 23, 28, 30: poddanie, koniec gry, rewanż za zgodą obu, powrót do sali --------------------------------------------

        [Fact]
        public async Task Surrender_GameOver_RematchNeedsBoth_BackToHall()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;
            await Card(a.Page, "reload").ClickAsync();
            await Card(b.Page, "taunt").ClickAsync();
            await Expect(History(a.Page)).ToHaveCountAsync(1);

            await Panel(a.Page).Locator(".game-surrender").ClickAsync();
            await Expect(Panel(a.Page).Locator("#game-surrender-question")).ToHaveTextAsync("Oddać pojedynek? Wygra przeciwnik.");
            await Panel(a.Page).Locator(".game-surrender-confirm").ClickAsync();

            await Expect(Panel(a.Page).Locator(".game-over-title")).ToHaveTextAsync("PORAŻKA");
            await Expect(Panel(b.Page).Locator(".game-over-title")).ToHaveTextAsync("ZWYCIĘSTWO");
            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik poddał pojedynek — wygrana walkowerem.");
            await Expect(Panel(b.Page).Locator(".game-over-summary")).ToContainTextAsync("Rozegrane: 1 runda.");
            await Expect(History(b.Page)).ToHaveCountAsync(1);
            foreach (var card in new[] { "shoot", "dodge", "reload", "block", "taunt" })
                await Expect(Card(b.Page, card)).ToBeDisabledAsync();

            // Rewanż: jeden głos nie wystarcza.
            await Panel(a.Page).Locator(".game-rematch").ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-over")).ToContainTextAsync("Czekamy, aż przeciwnik zgodzi się na rewanż");
            await Expect(Panel(b.Page).Locator(".game-over")).ToContainTextAsync("Przeciwnik proponuje rewanż.");
            await Expect(Panel(a.Page).Locator(".game-ready-button")).ToHaveCountAsync(0);

            await Panel(b.Page).GetByRole(AriaRole.Button, new() { Name = "Przyjmij rewanż" }).ClickAsync();
            foreach (var page in new[] { a.Page, b.Page })
            {
                await Expect(Panel(page).Locator(".game-board-title")).ToContainTextAsync("Przygotowanie do pojedynku");
                await Expect(History(page)).ToHaveCountAsync(0);                               // nowy pojedynek, nie reset starego
            }

            await Panel(a.Page).Locator(".game-surrender").ClickAsync();
            await Panel(a.Page).Locator(".game-surrender-confirm").ClickAsync();
            await Panel(b.Page).GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
            await Expect(b.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await Expect(Panel(a.Page).Locator(".game-over")).ToContainTextAsync("Przeciwnik opuścił stół.");
        }

        // ---- 24, 25: konto zawieszone — nie wyzwie, nie zagra; stan pojedynku nietknięty -----------------------------------------

        [Fact]
        public async Task Suspended_CannotChallengeOrMove_ServerRejects_OpponentStateUntouched()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;
            await Card(a.Page, "shoot").ClickAsync();
            await Expect(Panel(b.Page).Locator(".game-opponent .game-ready")).ToHaveTextAsync("Karta wybrana");

            await Api.AdminAsync(_env.Main, $"/api/moderation/users/{b.User!.Id}/suspend");
            await Ui.LoginAsync(b.Page, b.User.Email, E2EEnvironment.UserPassword);   // stara sesja unieważniona
            // Logowanie wraca do sali (/saloon) — stół w tej samej, już załadowanej sali, bez drugiego startu sceny 3D:
            // B musi wrócić w oknie powrotu (20 s), a każdy start sceny w WebGL programowym to ~8 s.
            await OpenTableInLoadedHallAsync(b.Page);

            await Expect(Panel(b.Page).Locator(".game-restricted")).ToHaveTextAsync("Możesz przeglądać Stół gry, ale udział w pojedynkach jest obecnie niedostępny.");
            await Expect(Panel(b.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");   // odczyt własnego pojedynku
            await Expect(Card(b.Page, "dodge")).ToBeDisabledAsync();
            var id = await DuelIdAsync(b.Page);
            Assert.Equal("account-suspended", await InvokeCodeAsync(b.Page, $"async () => (await window.sansPostDuel.invoke('SubmitMove', '{id}', 1, 'dodge')).code"));
            Assert.Equal("account-suspended", await InvokeCodeAsync(b.Page, @"async () => {
                const players = await window.sansPostDuel.invoke('GetPlayers');
                return players.length ? (await window.sansPostDuel.invoke('ChallengeUser', players[0].handle)).code : 'account-suspended'; }"));

            await Expect(Panel(a.Page).Locator(".game-board-title")).ToContainTextAsync("Runda 1 z 12");
            await Expect(History(a.Page)).ToHaveCountAsync(0);
            await Expect(Card(a.Page, "shoot")).ToContainTextAsync("Twój wybór");
        }

        // ---- 6: wygaśnięcie wyzwania; 15–17, 29: czas rundy, kary, remis; 22: walkower po oknie powrotu (krótkie czasy) ---------

        [Fact]
        public async Task Fast_ChallengeExpires_BothSidesUpdated()
        {
            var server = await FastServerAsync();
            await using var a = await PlayerAsync(server);
            await using var b = await PlayerAsync(server);
            await OpenTableAsync(a.Page);
            await OpenTableAsync(b.Page);

            await Challenge(a.Page, b.User!.Alias).ClickAsync();
            await Expect(Invite(b.Page, a.User!.Alias)).ToBeVisibleAsync();

            await Expect(Panel(a.Page).Locator(".game-lobby-note")).ToHaveTextAsync($"Wyzwanie do {b.User.Alias} wygasło bez odpowiedzi.", new() { Timeout = 10_000 });
            await Expect(Panel(b.Page).Locator(".game-lobby-note")).ToHaveTextAsync($"Wyzwanie od {a.User.Alias} wygasło.");
            await Expect(Panel(b.Page).Locator(".game-invite")).ToHaveCountAsync(0);
        }

        [Fact]
        public async Task Fast_ServerDeadline_OneTimeout_ThenBothTimeout()
        {
            var (a, b) = await DuelAsync(await FastServerAsync());
            await using var _a = a;
            await using var _b = b;
            await Expect(Panel(b.Page).Locator(".game-timer")).ToHaveTextAsync(new Regex(@"^[1-5] s$"));

            await Card(a.Page, "reload").ClickAsync();   // B nie wybiera

            await Expect(History(b.Page)).ToHaveCountAsync(1, new() { Timeout = 10_000 });
            await Expect(Panel(b.Page).Locator(".game-effects")).ToContainTextAsync("Czas minął — bez karty tracisz 1 prestiżu.");
            await Expect(Panel(b.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Ty: Brak ruchu");
            await Expect(Panel(b.Page).Locator(".game-you .game-player-stat").First).ToContainTextAsync("Prestiż: 2.");
            await Expect(Panel(a.Page).Locator(".game-you .game-player-stat").First).ToContainTextAsync("Prestiż: 3.");
            await Expect(Panel(a.Page).Locator(".game-effects")).ToContainTextAsync("Przeciwnik nie zdążył wybrać karty — traci 1 prestiżu.");

            // Obaj bez karty: obaj tracą.
            await Expect(History(a.Page)).ToHaveCountAsync(2, new() { Timeout = 10_000 });
            await Expect(Panel(a.Page).Locator(".game-you .game-player-stat").First).ToContainTextAsync("Prestiż: 2.");
            await Expect(Panel(b.Page).Locator(".game-you .game-player-stat").First).ToContainTextAsync("Prestiż: 1.");
            await Expect(Panel(b.Page).Locator(".game-reveal-cards")).ToContainTextAsync("Przeciwnik: Brak ruchu");
        }

        [Fact]
        public async Task Fast_TripleDoubleTimeout_IsADraw()
        {
            var (a, b) = await DuelAsync(await FastServerAsync());
            await using var _a = a;
            await using var _b = b;

            await Expect(Panel(a.Page).Locator(".game-over-title")).ToHaveTextAsync("REMIS", new() { Timeout = 25_000 });
            await Expect(Panel(b.Page).Locator(".game-over-title")).ToHaveTextAsync("REMIS");
            await Expect(History(b.Page)).ToHaveCountAsync(3);
            await Expect(Status(a.Page)).ToHaveTextAsync("Remis — obaj rewolwerowcy zostają w grze.");
        }

        [Fact]
        public async Task Fast_GraceExpires_Forfeit()
        {
            var (a, b) = await DuelAsync(await FastServerAsync());
            await using var _a = a;
            await using var _b = b;

            await a.Context.CloseAsync();

            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik utracił połączenie. Czekamy na powrót…");
            await Expect(Panel(b.Page).Locator(".game-over-title")).ToHaveTextAsync("ZWYCIĘSTWO", new() { Timeout = 10_000 });
            await Expect(Status(b.Page)).ToHaveTextAsync("Przeciwnik nie wrócił do stołu — wygrana walkowerem.");
        }

        // ---- 31, 32: mobile 390 / 360 ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(390, 844)]
        [InlineData(360, 740)]
        public async Task Mobile_LiveBoardFits_TouchTargets(int width, int height)
        {
            var (a, b) = await DuelAsync(width: width, height: height);
            await using var _a = a;
            await using var _b = b;
            await Card(a.Page, "shoot").ClickAsync();
            await Card(b.Page, "dodge").ClickAsync();
            await Expect(History(a.Page)).ToHaveCountAsync(1);
            await Panel(a.Page).EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");

            foreach (var page in new[] { a.Page, b.Page })
            {
                var sheet = (await Panel(page).BoundingBoxAsync())!;
                Assert.True(Math.Abs(sheet.X) < 0.5 && Math.Abs(sheet.Width - width) < 0.5, $"Arkusz na całą szerokość: {sheet.X} + {sheet.Width}.");
                await Ui.AssertNoHorizontalOverflowAsync(page, $"pojedynek na żywo {width}");
                var small = await page.EvaluateAsync<string[]>(
                    "() => [...document.querySelectorAll('dialog[open] button, dialog[open] a, dialog[open] summary')].filter(e => e.getClientRects().length && e.getBoundingClientRect().height < 44).map(e => e.className || e.tagName)");
                Assert.True(small.Length == 0, "Cele dotyku < 44 px: " + string.Join(", ", small));
            }
            await Expect(Panel(a.Page).Locator(".game-timer")).ToBeVisibleAsync();
        }

        // ---- 33: klawiatura — przyjęcie, GOTOWY, karta, poddanie bez myszy -----------------------------------------------------

        [Fact]
        public async Task Keyboard_AcceptReadyPlay_WithoutMouse()
        {
            await using var a = await PlayerAsync();
            await using var b = await PlayerAsync();
            await OpenTableAsync(a.Page);
            await OpenTableAsync(b.Page);
            await Challenge(a.Page, b.User!.Alias).ClickAsync();

            var accept = Invite(b.Page, a.User!.Alias).GetByRole(AriaRole.Button, new() { Name = "Przyjmij" });
            await accept.FocusAsync();
            await b.Page.Keyboard.PressAsync("Enter");
            await Expect(Panel(b.Page).Locator(".game-ready-button")).ToBeFocusedAsync();   // fokus prowadzi do "GOTOWY"
            await b.Page.Keyboard.PressAsync("Enter");
            await Panel(a.Page).Locator(".game-ready-button").ClickAsync();
            await Expect(Card(b.Page, "shoot")).ToBeFocusedAsync();                         // runda 1 — pierwsza karta

            await b.Page.Keyboard.PressAsync("Tab");
            await Expect(Card(b.Page, "dodge")).ToBeFocusedAsync();
            await b.Page.Keyboard.PressAsync("Space");
            await Expect(Card(b.Page, "dodge")).ToContainTextAsync("Twój wybór");
            // Wybrana karta staje się nieaktywna — fokus wraca do okna stołu (nie ginie na body pod oknem).
            await b.Page.WaitForFunctionAsync("() => !!document.activeElement && !!document.activeElement.closest('dialog[open]')");

            for (var i = 0; i < 12; i++)
            {
                await b.Page.Keyboard.PressAsync("Tab");
                Assert.True(await b.Page.EvaluateAsync<bool>("() => { const a = document.activeElement; return !a || a === document.body || !!a.closest('dialog[open]'); }"),
                    $"Tab #{i + 1} trafił do sali pod oknem.");
            }
            await Expect(Panel(b.Page).Locator(".game-timer")).ToHaveAttributeAsync("role", "timer");
            await Expect(Status(b.Page)).ToHaveAttributeAsync("role", "status");
        }

        // ---- 34: reduced motion — wynik rundy od razu, bez animacji odsłonięcia -----------------------------------------------

        [Fact]
        public async Task ReducedMotion_RevealShownImmediately()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;
            // Preferencja systemu przy otwartym stole (sala przy reduced motion od startu wybiera inny tryb sceny).
            await a.Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await Card(a.Page, "reload").ClickAsync();
            await Card(b.Page, "shoot").ClickAsync();

            await Expect(Panel(a.Page).Locator(".game-reveal")).ToBeVisibleAsync();
            var running = await a.Page.EvaluateAsync<int>("() => document.querySelector('dialog[open] .game-reveal').getAnimations({ subtree: true }).length");
            Assert.Equal(0, running);
            await Expect(Panel(a.Page).Locator(".game-effects")).ToContainTextAsync("Trafienie przerwało Twoje przeładowanie.");
        }

        // ---- 35: axe — lobby z zaproszeniem, gotowość, trwająca runda z odsłonięciem, koniec gry (jasny i ciemny) ------------

        [Fact]
        public async Task Axe_LiveDuel_NoCriticalOrSerious()
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

            var (a, b) = await DuelAsync(ready: false);
            await using var _a = a;
            await using var _b = b;
            await AuditAsync(a.Page, "gotowość (jasny)");
            await b.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await Panel(a.Page).Locator(".game-ready-button").ClickAsync();
            await Panel(b.Page).Locator(".game-ready-button:not([disabled])").ClickAsync();
            await Card(a.Page, "shoot").ClickAsync();
            await Card(b.Page, "reload").ClickAsync();
            await Expect(History(b.Page)).ToHaveCountAsync(1);
            await AuditAsync(b.Page, "runda z odsłonięciem (ciemny)");
            await Panel(b.Page).Locator(".game-surrender").ClickAsync();
            await AuditAsync(b.Page, "pytanie o poddanie (ciemny)");
            await Panel(b.Page).Locator(".game-surrender-confirm").ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-over-title")).ToHaveTextAsync("ZWYCIĘSTWO");
            await AuditAsync(a.Page, "koniec gry (jasny)");
            await AuditAsync(b.Page, "koniec gry (ciemny)");

            await using var c = await PlayerAsync();
            await OpenTableAsync(c.Page);
            await Panel(a.Page).GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
            // "Wróć do sali" cofa historię (okno się zamyka) — dopiero potem nowa nawigacja, bez przerwania tamtej.
            await Expect(a.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await a.Page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            await OpenTableAsync(a.Page);
            await Challenge(c.Page, a.User!.Alias).ClickAsync();
            await Expect(Invite(a.Page, c.User!.Alias)).ToBeVisibleAsync();
            await AuditAsync(a.Page, "lobby z zaproszeniem (jasny)");
            await AuditAsync(c.Page, "lobby z wysłanym wyzwaniem (jasny)");

            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));
        }

        // ---- Sprint 21: gwiazdka za zwycięstwo, ranking stołu, Mistrz Stołu w sali, pojedynki w profilu ------------------

        [Fact]
        public async Task Prestige_WinnerStar_TableRanking_ChampionPlaque_ProfileDuels()
        {
            var (a, b) = await DuelAsync();
            await using var _a = a;
            await using var _b = b;

            await Panel(b.Page).Locator(".game-surrender").ClickAsync();
            await Panel(b.Page).Locator(".game-surrender-confirm").ClickAsync();
            await Expect(Panel(a.Page).Locator(".game-over-title")).ToHaveTextAsync("ZWYCIĘSTWO");
            await Expect(Panel(a.Page).Locator(".game-over-star")).ToContainTextAsync("+1");
            await Expect(Panel(a.Page).Locator(".game-over-star")).ToContainTextAsync("masz teraz 1");   // po zapisie wyniku
            await Expect(Panel(b.Page).Locator(".game-over-star")).ToHaveCountAsync(0);

            // Lobby po powrocie: własne statystyki i Top 10 z przydomkiem zwycięzcy (bez e-maili).
            await Panel(a.Page).GetByRole(AriaRole.Button, new() { Name = "Wróć do sali" }).ClickAsync();
            await Expect(a.Page.Locator("dialog[open]")).ToHaveCountAsync(0);
            await a.Page.WaitForFunctionAsync("() => window.__sansPostHall && window.__sansPostHall.area === null && window.__sansPostHall.moving === false");
            await OpenTableAsync(a.Page);
            await Expect(Panel(a.Page).Locator(".game-you-stats")).ToContainTextAsync("★ 1");
            await Expect(Panel(a.Page).Locator(".game-you-stats")).ToContainTextAsync("wygrane 1");
            await Expect(Panel(a.Page).Locator(".game-leaderboard tbody")).ToContainTextAsync(a.User!.Alias);
            await Expect(Panel(a.Page).Locator(".game-standings")).Not.ToContainTextAsync("@");

            // Sala: plakietka Mistrza Stołu w scenie (canvas) i ten sam tekst dla czytnika przy strefie "Stół gry".
            await Ui.GotoAsync(b.Page, "/saloon?scene=3d");
            await Expect(b.Page.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = SceneStartTimeout });
            await b.Page.WaitForFunctionAsync("() => !!window.__sansPostHall.champion");
            await Expect(b.Page.Locator("#hall-champion")).ToContainTextAsync("Mistrz Stołu:");
            await Expect(b.Page.Locator(".hall-zone[data-zone='game']")).ToHaveAttributeAsync("aria-describedby", "hall-champion");

            // Profil: sekcja "Pojedynki" (gwiazdki = zwycięstwa).
            await Ui.GotoAsync(b.Page, $"/u/{a.User.Alias}");
            var duels = b.Page.Locator(".profile-duels");
            await Expect(duels.Locator("#profile-duels-title")).ToHaveTextAsync("Pojedynki");
            await Expect(duels.Locator(".profile-duel-stat").Filter(new() { HasText = "Wygrane" }).Locator("dd")).ToHaveTextAsync("1");
            await Expect(duels.Locator(".profile-duel-stat.is-stars dd")).ToHaveTextAsync("1");
        }
    }
}
