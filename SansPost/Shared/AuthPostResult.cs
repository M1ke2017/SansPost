using Microsoft.AspNetCore.WebUtilities;
using SansPost.Features.Saloon;

namespace SansPost.Shared
{
    // Wynik natywnego POST /auth/login lub /auth/register wysłanego z karty na Entrance (sansPost.submitAuthForm /
    // registerAndSignIn): etap, status HTTP i adres końcowy po przekierowaniach AccountController.
    public sealed record AuthPostResult(string Stage, int Status, string Path, string Query)
    {
        public bool SignedIn => Stage == "login" && Status is >= 200 and < 300 && Path == SaloonRoutes.Hub;

        // Kod z "?error=…"; 429 = limiter (bez przekierowania), 0 = brak połączenia.
        public string? Error =>
            Status == 0 ? "network"
            : Status == 429 ? "rate-limited"
            : QueryHelpers.ParseQuery(Query).TryGetValue("error", out var error) ? error.ToString()
            : SignedIn ? null
            : "invalid";
    }
}
