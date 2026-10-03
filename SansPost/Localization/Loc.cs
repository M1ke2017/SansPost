using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace SansPost.Localization
{
    // Język interfejsu (Sprint 22): PL (domyślny) albo EN — jeden na circuit Blazora (scoped), wybór zapamiętany w cookie.
    //   • zasoby .NET (ResourceManager, satelity .resx): kluczem jest polski tekst interfejsu, więc PL działa bez tłumaczenia,
    //     a EN pochodzi z Resources/Ui.en.resx (brak tłumaczenia = polski tekst, test kompletności pilnuje zasobów);
    //   • kultura podana jawnie (nie CurrentUICulture wątku) — przełączenie działa w miejscu, bez przeładowania strony:
    //     sesja, stan BAR (adres), pojedynek, połączenie SignalR i odtwarzane radio zostają nienaruszone;
    //   • komponenty dziedziczące LocalizedComponentBase odświeżają się po zmianie języka (słabe referencje — bez wycieków);
    //   • treści użytkowników (posty, komentarze, przydomki) nie są tłumaczone — tylko interfejs;
    //   • zakres tego wydania (świadomy): publiczny / użytkowy SansPost = PL i EN; panel administratora
    //     (Pages/AdminModeration.razor) = tylko PL — narzędzie wewnętrzne, wyłączone z testu kompletności zasobów.
    public sealed class Loc
    {
        public const string CookieName = "sp-lang";
        public const string Polish = "pl";
        public const string English = "en";
        public static readonly IReadOnlyList<string> Supported = new[] { Polish, English };

        private static readonly ResourceManager Resources = new("SansPost.Resources.Ui", typeof(Loc).Assembly);
        private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
        private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");
        // Satelita zasobów (Ui.en.resx) ma kulturę neutralną "en"; formatowanie liczb i dat — en-GB.
        private static readonly CultureInfo EnglishResources = CultureInfo.GetCultureInfo("en");

        private readonly List<WeakReference<LocalizedComponentBase>> _components = new();

        public string Language { get; private set; } = Polish;
        public bool IsEnglish => Language == English;
        public CultureInfo Culture => IsEnglish ? EnglishCulture : PolishCulture;

        public event Action? Changed;

        public static string Normalize(string? language) =>
            string.Equals(language?.Trim(), English, StringComparison.OrdinalIgnoreCase) ? English : Polish;

        // Tłumaczenie tekstu interfejsu (klucz = tekst polski).
        public string this[string text] => Translate(text, Language);

        public string Format(string text, params object?[] args) => string.Format(Culture, this[text], args);

        // Komunikat z serwera albo ustawiany warunkowo (może być pusty): dokładny klucz, a dla komunikatów z liczbą
        // wstawioną po stronie serwera ("Spróbuj ponownie za 12 s.") — wzorzec z zasobów ("… za {0} s.").
        public string? Text(string? text) => text is null ? null : TranslateMessage(text, Language);

        public static string Translate(string text, string language) =>
            language == English ? Lookup(text) ?? text : text;

        public static string TranslateMessage(string text, string language)
        {
            if (language != English)
                return text;
            if (Lookup(text) is { } exact)
                return exact;
            foreach (var (pattern, english) in Dictionary.Value.Patterns)
            {
                var match = pattern.Match(text);
                if (match.Success)
                    return string.Format(EnglishCulture, english, match.Groups.Values.Skip(1).Select(g => (object)g.Value).ToArray());
            }
            return text;
        }

        public static bool HasTranslation(string text) => Lookup(text) is not null;

        // Słownik EN wczytany raz z satelity zasobów. Generator .resources nie odróżnia kluczy różniących się tylko
        // wielkością liter ("Stół gry" / "STÓŁ GRY"), więc w zasobach jest jeden wariant, a wielkość liter tłumaczenia
        // dopasowujemy do tekstu źródłowego: WERSALIKI → wersaliki, mała litera na początku → mała litera.
        private static string? Lookup(string text)
        {
            var dictionary = Dictionary.Value;
            if (dictionary.Exact.TryGetValue(text, out var exact))
                return exact;
            if (!dictionary.IgnoreCase.TryGetValue(text, out var other))
                return null;
            if (text.Any(char.IsLetter) && !text.Any(char.IsLower))
                return other.ToUpper(EnglishCulture);
            if (text.Length > 0 && char.IsLower(text[0]) && other.Length > 0 && char.IsUpper(other[0]))
                return other.Any(char.IsLower) ? char.ToLower(other[0], EnglishCulture) + other[1..] : other.ToLower(EnglishCulture);
            if (text.Length > 0 && char.IsUpper(text[0]) && other.Length > 0 && char.IsLower(other[0]))
                return char.ToUpper(other[0], EnglishCulture) + other[1..];
            return other;
        }

        private sealed record EnglishDictionary(
            IReadOnlyDictionary<string, string> Exact, IReadOnlyDictionary<string, string> IgnoreCase, IReadOnlyList<(Regex Pattern, string English)> Patterns);

        private static readonly Regex Placeholder = new(@"\{\d\}", RegexOptions.CultureInvariant);

        private static readonly Lazy<EnglishDictionary> Dictionary = new(() =>
        {
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var set = Resources.GetResourceSet(EnglishResources, createIfNotExists: true, tryParents: false);
            if (set is not null)
                foreach (System.Collections.DictionaryEntry entry in set)
                    if (entry.Key is string key && entry.Value is string value)
                        exact[key] = value;
            var ignoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in exact)
                ignoreCase.TryAdd(key, value);

            // Wzorce z kluczy z parametrami ("{0}", "{1}"): polski komunikat z wartością wstawioną przez serwer → EN.
            var patterns = new List<(Regex, string)>();
            foreach (var (key, value) in exact)
            {
                if (!Placeholder.IsMatch(key))
                    continue;
                var regex = "^" + Placeholder.Replace(Regex.Escape(key).Replace(@"\{", "{"), "(.+?)") + "$";
                patterns.Add((new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(50)), value));
            }
            return new EnglishDictionary(exact, ignoreCase, patterns);
        });

        // Komunikat błędu z serwera: logika zawsze po stabilnym kodzie (np. "account-suspended"), tekst dla człowieka
        // z zasobów po kodzie ("code:account-suspended"), potem po polskim komunikacie, a na końcu sam komunikat.
        public string Error(string? code, string? message) => ErrorText(code, message, Language);

        public static string ErrorText(string? code, string? message, string language)
        {
            if (language == English)
            {
                if (code is not null && Lookup("code:" + code) is { } byCode)
                    return byCode;
                if (message is not null)
                    return TranslateMessage(message, language);
            }
            return message ?? Translate("Coś poszło nie tak. Spróbuj ponownie za chwilę.", language);
        }

        // Liczebniki: PL — 1 / 2–4 (bez 12–14) / 5+; EN — 1 / wiele. Formy podawane po polsku, angielskie z zasobów.
        public string Plural(int count, string one, string few, string many)
        {
            var number = count.ToString("N0", Culture);
            if (IsEnglish)
                return $"{number} {this[count == 1 ? one : many]}";
            var form = count == 1 ? one
                : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? few
                : many;
            return $"{number} {form}";
        }

        public string Number(int value) => value.ToString("N0", Culture);

        // Język z żądania HTTP (REST, prerender): cookie wyboru, potem Accept-Language; domyślnie polski.
        public static string FromRequest(HttpContext? context)
        {
            if (context?.Request.Cookies[CookieName] is { } cookie)
                return Normalize(cookie);
            var accept = context?.Request.Headers.AcceptLanguage.ToString();
            return !string.IsNullOrEmpty(accept) && accept.TrimStart().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Polish;
        }

        // Pierwsze ustawienie (z cookie przy prerenderze) — bez odświeżania, drzewo dopiero powstaje.
        public void Initialize(string? language) => Language = Normalize(language);

        public async Task SetAsync(string language)
        {
            var next = Normalize(language);
            if (next == Language)
                return;
            Language = next;
            Changed?.Invoke();

            List<LocalizedComponentBase> alive;
            lock (_components)
            {
                _components.RemoveAll(w => !w.TryGetTarget(out _));
                alive = _components.Select(w => w.TryGetTarget(out var c) ? c : null).OfType<LocalizedComponentBase>().ToList();
            }
            foreach (var component in alive)
                await component.RefreshAfterLanguageChangeAsync();
        }

        internal void Track(LocalizedComponentBase component)
        {
            lock (_components)
            {
                if (_components.Count > 64)
                    _components.RemoveAll(w => !w.TryGetTarget(out _));
                _components.Add(new WeakReference<LocalizedComponentBase>(component));
            }
        }
    }

    // Baza komponentów z tekstem interfejsu (_Imports.razor): L["…"] i odświeżenie po zmianie języka.
    public abstract class LocalizedComponentBase : ComponentBase
    {
        private Loc? _loc;

        [Inject]
        protected Loc L
        {
            get => _loc!;
            set
            {
                _loc = value;
                value.Track(this);
            }
        }

        // Dodatkowa praca po zmianie języka poza drzewem Blazora (np. napisy w scenie 3D rysowane na canvasie).
        protected virtual Task OnLanguageChangedAsync() => Task.CompletedTask;

        internal async Task RefreshAfterLanguageChangeAsync()
        {
            try
            {
                await InvokeAsync(async () =>
                {
                    await OnLanguageChangedAsync();
                    StateHasChanged();
                });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ArgumentException or InvalidOperationException)
            {
                // Komponent już usunięty z drzewa (słaba referencja jeszcze nie zebrana) — nic do odświeżenia.
            }
        }
    }
}
