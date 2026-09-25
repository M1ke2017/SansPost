using System.Text.Json;
using Microsoft.Playwright;
using SansPost.E2E.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace SansPost.E2E
{
    // Regresja geometrii dekoracyjnej sceny (Sprint 11): słońce/księżyc nie może być przecięte linią kurzu nad sceną
    // kategorii ani nachodzić na tekst/CTA — w hero i w nagłówku kategorii, jasny i ciemny motyw, 5 szerokości.
    // Zrzuty trafiają do katalogu wyjściowego testu (frontier-shots) do ręcznego przeglądu.
    [Collection(E2ECollection.Name)]
    [Trait("Category", "E2E")]
    [Trait("Category", "Visual")]
    public class FrontierSceneE2ETests
    {
        private readonly E2EEnvironment _env;
        private readonly ITestOutputHelper _output;

        public FrontierSceneE2ETests(E2EEnvironment env, ITestOutputHelper output)
        {
            _env = env;
            _output = output;
        }

        private sealed record Box(string Name, double X, double Y, double W, double H);

        // Widoczna tarcza (słońce w jasnym, księżyc w ciemnym) + elementy, z którymi nie może kolidować.
        private const string MeasureScript = @"(container) => {
            const root = document.querySelector(container);
            const box = (name, el) => { if (!el) return null; const r = el.getBoundingClientRect(); return r.width && r.height ? { name, x: r.x, y: r.y, w: r.width, h: r.height } : null; };
            const celestial = [...root.querySelectorAll(':scope > .frontier-celestial .scene-sun, :scope > .frontier-celestial .scene-moon')]
                .find(e => getComputedStyle(e).display !== 'none');
            const others = [...root.querySelectorAll('.dust-line, h1, p, .btn, .place-sign, .eyebrow')].map((e, i) => box(e.className || e.tagName, e)).filter(Boolean);
            return { celestial: box('celestial', celestial), container: box('container', root), others,
                     overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth };
        }";

        private static bool Intersects(Box a, Box b) =>
            a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

        [Theory]
        [InlineData("light")]
        [InlineData("dark")]
        public async Task CelestialObject_IsNeverCrossedByLine_OrCoveredByContent(string theme)
        {
            var shots = Path.Combine(AppContext.BaseDirectory, "frontier-shots");
            Directory.CreateDirectory(shots);

            foreach (var (width, height) in new[] { (1440, 900), (1280, 800), (1024, 768), (768, 1024), (390, 844) })
            {
                await using var context = await _env.NewContextAsync(width: width, height: height,
                    colorScheme: theme == "dark" ? ColorScheme.Dark : ColorScheme.Light, reducedMotion: true);
                var page = await context.NewPageAsync();

                foreach (var (path, container) in new[] { ("/c/games", ".place-header"), ("/saloon", ".frontier-banner") })
                {
                    await Ui.GotoAsync(page, path);
                    await Expect(page.Locator(container).First).ToBeVisibleAsync();
                    var json = await page.EvaluateAsync<JsonElement>(MeasureScript, container);
                    var label = $"{path} {theme} {width}px";

                    Assert.False(json.GetProperty("overflow").GetBoolean(), $"Poziomy overflow: {label}");
                    Assert.True(json.GetProperty("celestial").ValueKind == JsonValueKind.Object, $"Brak widocznej tarczy: {label}");
                    var celestial = Read(json.GetProperty("celestial"));
                    var box = Read(json.GetProperty("container"));

                    // W całości wewnątrz sceny (nie przy krawędzi, nie obcięta).
                    Assert.True(celestial.X > box.X + 24 && celestial.X + celestial.W < box.X + box.W - 24
                        && celestial.Y > box.Y && celestial.Y + celestial.H < box.Y + box.H, $"Tarcza poza bezpieczną strefą: {label}");

                    foreach (var other in json.GetProperty("others").EnumerateArray().Select(Read))
                        Assert.False(Intersects(celestial, other), $"Tarcza koliduje z '{other.Name}': {label}");

                    _output.WriteLine($"{label}: tarcza {celestial.X:F0},{celestial.Y:F0} {celestial.W:F0}×{celestial.H:F0}");
                    await page.Locator(container).First.ScreenshotAsync(new() { Path = Path.Combine(shots, $"{path.Trim('/').Replace('/', '-') switch { "" => "home", var p => p }}-{theme}-{width}.png"), Animations = ScreenshotAnimations.Disabled });
                }
            }
        }

        private static Box Read(JsonElement e) =>
            new(e.GetProperty("name").GetString() ?? "", e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("w").GetDouble(), e.GetProperty("h").GetDouble());
    }
}
