namespace SansPost.Features.Identity
{
    // Wspólna zasada porównywania tożsamości: trim + wielkie litery (invariant).
    // Migracja AddAuthenticationFoundation wypełnia istniejące rekordy odpowiednikiem UPPER(TRIM(...)).
    public static class IdentityNormalizer
    {
        public static string Normalize(string value) => value.Trim().ToUpperInvariant();
    }
}
