using Microsoft.AspNetCore.WebUtilities;
using SansPost.Features.Posts;
using SansPost.Features.Saloon;
using SansPost.Shared.Ui;

namespace SansPost.Shared.Bar
{
    // Poziomy BAR w Main Hall 3D: Karta rozmów (Menu) i to, co z niej wybrano.
    public enum BarView { Menu, Newest, Popular, All, Topic, Search, Conversation, Login, Register }

    // Stan BAR odzwierciedlony w adresie /saloon — link można udostępnić, a "wstecz"/"dalej" wracają do tego samego widoku:
    //   ?bar=menu · ?bar=newest · ?bar=popular · ?bar=all[&sort=popular] · ?bar=topic&category=gry[&sort=popular]
    //   ?bar=search&q=docker[&category=technology]
    //   ?bar=post&id=12&from=search&category=technology&q=docker — rozmowa otwarta z listy; "from" + parametry listy
    //   (Origin) to dokładnie poziom, do którego prowadzi powrót (ten sam temat, sortowanie, fraza i filtr).
    //   ?bar=login&next=%2Fnew%3Fcategory%3Dtravel&back=%2Fsaloon%3Fbar%3Dtopic%26category%3Dtravel — karta logowania
    //   (albo bar=register) w BAR: "next" to miejsce po sukcesie (tylko adres lokalny; serwer sprawdza go ponownie),
    //   "back" (Origin) — poziom, do którego wraca Escape / "Wróć" (brak = sala).
    // Brak "bar" = sala bez otwartego baru. Parametry spoza BAR (np. "scene") nie są przenoszone.
    public sealed record BarState(BarView View, PostCategory? Category = null, PostSort Sort = PostSort.Newest, string Query = "",
        int PostId = 0, BarState? Origin = null, string? Next = null)
    {
        public static readonly BarState Menu = new(BarView.Menu);

        // Tematy w Karcie rozmów — Feedback zostaje zwykłą kategorią w systemie, ale nie jest tematem baru.
        public static readonly IReadOnlyList<PostCategory> Topics = CategoryMeta.All.Where(c => c != PostCategory.Feedback).ToList();

        public static BarState? Parse(string? bar, string? category, string? sort, string? query, string? postId = null, string? from = null,
            string? next = null, string? back = null)
        {
            if (string.Equals(bar, "login", StringComparison.OrdinalIgnoreCase) || string.Equals(bar, "register", StringComparison.OrdinalIgnoreCase))
            {
                var origin = ParseBack(back);
                return Auth(string.Equals(bar, "register", StringComparison.OrdinalIgnoreCase) ? BarView.Register : BarView.Login,
                    SafeReturn(next) ?? origin?.Url ?? SaloonRoutes.Hub, origin);
            }

            if (string.Equals(bar, "post", StringComparison.OrdinalIgnoreCase))
            {
                // Rozmowa bez poprawnego id nie ma sensu — Karta rozmów. Poziom listy z tych samych parametrów;
                // brak albo nieprawidłowe "from" — powrót do Karty rozmów.
                if (!int.TryParse(postId, out var id) || id <= 0)
                    return Menu;
                var origin = from is null || string.Equals(from, "post", StringComparison.OrdinalIgnoreCase) ? null : Parse(from, category, sort, query);
                return Conversation(id, origin is { IsList: true } ? origin : Menu);
            }

            var parsedCategory = CategoryMeta.TryParseSlug(category, out var c) ? c : (PostCategory?)null;
            var parsedSort = string.Equals(sort, "popular", StringComparison.OrdinalIgnoreCase) ? PostSort.Popular : PostSort.Newest;
            return bar?.ToLowerInvariant() switch
            {
                "menu" => Menu,
                "newest" => new BarState(BarView.Newest),
                "popular" => new BarState(BarView.Popular, Sort: PostSort.Popular),
                "all" => new BarState(BarView.All, Sort: parsedSort),
                // Temat bez poprawnej kategorii nie ma sensu — wraca do Karty rozmów.
                "topic" => parsedCategory is null ? Menu : new BarState(BarView.Topic, parsedCategory, parsedSort),
                "search" => new BarState(BarView.Search, parsedCategory, Query: (query ?? "").Trim()),
                _ => null
            };
        }

        public static BarState Conversation(int postId, BarState origin) => new(BarView.Conversation, PostId: postId, Origin: origin);

        public static BarState Auth(BarView view, string next, BarState? origin) => new(view, Origin: origin, Next: next);

        public bool IsAuth => View is BarView.Login or BarView.Register;

        // Adres powrotu po zalogowaniu: tylko ścieżka lokalna ("/…", bez "//" i "/\" — bez open redirect). Serwer
        // (AccountController) i tak przyjmuje wyłącznie adres lokalny, a po sukcesie nawigacja idzie pod adres, który wybrał.
        public static string? SafeReturn(string? url)
        {
            if (string.IsNullOrEmpty(url) || url[0] != '/' || url.Length > 2048)
                return null;
            if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
                return null;
            return url.Any(char.IsControl) || !Uri.IsWellFormedUriString(url, UriKind.Relative) ? null : url;
        }

        // Poziom BAR, do którego wraca karta logowania: tylko adres /saloon?bar=… (bez zagnieżdżonych kart logowania).
        private static BarState? ParseBack(string? back)
        {
            if (SafeReturn(back) is not { } url || !url.StartsWith(SaloonRoutes.Hub + "?", StringComparison.OrdinalIgnoreCase))
                return null;
            var q = QueryHelpers.ParseQuery(url[(url.IndexOf('?') + 1)..]);
            string? Get(string key) => q.TryGetValue(key, out var value) ? value.ToString() : null;
            var bar = Get("bar");
            if (string.Equals(bar, "login", StringComparison.OrdinalIgnoreCase) || string.Equals(bar, "register", StringComparison.OrdinalIgnoreCase))
                return Menu;
            return Parse(bar, Get("category"), Get("sort"), Get("q"), Get("id"), Get("from"));
        }

        // Poziomy, z których można otworzyć rozmowę (lista rozmów albo wyniki wyszukiwania).
        public bool IsList => View is BarView.Newest or BarView.Popular or BarView.All or BarView.Topic or BarView.Search;

        public string Url
        {
            get
            {
                if (IsAuth)
                {
                    var url = $"{SaloonRoutes.Hub}?bar={(View == BarView.Register ? "register" : "login")}&next={Uri.EscapeDataString(Next ?? SaloonRoutes.Hub)}";
                    return Origin is null ? url : $"{url}&back={Uri.EscapeDataString(Origin.Url)}";
                }

                if (View == BarView.Conversation)
                {
                    var origin = Origin ?? Menu;
                    var list = origin.Url[(origin.Url.IndexOf("bar=", StringComparison.Ordinal) + 4)..];
                    return $"{SaloonRoutes.Hub}?bar=post&id={PostId}&from={list}";
                }

                var parts = new List<string> { "bar=" + View.ToString().ToLowerInvariant() };
                if (View is BarView.Topic or BarView.Search && Category is PostCategory category)
                    parts.Add("category=" + CategoryMeta.Slug(category));
                if (View is BarView.All or BarView.Topic && Sort == PostSort.Popular)
                    parts.Add("sort=popular");
                if (View == BarView.Search && Query.Length > 0)
                    parts.Add("q=" + Uri.EscapeDataString(Query));
                return $"{SaloonRoutes.Hub}?{string.Join("&", parts)}";
            }
        }

        // Poziom wyżej, gdy nie ma historii w tej sesji (link wklejony z zewnątrz): wyszukiwanie w temacie → temat,
        // lista → Karta rozmów, Karta rozmów → sala (null).
        public BarState? Parent => View switch
        {
            BarView.Menu => null,
            BarView.Conversation => Origin ?? Menu,
            BarView.Login or BarView.Register => Origin,
            BarView.Search when Category is PostCategory category => new BarState(BarView.Topic, category),
            _ => Menu
        };

        public string Title => View switch
        {
            BarView.Menu => "Karta rozmów",
            BarView.Newest => "Najnowsze rozmowy",
            BarView.Popular => "Popularne rozmowy",
            BarView.All => "Wszystkie rozmowy",
            BarView.Topic => Category!.Value.DisplayName(),
            BarView.Search => Query.Length == 0 ? "Szukaj rozmowy" : $"Wyniki: „{Query}”",
            BarView.Conversation => "Rozmowa",
            BarView.Login => "Powrót do Saloonu",
            BarView.Register => "Karta nowego przybysza",
            _ => ""
        };
    }
}
