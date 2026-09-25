using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Mały baseline wizualny (Chromium, Windows): jasny/ciemny/mobile. Osobna instancja z samymi treściami startowymi,
    // bez animacji; czasy względne i losowe przydomki są maskowane. Brak baseline'u → zapis i informacja w wyjściu testu.
    // Aktualizacja świadomie: usuń plik z VisualBaselines/ i uruchom test ponownie.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Visual")]
    public class VisualRegressionE2ETests : IAsyncLifetime
    {
        private const double AllowedRatio = 0.005;   // 0,5% pikseli (antyaliasing, subpiksele)

        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;
        private SansPostServer _server = null!;

        public VisualRegressionE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        public async Task InitializeAsync() =>
            _server = await _env.StartServerAsync("visual", new Dictionary<string, string?> { ["PublicDemo__SeedContent"] = "true" });

        public Task DisposeAsync() => Task.CompletedTask;

        private static string BaselineDir => Path.Combine(E2EEnvironment.FindRepoRoot(), "SansPost.E2E", "VisualBaselines");

        private async Task SnapshotAsync(IPage page, string name, params ILocator[] extraMasks)
        {
            await page.EvaluateAsync("() => document.fonts.ready");
            var masks = new List<ILocator> { page.Locator("time"), page.Locator("output#alias-value") };
            masks.AddRange(extraMasks);
            var actual = await page.ScreenshotAsync(new PageScreenshotOptions { Mask = masks, Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide });

            Directory.CreateDirectory(BaselineDir);
            var baselinePath = Path.Combine(BaselineDir, name + ".png");
            if (!File.Exists(baselinePath))
            {
                await File.WriteAllBytesAsync(baselinePath, actual);
                _output.WriteLine($"Utworzono baseline: {name}");
                return;
            }

            var result = PngDiff.Compare(await File.ReadAllBytesAsync(baselinePath), actual);
            if (!result.SameSize || result.Ratio > AllowedRatio)
            {
                var actualPath = Path.Combine(AppContext.BaseDirectory, "visual-actual", name + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
                await File.WriteAllBytesAsync(actualPath, actual);
                Assert.Fail($"Regresja wizualna '{name}': {(result.SameSize ? $"{result.Ratio:P2} pikseli różnych" : "inny rozmiar")}. Zrzut: {actualPath}");
            }

            _output.WriteLine($"{name}: {result.Ratio:P3} różnicy (limit {AllowedRatio:P1})");
        }

        [Theory]
        [InlineData("light")]
        [InlineData("dark")]
        public async Task DesktopScreens(string theme)
        {
            var scheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light;
            await using var context = await _env.NewContextAsync(_server, 1280, 800, scheme, reducedMotion: true);
            var page = await context.NewPageAsync();

            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            await SnapshotAsync(page, $"home-{theme}");

            await Ui.GotoAsync(page, "/post-view/2");
            await Expect(page.Locator(".comment").First).ToBeVisibleAsync();
            await SnapshotAsync(page, $"post-{theme}");

            if (theme == "light")
            {
                await Ui.GotoAsync(page, "/register");
                await SnapshotAsync(page, "register-light");
            }
            else
            {
                await Ui.GotoAsync(page, "/c/games");
                await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
                await SnapshotAsync(page, "category-dark");
            }
        }

        [Fact]
        public async Task MobileScreens()
        {
            await using var context = await _env.NewContextAsync(_server, 390, 844, ColorScheme.Light, reducedMotion: true);
            var page = await context.NewPageAsync();
            await Ui.GotoAsync(page, "/");
            await Expect(page.Locator(".post-card").First).ToBeVisibleAsync();
            await SnapshotAsync(page, "home-mobile-390");

            // Panel powiadomień: deterministyczne przydomki i tytuł.
            var owner = await _server.CreateUserAsync(await FreeAliasAsync("SilverFox", "SilverWren", "SilverLark"));
            var commenter = await _server.CreateUserAsync(await FreeAliasAsync("CopperWren", "CopperLark", "CopperOwl"));
            var postId = await Api.CreatePostAsync(_server, owner, "Post do zrzutu panelu");
            await Api.CommentAsync(_server, commenter, postId, "Komentarz do zrzutu");

            await using var session = await _env.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = _server.BaseUrl,
                ViewportSize = new ViewportSize { Width = 390, Height = 844 },
                ReducedMotion = ReducedMotion.Reduce,
                ColorScheme = ColorScheme.Light
            });
            var mobile = await session.NewPageAsync();
            await Ui.LoginAsync(mobile, owner.Email, E2EEnvironment.UserPassword);
            await mobile.Locator("#notif-trigger").ClickAsync();
            await Expect(mobile.Locator("#notif-panel .notif-item").First).ToBeVisibleAsync();
            await SnapshotAsync(mobile, "notifications-mobile-390", mobile.Locator(".post-list"));
        }

        // Instancja wizualna żyje przez całe uruchomienie — przy powtórce testu bierzemy pierwszy wolny przydomek z listy.
        private async Task<string> FreeAliasAsync(params string[] candidates)
        {
            using var api = _server.CreateApiClient();
            foreach (var alias in candidates)
            {
                if ((await api.GetAsync($"/api/profiles/{alias}")).StatusCode == System.Net.HttpStatusCode.NotFound)
                    return alias;
            }

            throw new InvalidOperationException("Brak wolnego przydomka do zrzutu.");
        }
    }
}
