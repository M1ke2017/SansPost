using System.Globalization;
using SansPost.Localization;

namespace SansPost.Shared.Ui
{
    // Liczebniki, czas względny i daty w języku interfejsu (PL / EN — Sprint 22). Formy liczebników podawane po polsku
    // (1 / 2–4 / 5+); w angielskim z zasobów (1 / wiele).
    public static class UiText
    {
        public static string Plural(Loc l, int count, string one, string few, string many) => l.Plural(count, one, few, many);

        public static string Posts(Loc l, int count) => l.Plural(count, "post", "posty", "postów");
        public static string Comments(Loc l, int count) => l.Plural(count, "komentarz", "komentarze", "komentarzy");
        public static string Likes(Loc l, int count) => l.Plural(count, "polubienie", "polubienia", "polubień");
        public static string Reactions(Loc l, int count) => l.Plural(count, "reakcja", "reakcje", "reakcji");

        public static string Number(Loc l, int value) => l.Number(value);

        // Czas względny liczony względem UTC — niezależny od strefy serwera.
        // Starsze daty: sama data (bez godziny), więc strefa serwera nie wprowadza w błąd.
        public static string Relative(Loc l, DateTime utc, DateTime? now = null)
        {
            var reference = now ?? DateTime.UtcNow;
            var diff = reference - DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            if (diff < TimeSpan.FromMinutes(1)) return l["przed chwilą"];
            if (diff < TimeSpan.FromHours(1)) return Ago(l, l.Plural((int)diff.TotalMinutes, "minutę", "minuty", "minut"));
            if (diff < TimeSpan.FromDays(1)) return Ago(l, l.Plural((int)diff.TotalHours, "godzinę", "godziny", "godzin"));
            if (diff < TimeSpan.FromDays(2)) return l["wczoraj"];
            if (diff < TimeSpan.FromDays(7)) return Ago(l, l.Plural((int)diff.TotalDays, "dzień", "dni", "dni"));
            return Date(l, utc, reference);
        }

        private static string Ago(Loc l, string amount) => l.Format("{0} temu", amount);

        public static string Date(Loc l, DateTime utc, DateTime? now = null) =>
            utc.Year == (now ?? DateTime.UtcNow).Year
                ? utc.ToString("d MMM", l.Culture)
                : utc.ToString("d MMM yyyy", l.Culture);

        public static string FullDate(Loc l, DateTime utc) => utc.ToString("d MMMM yyyy, HH:mm 'UTC'", l.Culture);

        // "od sierpnia 2026" (dopełniacz) / "August 2026".
        public static string MonthYear(Loc l, DateTime utc) => l.IsEnglish
            ? utc.ToString("MMMM yyyy", l.Culture)
            : $"{l.Culture.DateTimeFormat.MonthGenitiveNames[utc.Month - 1]} {utc.Year}";

        public static string Initials(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return "?";

            var letters = username.Where(char.IsLetterOrDigit).Take(2).ToArray();
            return letters.Length == 0 ? username[..1] : new string(letters);
        }

        public static string ProfileUrl(string username) => $"/u/{Uri.EscapeDataString(username)}";
        public static string PostUrl(int postId) => $"/post-view/{postId}";
        public static string LoginUrl(string returnUrl) => $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }
}
