using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // axe-core na głównych ekranach: 0 critical / 0 serious. Moderate/minor raportowane (wyjście testu + plik).
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Accessibility")]
    public class AccessibilityE2ETests
    {
        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public AccessibilityE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        [Fact]
        public async Task MainScreens_HaveNoCriticalOrSeriousViolations()
        {
            var findings = new List<string>();
            var blocking = new List<string>();

            async Task AuditAsync(IPage page, string name)
            {
                var result = await page.RunAxe();
                foreach (var violation in result.Violations)
                {
                    var line = $"{name}: [{violation.Impact}] {violation.Id} — {violation.Help} ({violation.Nodes.Length} el.: {string.Join(" | ", violation.Nodes.Take(3).Select(n => string.Join(" ", n.Target)))})";
                    findings.Add(line);
                    if (violation.Impact is "critical" or "serious")
                        blocking.Add(line);
                }
            }

            foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
            {
                await using var context = await _env.NewContextAsync(colorScheme: scheme, reducedMotion: true);
                var guest = await context.NewPageAsync();
                foreach (var (path, name) in new[] { ("/", "Entrance"), ("/saloon", "Saloon"), ("/categories", "Categories"), ("/search?q=gry", "Search"), ("/post-view/2", "Post"),
                             ("/login", "Login"), ("/register", "Register"), ("/u/DustyRaven", "Profile") })
                {
                    await Ui.GotoAsync(guest, path);
                    await AuditAsync(guest, $"{name} ({scheme})");
                }

                // Sprint 14D: karty Saloonu na Entrance (rejestracja z przydomkiem, logowanie) — po animacji otwarcia.
                await Ui.GotoAsync(guest, "/");
                foreach (var (sign, name) in new[] { ("Załóż konto", "Register card"), ("Zaloguj się", "Login card") })
                {
                    await guest.Locator("nav.entrance-signs").GetByRole(AriaRole.Link, new() { Name = sign }).ClickAsync();
                    await Expect(guest.Locator("dialog[open] .card-form")).ToBeVisibleAsync();
                    await guest.Locator("dialog[open]").EvaluateAsync("d => Promise.all(d.getAnimations({ subtree: true }).map(a => a.finished))");
                    await AuditAsync(guest, $"Entrance {name} ({scheme})");
                    await guest.Keyboard.PressAsync("Escape");
                    await Expect(guest.Locator("dialog[open]")).ToHaveCountAsync(0);
                }

                // Scena 3D (wymuszona — headless ma tylko WebGL programowy) z tabliczkami na tablicach przy drzwiach.
                await using var motion = await _env.NewContextAsync(colorScheme: scheme);
                var scene = await motion.NewPageAsync();
                await Ui.GotoAsync(scene, "/?scene=3d");
                await Expect(scene.Locator(".entrance")).ToHaveAttributeAsync("data-renderer", "3d", new() { Timeout = 20_000 });
                await scene.WaitForTimeoutAsync(700);   // przenikanie CSS → WebGL
                await AuditAsync(scene, $"Entrance 3D ({scheme})");

                // Sprint 15/16: Main Hall 3D (wymuszona) z przyciskami stref, potem BAR: Karta rozmów, lista i wyniki wyszukiwania.
                await Ui.GotoAsync(scene, "/saloon?scene=3d");
                await Expect(scene.Locator(".saloon-hall.is-ready")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
                await scene.WaitForTimeoutAsync(500);   // przenikanie Pending → sala
                await AuditAsync(scene, $"Main Hall 3D ({scheme})");
                await scene.Locator(".hall-zone[data-zone='bar']").ClickAsync();
                await Expect(scene.Locator("dialog[open] [data-bar-item='newest']")).ToBeVisibleAsync();
                await scene.Locator("dialog[open]").EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
                await AuditAsync(scene, $"BAR — Karta rozmów ({scheme})");
                await scene.Locator("dialog[open] [data-bar-item='topic-technology']").ClickAsync();
                await Expect(scene.Locator("dialog[open] .post-card").First).ToBeVisibleAsync();
                await AuditAsync(scene, $"BAR — temat ({scheme})");
                // Sprint 16B: rozmowa w oknie BAR (post, reakcje, komentarze) — z listy tematu, potem Escape do listy.
                await scene.Locator("dialog[open] .post-card-title a").First.ClickAsync();
                await Expect(scene.Locator("#bar-conversation-title")).ToBeFocusedAsync();
                await AuditAsync(scene, $"BAR — rozmowa ({scheme})");
                await scene.Keyboard.PressAsync("Escape");
                await Expect(scene.Locator("#bar-level-title")).ToHaveTextAsync("Technologia");
                // Sprint 16B-FIX: karty logowania i rejestracji w BAR (gość), potem Escape do tematu.
                await scene.Locator("dialog[open] .bar-list a[href^='/login']").First.ClickAsync();
                await Expect(scene.Locator("#bar-login-email")).ToBeFocusedAsync();
                await AuditAsync(scene, $"BAR — logowanie ({scheme})");
                await scene.Locator("dialog[open] .bar-auth .card-link").ClickAsync();
                await Expect(scene.Locator("#bar-register-email")).ToBeVisibleAsync();
                await AuditAsync(scene, $"BAR — rejestracja ({scheme})");
                await scene.Locator("#bar-register-email").FocusAsync();
                await scene.Keyboard.PressAsync("Escape");
                await Expect(scene.Locator("#bar-level-title")).ToHaveTextAsync("Technologia");
                await scene.Locator("#bar-search-input").FillAsync("rozmowa");
                await scene.Locator("#bar-search-input").PressAsync("Enter");
                await scene.WaitForFunctionAsync("() => document.querySelector('dialog[open] .search-results') && !document.querySelector('#bar-search-hint .spinner')");
                await AuditAsync(scene, $"BAR — wyniki wyszukiwania ({scheme})");

                // Sprint 17: tablica Wanted (plakaty) i rozmowa otwarta z plakatu.
                await scene.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Zamknij okno" }).ClickAsync();
                await Expect(scene.Locator("dialog[open]")).ToHaveCountAsync(0);
                await scene.WaitForFunctionAsync("() => window.__sansPostHall.moving === false && window.__sansPostHall.area === null");
                await scene.Locator(".hall-zone[data-zone='wanted']").ClickAsync();
                await Expect(scene.Locator("dialog[open] [data-wanted-post]").First).ToBeFocusedAsync();
                await scene.Locator("dialog[open]").EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
                await AuditAsync(scene, $"Wanted — tablica ({scheme})");
                await scene.Keyboard.PressAsync("Enter");
                await Expect(scene.Locator("#bar-conversation-title")).ToBeFocusedAsync();
                await AuditAsync(scene, $"Wanted — rozmowa ({scheme})");
                await scene.Keyboard.PressAsync("Escape");
                await Expect(scene.Locator("dialog[open] [data-wanted-post]").First).ToBeFocusedAsync();
                await scene.Keyboard.PressAsync("Escape");
                await Expect(scene.Locator("dialog[open]")).ToHaveCountAsync(0);
                await scene.WaitForFunctionAsync("() => window.__sansPostHall.moving === false && window.__sansPostHall.area === null");

                // Sprint 19: Stół gry (gość — zasady i podgląd kart komend) i pasek stref po onboardingu (kompaktowy).
                await scene.Locator(".hall-zone[data-zone='game']").ClickAsync();
                await Expect(scene.Locator("dialog[open].game-panel .game-card").First).ToBeVisibleAsync();
                await Expect(scene.Locator("nav.hall-zones.is-compact")).ToHaveCountAsync(1);
                await scene.WaitForFunctionAsync("() => window.__sansPostHall.moving === false");
                await scene.Locator("dialog[open]").EvaluateAsync("d => Promise.allSettled(d.getAnimations({ subtree: true }).map(a => a.finished))");
                await AuditAsync(scene, $"Stół gry — gość ({scheme})");
            }

            // Panel powiadomień (zalogowany, z powiadomieniem) i moderacja (admin, z otwartym oknem potwierdzenia).
            await using var user = await Session.UserAsync(_env, reducedMotion: true);
            var other = await _env.Main.CreateUserAsync();
            var postId = await Api.CreatePostAsync(_env.Main, user.User!, "Post do audytu dostępności");
            await Api.CommentAsync(_env.Main, other, postId, "Komentarz do audytu");
            await Ui.GotoAsync(user.Page, "/saloon");
            await user.Page.Locator("#notif-trigger").ClickAsync();
            await Expect(user.Page.Locator("#notif-panel .notif-item").First).ToBeVisibleAsync();
            await AuditAsync(user.Page, "Notifications panel");
            await Ui.GotoAsync(user.Page, $"/post-view/{postId}");
            await AuditAsync(user.Page, "Post (zalogowany)");
            await Ui.GotoAsync(user.Page, "/new");
            await AuditAsync(user.Page, "Composer");

            using (var api = await _env.Main.CreateAuthenticatedApiClientAsync(other.Email, E2EEnvironment.UserPassword))
                await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(api, "/api/reports", new { targetType = "Post", targetId = postId, reason = "Other" });
            await using var admin = await Session.AdminAsync(_env);
            await Ui.GotoAsync(admin.Page, "/admin/moderation");
            await AuditAsync(admin.Page, "Moderation");
            await admin.Page.Locator(".queue-item").First.GetByRole(AriaRole.Button, new() { Name = "Odrzuć zgłoszenie" }).ClickAsync();
            await Expect(admin.Page.Locator("dialog[open]")).ToBeVisibleAsync();
            // Audyt po zakończeniu animacji otwarcia (pop-in od opacity 0) — w trakcie axe mierzy półprzezroczyste kolory.
            await admin.Page.Locator("dialog[open]").EvaluateAsync("d => Promise.all(d.getAnimations({ subtree: true }).map(a => a.finished))");
            await AuditAsync(admin.Page, "Moderation dialog");

            foreach (var line in findings)
                _output.WriteLine(line);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "a11y-findings.txt"), findings);

            Assert.True(blocking.Count == 0, "Naruszenia critical/serious:\n" + string.Join("\n", blocking));
        }
    }
}
