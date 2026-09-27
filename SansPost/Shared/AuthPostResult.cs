using Microsoft.AspNetCore.WebUtilities;
using SansPost.Features.Saloon;

namespace SansPost.Shared
{
    // Wynik natywnego POST /auth/login lub /auth/register wysłanego z karty na Entrance (sansPost.submitAuthForm /
    // registerAndSignIn): etap, status HTTP i adres końcowy po przekierowaniach AccountController.
    public sealed record AuthPostResult(string Stage, int Status, string Path, string Query)
    {
        // Zalogowany: etap logowania, 2xx i koniec poza /login (porażka wraca na /login?error=…). Adres końcowy to cel
        // wybrany przez serwer (ReturnUrl tylko lokalny, inaczej /saloon) — Entrance: /saloon, BAR: miejsce w Saloonie.
        public bool SignedIn => Stage == "login" && Status is >= 200 and < 300 && Path.StartsWith('/')
            && !string.Equals(Path, "/login", StringComparison.OrdinalIgnoreCase);

        // Zweryfikowany przez serwer adres powrotu (ścieżka + query) — tam wraca przeładowana strona po zalogowaniu.
        public string ReturnTo => SignedIn ? Path + Query : SaloonRoutes.Hub;

        // Kod z "?error=…"; 429 = limiter (bez przekierowania), 0 = brak połączenia.
        public string? Error =>
            Status == 0 ? "network"
            : Status == 429 ? "rate-limited"
            : QueryHelpers.ParseQuery(Query).TryGetValue("error", out var error) ? error.ToString()
            : SignedIn ? null
            : "invalid";
    }
}
