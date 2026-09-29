using System.Collections;
using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SansPost.Features.Duels;
using SansPost.Features.Moderation;
using SansPost.Features.Posts;
using SansPost.Localization;
using SansPost.Shared.Ui;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Localization
{
    // Sprint 22: PL/EN. Kluczem zasobu jest polski tekst interfejsu — test kompletności skanuje źródła (Razor, komunikaty
    // serwisów) i pilnuje, żeby każdy tekst dla użytkownika miał tłumaczenie EN z tymi samymi parametrami ({0}, {1}).
    [Trait("Category", "Ui")]
    public class LocalizationTests
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
        private static readonly Regex PolishLetter = new("[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]");
        private static readonly Regex Placeholder = new(@"\{\d\}");

        // Zakres wydania (świadomy): publiczny / użytkowy SansPost = PL i EN, panel administratora
        // (Pages/AdminModeration.razor) = tylko PL — narzędzie wewnętrzne, dlatego wyłączony z kompletności EN.
        private static readonly string[] ExcludedFiles = { "AdminModeration.razor" };

        private static Dictionary<string, string> EnglishResources()
        {
            var set = new ResourceManager("SansPost.Resources.Ui", typeof(Loc).Assembly).GetResourceSet(CultureInfo.GetCultureInfo("en"), createIfNotExists: true, tryParents: false);
            Assert.NotNull(set);
            return set!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        }

        private static string AppRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SansPost.sln")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "SansPost");
        }

        private static IEnumerable<string> SourceFiles(string pattern, params string[] folders) =>
            (folders.Length == 0 ? new[] { AppRoot() } : folders.Select(f => Path.Combine(AppRoot(), f)))
                .SelectMany(d => Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !ExcludedFiles.Contains(Path.GetFileName(f)));

        private static string Unescape(string literal) => literal.Replace("\\\"", "\"").Replace("\\n", "\n");

        // Interpolacja $"… {expr} …" → klucz z parametrami "… {0} …" (tak jak w zasobach).
        private static string ToKey(string literal, bool interpolated)
        {
            if (!interpolated)
                return Unescape(literal);
            var index = 0;
            return Unescape(Regex.Replace(literal, @"\{[^{}]+\}", _ => "{" + index++ + "}"));
        }

        private static readonly Regex Literal = new(@"(\$?)""((?:[^""\\\n]|\\.)*)""");

        private static bool IsCodeNoise(string line) =>
            Regex.IsMatch(line, @"^\s*(//|\*|@\*)|Log(Warning|Error|Information|Debug|Critical|Trace)\(|throw new|OptionsValidation|ValidateOnStart|\[(Required|StringLength|Range)");

        // Teksty interfejsu w Razor: L["…"], L.Format("…"), L.Plural(…, "…"), literały w parametrach komponentów
        // (Title/Text/Label/Kicker…) i polskie literały w @code (menu, strefy, komunikaty przekazywane do L[…]).
        private static IEnumerable<(string File, string Key)> RazorTexts()
        {
            foreach (var file in SourceFiles("*.razor"))
            {
                var source = Regex.Replace(File.ReadAllText(file), @"@\*.*?\*@", "", RegexOptions.Singleline);
                var name = Path.GetFileName(file);
                foreach (Match m in Regex.Matches(source, @"\bL\[""((?:[^""\\]|\\.)*)""\]"))
                    yield return (name, Unescape(m.Groups[1].Value));
                foreach (Match m in Regex.Matches(source, @"\bL\.Format\(""((?:[^""\\]|\\.)*)"""))
                    yield return (name, Unescape(m.Groups[1].Value));
                foreach (Match m in Regex.Matches(source, @"\b(?:Title|Text|Label|Kicker|ConfirmLabel|Hint)=""([^""@][^""]*)"""))
                    if (PolishLetter.IsMatch(m.Groups[1].Value) || m.Groups[1].Value.Contains(' '))
                        yield return (name, m.Groups[1].Value);
                var code = source.IndexOf("@code", StringComparison.Ordinal);
                if (code < 0)
                    continue;
                foreach (var line in source[code..].Split('\n'))
                {
                    if (IsCodeNoise(line))
                        continue;
                    foreach (Match m in Literal.Matches(line))
                        if (PolishLetter.IsMatch(m.Groups[2].Value))
                            yield return (name, ToKey(m.Groups[2].Value, m.Groups[1].Value == "$"));
                }
            }
        }

        // Komunikaty z serwisów i kontrolerów (ServiceResult.Fail, walidacja kontraktów) oraz stałe tekstów UI w C#.
        // Pomijane: logi, wyjątki konfiguracji, treści demo (to treść użytkowników, nie interfejs), telemetria.
        private static IEnumerable<(string File, string Key)> CSharpTexts()
        {
            var files = SourceFiles("*.cs", "Features", "Controllers", "Hubs", "Shared", "Localization")
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Demo{Path.DirectorySeparatorChar}") && !f.EndsWith("Loc.cs"));
            foreach (var file in files)
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (IsCodeNoise(line))
                        continue;
                    foreach (Match m in Literal.Matches(line))
                    {
                        var value = m.Groups[2].Value;
                        if (PolishLetter.IsMatch(value) && !value.StartsWith("^"))
                            yield return (Path.GetFileName(file), ToKey(value, m.Groups[1].Value == "$"));
                    }
                }
            }
        }

        [Fact]
        public void EveryUserFacingText_HasEnglishTranslation()
        {
            var missing = RazorTexts().Concat(CSharpTexts())
                .Where(t => !Loc.HasTranslation(t.Key))
                .Select(t => $"{t.File}: {t.Key}")
                .Distinct()
                .ToList();

            Assert.True(missing.Count == 0, "Brak tłumaczeń EN:\n" + string.Join("\n", missing));
        }

        [Fact]
        public void DomainCatalogs_AreTranslated()
        {
            foreach (var category in Enum.GetValues<PostCategory>())
                Assert.True(Loc.HasTranslation(category.DisplayName()), category.DisplayName());
            foreach (var card in DuelMapper.Catalog)
            {
                Assert.True(Loc.HasTranslation(card.Name), card.Name);
                Assert.True(Loc.HasTranslation(card.Description), card.Description);
            }
            foreach (var reason in Enum.GetValues<ReportReason>())
                Assert.True(Loc.HasTranslation(ReportDialog.Label(reason)), ReportDialog.Label(reason));
            foreach (var field in typeof(UiErrors).GetFields().Concat(typeof(AuthText).GetFields()).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                Assert.True(Loc.HasTranslation((string)field.GetRawConstantValue()!), field.Name);
        }

        [Fact]
        public void Translations_KeepPlaceholders_AndAreNotEmptyOrPolish()
        {
            var problems = new List<string>();
            foreach (var (key, value) in EnglishResources())
            {
                var keyArgs = Placeholder.Matches(key).Select(m => m.Value).OrderBy(v => v);
                var valueArgs = Placeholder.Matches(value).Select(m => m.Value).OrderBy(v => v);
                if (!keyArgs.SequenceEqual(valueArgs))
                    problems.Add($"parametry: {key} → {value}");
                if (string.IsNullOrWhiteSpace(value))
                    problems.Add($"puste: {key}");
                if (PolishLetter.IsMatch(value))
                    problems.Add($"polskie znaki w EN: {key} → {value}");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void GameTerms_UseAgreedEnglishNames()
        {
            var expected = new Dictionary<string, string>
            {
                ["Śladem Rewolwerowca"] = "The Gunslinger's Draw", ["Strzał"] = "Shoot", ["Unik"] = "Dodge", ["Przeładowanie"] = "Reload",
                ["Blok"] = "Block", ["Prowokacja"] = "Taunt", ["Stół gry"] = "Game Table", ["Mistrz Stołu"] = "Master of the Table",
                ["GOTOWY"] = "READY", ["Poddaj pojedynek"] = "Surrender", ["Rewanż"] = "Rematch"
            };
            foreach (var (polish, english) in expected)
                Assert.Equal(english, Loc.Translate(polish, Loc.English));
        }

        // Klucze różniące się wielkością liter są w zasobach raz; wielkość liter tłumaczenia idzie za tekstem źródłowym.
        [Fact]
        public void Translate_FollowsSourceCasing()
        {
            Assert.Equal("Game Table", Loc.Translate("Stół gry", Loc.English));
            Assert.Equal("GAME TABLE", Loc.Translate("STÓŁ GRY", Loc.English));
            Assert.Equal("MASTER OF THE TABLE", Loc.Translate("MISTRZ STOŁU", Loc.English));
            Assert.Equal("READY", Loc.Translate("GOTOWY", Loc.English));
            Assert.Equal("wins", Loc.Translate("wygrane", Loc.English));
            Assert.Equal("Wins", Loc.Translate("Wygrane", Loc.English));
        }

        [Fact]
        public void Translate_PolishIsIdentity_EnglishFromResources_UnknownFallsBack()
        {
            Assert.Equal("Zaloguj się", Loc.Translate("Zaloguj się", Loc.Polish));
            Assert.Equal("Sign in", Loc.Translate("Zaloguj się", Loc.English));
            Assert.Equal("tekst użytkownika", Loc.Translate("tekst użytkownika", Loc.English));
        }

        [Fact]
        public void ServerMessages_WithNumbers_TranslateByPattern()
        {
            var polish = UiErrors.RateLimited(TimeSpan.FromSeconds(12));
            Assert.Equal(polish, Loc.TranslateMessage(polish, Loc.Polish));
            Assert.Equal("Too many actions in a short time. Try again in 12 s.", Loc.TranslateMessage(polish, Loc.English));
            Assert.Equal("Post limit reached (5).", Loc.TranslateMessage("Osiągnięto limit postów (5).", Loc.English));
            Assert.Equal("Coś zupełnie nowego.", Loc.TranslateMessage("Coś zupełnie nowego.", Loc.English));
        }

        [Fact]
        public void ErrorText_PrefersStableCode_ThenMessage()
        {
            Assert.Equal("No connection to the Game Table — try again in a moment.",
                Loc.ErrorText("connection-lost", "dowolny komunikat klienta", Loc.English));
            Assert.Equal("You can't challenge yourself.", Loc.ErrorText("unknown-code", "Nie możesz wyzwać samego siebie.", Loc.English));
            Assert.Equal("Nie możesz wyzwać samego siebie.", Loc.ErrorText("unknown-code", "Nie możesz wyzwać samego siebie.", Loc.Polish));
        }

        [Fact]
        public void Plural_UsesLanguageRules()
        {
            var pl = new Loc();
            Assert.Equal("1 post", pl.Plural(1, "post", "posty", "postów"));
            Assert.Equal("3 posty", pl.Plural(3, "post", "posty", "postów"));
            Assert.Equal("12 postów", pl.Plural(12, "post", "posty", "postów"));
            Assert.Equal("22 posty", pl.Plural(22, "post", "posty", "postów"));

            var en = new Loc();
            en.Initialize(Loc.English);
            Assert.Equal("1 post", en.Plural(1, "post", "posty", "postów"));
            Assert.Equal("12 posts", en.Plural(12, "post", "posty", "postów"));
            Assert.Equal("1,234", en.Number(1234));
        }

        [Theory]
        [InlineData("en", null, "en")]
        [InlineData("pl", "en-US,en;q=0.9", "pl")]
        [InlineData(null, "en-GB,en;q=0.9", "en")]
        [InlineData(null, "pl-PL,pl;q=0.9,en;q=0.8", "pl")]
        [InlineData(null, null, "pl")]
        [InlineData("xx", null, "pl")]
        public void FromRequest_CookieThenAcceptLanguage_DefaultPolish(string? cookie, string? accept, string expected)
        {
            var context = new DefaultHttpContext();
            if (cookie is not null)
                context.Request.Headers.Cookie = $"{Loc.CookieName}={cookie}";
            if (accept is not null)
                context.Request.Headers.AcceptLanguage = accept;
            Assert.Equal(expected, Loc.FromRequest(context));
        }

        [Fact]
        public async Task SetAsync_ChangesLanguageOnce_AndNotifies()
        {
            var loc = new Loc();
            var changes = 0;
            loc.Changed += () => changes++;

            await loc.SetAsync("en");
            await loc.SetAsync("EN");
            Assert.True(loc.IsEnglish);
            Assert.Equal("Sign in", loc["Zaloguj się"]);
            Assert.Equal(1, changes);

            await loc.SetAsync("pl");
            Assert.Equal("Zaloguj się", loc["Zaloguj się"]);
            Assert.Equal(2, changes);
        }
    }

    // Prerender w wybranym języku: cookie sp-lang → <html lang>, teksty interfejsu i szablony reconnect w EN;
    // treść użytkownika bez zmian; REST — komunikat błędu (detail) w języku żądania, kod bez zmian.
    [Trait("Category", "Ui")]
    public class LocalizedRenderingTests : IClassFixture<SansPostFactory>
    {
        private readonly SansPostFactory _factory;

        public LocalizedRenderingTests(SansPostFactory factory)
        {
            _factory = factory;
        }

        private HttpClient Client(string language)
        {
            var client = _factory.CreateHttpsClient();
            client.DefaultRequestHeaders.Add("Cookie", $"{Loc.CookieName}={language}");
            return client;
        }

        [Fact]
        public async Task Pages_RenderInCookieLanguage()
        {
            var en = await Client("en").GetStringAsync("/login");
            Assert.Contains("<html lang=\"en\">", en);
            Assert.Contains("Sign in", en);
            Assert.Contains("Connecting to SansPost", en);
            Assert.DoesNotContain("Zaloguj się", en);
            Assert.Contains("Connecting to SansPost…", en);

            var pl = await Client("pl").GetStringAsync("/login");
            Assert.Contains("<html lang=\"pl\">", pl);
            Assert.Contains("Zaloguj się", pl);
            Assert.DoesNotContain("Connecting to SansPost", pl);
        }

        [Fact]
        public async Task Categories_ShowTranslatedNames_BrandUnchanged()
        {
            var html = await Client("en").GetStringAsync("/categories");
            Assert.Contains("Games", html);
            Assert.Contains("Feedback", html);
            Assert.Contains("SansPost", html);
            Assert.DoesNotContain(">Gry<", html);
        }

        [Fact]
        public async Task Api_ProblemDetail_FollowsRequestLanguage_CodeUnchanged()
        {
            var response = await Client("en").GetAsync("/api/search/posts?q=a");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("The query must be", body);
            Assert.DoesNotContain("Zapytanie musi", body);
        }
    }
}
