using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Ui
{
    // Sprint 8C: konfiguracja treści startowych per środowisko i pasek kategorii.
    [Trait("Category", "Ui")]
    public class DemoConfigAndCategoryStripTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public DemoConfigAndCategoryStripTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private static PublicDemoOptions LoadOptions(params string[] files)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SansPost", "appsettings.json")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            var builder = new ConfigurationBuilder();
            foreach (var file in files)
                builder.AddJsonFile(Path.Combine(dir!.FullName, "SansPost", file), optional: false);

            return builder.Build().GetSection(PublicDemoOptions.SectionName).Get<PublicDemoOptions>() ?? new PublicDemoOptions();
        }

        // 1 — Development: treści startowe włączone; domyślnie (produkcja): wyłączone, tylko świadoma konfiguracja.
        [Fact]
        public void SeedContent_IsOnInDevelopment_AndOffByDefault()
        {
            Assert.False(LoadOptions("appsettings.json").SeedContent);
            Assert.True(LoadOptions("appsettings.json", "appsettings.Development.json").SeedContent);
        }

        // 6 — pasek kategorii: wszystkie kategorie (łącznie z ostatnią — Feedback) w jednym przewijanym pasku
        // oraz dostępne przyciski przewijania. Faktyczne przewijanie sprawdzone w przeglądarce (raport 8C).
        [Fact]
        public async Task CategoryStrip_ContainsEveryCategory_AndAccessibleScrollButtons()
        {
            foreach (var path in new[] { "/saloon", "/c/general" })
            {
                var html = await _factory.CreateHttpsClient().GetStringAsync(path);
                var strip = Regex.Match(html, "<div class=\"sign-strip\".*?</nav>.*?</button>", RegexOptions.Singleline).Value;

                Assert.Contains("kategorie w lewo\"", strip);   // "Przewiń" — polskie znaki kodowane encjami
                Assert.Contains("kategorie w prawo\"", strip);
                Assert.Contains("data-scroller-track", strip);
                foreach (var category in Enum.GetValues<PostCategory>())
                    Assert.Contains($"href=\"/c/{category.ToString().ToLowerInvariant()}\"", strip);
            }
        }
    }
}
