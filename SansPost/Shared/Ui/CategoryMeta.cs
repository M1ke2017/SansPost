using SansPost.Features.Posts;

namespace SansPost.Shared.Ui
{
    // Wizualny język kategorii: ikona + subtelny akcent (klasa CSS) + krótki opis. Jeden system, bez krzykliwych kolorów.
    public static class CategoryMeta
    {
        public static readonly IReadOnlyList<PostCategory> All = Enum.GetValues<PostCategory>();

        // Symbole miejsc w miasteczku: szyld saloonu, telegraf, karty, drogowskaz, latarnia, kuźnia, tablica ogłoszeń.
        public static string Icon(PostCategory category) => category switch
        {
            PostCategory.Technology => Icons.Telegraph,
            PostCategory.Games => Icons.Cards,
            PostCategory.Travel => Icons.Signpost,
            PostCategory.Ideas => Icons.Lantern,
            PostCategory.Projects => Icons.Anvil,
            PostCategory.Feedback => Icons.NoticeBoard,
            _ => Icons.SaloonSign
        };

        public static string CssClass(PostCategory category) => "cat-" + Slug(category);

        // Adres kategorii: /c/{slug} (angielska nazwa enum, małe litery — stabilna niezależnie od tłumaczenia).
        public static string Slug(PostCategory category) => category.ToString().ToLowerInvariant();

        public static string Url(PostCategory category) => $"/c/{Slug(category)}";

        public static bool TryParseSlug(string? slug, out PostCategory category) =>
            Enum.TryParse(slug, ignoreCase: true, out category) && Enum.IsDefined(category);

        public static string Description(PostCategory category) => category switch
        {
            PostCategory.General => "Rozmowy, które nie pasują nigdzie indziej.",
            PostCategory.Technology => "Programowanie, sprzęt, narzędzia i nowinki.",
            PostCategory.Games => "Gry, rekomendacje i wspólne sesje.",
            PostCategory.Travel => "Miejsca, trasy i wskazówki z podróży.",
            PostCategory.Ideas => "Pomysły do przedyskutowania — także te niegotowe.",
            PostCategory.Projects => "Pokaż, nad czym pracujesz, i zbierz opinie.",
            PostCategory.Feedback => "Uwagi o SansPost — co działa, a co poprawić.",
            _ => string.Empty
        };

        // Krótka nazwa miejsca na szyldzie kategorii.
        public static string Place(PostCategory category) => category switch
        {
            PostCategory.General => "Główna sala",
            PostCategory.Technology => "Telegraf",
            PostCategory.Games => "Stolik do gry",
            PostCategory.Travel => "Rozstaje dróg",
            PostCategory.Ideas => "Pod latarnią",
            PostCategory.Projects => "Warsztat",
            PostCategory.Feedback => "Tablica ogłoszeń",
            _ => string.Empty
        };

        // Kontekstowy pusty stan kategorii.
        public static string EmptyText(PostCategory category) => category switch
        {
            PostCategory.Feedback => "Tablica ogłoszeń jest jeszcze pusta. Twoja opinia może zawisnąć tu pierwsza.",
            PostCategory.Projects => "W warsztacie jeszcze cicho — nikt nie pokazał tu projektu.",
            PostCategory.Ideas => "Pod latarnią nikt jeszcze nie zostawił pomysłu.",
            _ => "Na tym szlaku nikt jeszcze nie zostawił wiadomości."
        };
    }
}
