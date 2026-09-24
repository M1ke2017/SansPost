using System.Globalization;

namespace SansPost.Shared.Ui
{
    // Polskie formy liczebników i czasu względnego dla UI.
    public static class UiText
    {
        private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

        // 1 komentarz, 2–4 komentarze, 5+ komentarzy (12–14 → komentarzy).
        public static string Plural(int count, string one, string few, string many)
        {
            var form = count == 1 ? one
                : count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14) ? few
                : many;
            return $"{count.ToString("N0", Polish)} {form}";
        }

        public static string Posts(int count) => Plural(count, "post", "posty", "postów");
        public static string Comments(int count) => Plural(count, "komentarz", "komentarze", "komentarzy");
        public static string Likes(int count) => Plural(count, "polubienie", "polubienia", "polubień");

        public static string Number(int value) => value.ToString("N0", Polish);

        // Czas względny liczony względem UTC — niezależny od strefy serwera.
        // Starsze daty: sama data (bez godziny), więc strefa serwera nie wprowadza w błąd.
        public static string Relative(DateTime utc, DateTime? now = null)
        {
            var reference = now ?? DateTime.UtcNow;
            var diff = reference - DateTime.SpecifyKind(utc, DateTimeKind.Utc);

            if (diff < TimeSpan.FromMinutes(1)) return "przed chwilą";
            if (diff < TimeSpan.FromHours(1)) return Plural((int)diff.TotalMinutes, "minutę", "minuty", "minut") + " temu";
            if (diff < TimeSpan.FromDays(1)) return Plural((int)diff.TotalHours, "godzinę", "godziny", "godzin") + " temu";
            if (diff < TimeSpan.FromDays(2)) return "wczoraj";
            if (diff < TimeSpan.FromDays(7)) return Plural((int)diff.TotalDays, "dzień", "dni", "dni") + " temu";
            return Date(utc, reference);
        }

        public static string Date(DateTime utc, DateTime? now = null) =>
            utc.Year == (now ?? DateTime.UtcNow).Year
                ? utc.ToString("d MMM", Polish)
                : utc.ToString("d MMM yyyy", Polish);

        public static string FullDate(DateTime utc) => utc.ToString("d MMMM yyyy, HH:mm 'UTC'", Polish);

        // "od sierpnia 2026" — dopełniacz nazwy miesiąca.
        public static string MonthYear(DateTime utc) =>
            $"{Polish.DateTimeFormat.MonthGenitiveNames[utc.Month - 1]} {utc.Year}";

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
