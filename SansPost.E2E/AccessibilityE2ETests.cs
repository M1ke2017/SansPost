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
