using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SansPost.Features
{
    // Nieprzezroczysty kursor keyset pagination: (zakres, CreatedAt, Id) ostatniego elementu strony.
    // Scope wiąże kursor z konkretną listą i kierunkiem (np. "posts.Newest", "comments") — kursor z innej listy jest odrzucany.
    // Nie jest podpisany — manipulacja daje tylko inną stronę publicznych danych.
    public sealed record KeysetCursor(string Scope, DateTime CreatedAt, int Id)
    {
        public string Encode()
        {
            var raw = string.Create(CultureInfo.InvariantCulture, $"{Scope}|{CreatedAt.Ticks}|{Id}");
            return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
        }

        public static bool TryDecode(string value, string expectedScope, out KeysetCursor? cursor)
        {
            cursor = null;
            try
            {
                var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value)).Split('|');
                if (parts.Length != 3
                    || parts[0] != expectedScope
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    || ticks > DateTime.MaxValue.Ticks
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    return false;
                }

                cursor = new KeysetCursor(expectedScope, new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    public sealed record KeysetPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
}
