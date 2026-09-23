using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SansPost.Features
{
    // Nieprzezroczysty kursor keyset pagination: pełny klucz sortowania ostatniego elementu strony.
    //   Scope   — wiąże kursor z listą i kierunkiem (np. "posts.Newest", "comments", "search.posts.<hash>").
    //   Rank    — całkowity wynik rankingu (search/popular); liczba całkowita, bo float nie nadaje się na separator kursora.
    //   AsOf    — moment odniesienia rankingu zależnego od czasu (popular), stały dla wszystkich stron.
    // Nie jest podpisany — manipulacja daje tylko inną stronę publicznych danych.
    public sealed record KeysetCursor(string Scope, DateTime CreatedAt, int Id, long? Rank = null, DateTime? AsOf = null)
    {
        public string Encode()
        {
            var raw = string.Create(CultureInfo.InvariantCulture,
                $"{Scope}|{CreatedAt.Ticks}|{Id}|{Rank}|{AsOf?.Ticks}");
            return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryDecode(string value, string expectedScope, out KeysetCursor? cursor)
        {
            cursor = null;
            try
            {
                var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)).Split('|');
                if (parts.Length != 5
                    || parts[0] != expectedScope
                    || !TryParseTicks(parts[1], out var createdAt)
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    return false;
                }

                long? rank = null;
                if (parts[3].Length > 0)
                {
                    if (!long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedRank))
                        return false;
                    rank = parsedRank;
                }

                DateTime? asOf = null;
                if (parts[4].Length > 0)
                {
                    if (!TryParseTicks(parts[4], out var parsedAsOf))
                        return false;
                    asOf = parsedAsOf;
                }

                cursor = new KeysetCursor(expectedScope, createdAt, id, rank, asOf);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool TryParseTicks(string value, out DateTime result)
        {
            result = default;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks > DateTime.MaxValue.Ticks)
                return false;

            result = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
    }

    public sealed record KeysetPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
}
