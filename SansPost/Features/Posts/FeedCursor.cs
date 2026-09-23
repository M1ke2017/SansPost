using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SansPost.Features.Posts
{
    // Nieprzezroczysty kursor keyset pagination: (sort, CreatedAt, Id) ostatniego elementu strony.
    // Nie jest podpisany — manipulacja daje tylko inną stronę publicznych danych, nigdy dostęp do cudzych.
    public sealed record FeedCursor(PostSort Sort, DateTime CreatedAt, int Id)
    {
        public string Encode()
        {
            var raw = string.Create(CultureInfo.InvariantCulture, $"{(int)Sort}|{CreatedAt.Ticks}|{Id}");
            return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryDecode(string value, PostSort expectedSort, out FeedCursor? cursor)
        {
            cursor = null;
            try
            {
                var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)).Split('|');
                if (parts.Length != 3
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sort)
                    || sort != (int)expectedSort
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    return false;
                }

                cursor = new FeedCursor(expectedSort, new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
