namespace SansPost.Infrastructure.Security
{
    // Sekcja "Jwt". Key nie może znajdować się w repozytorium:
    // Development -> User Secrets (dotnet user-secrets set "Jwt:Key" "..."),
    // deployment  -> zmienna środowiskowa Jwt__Key.
    public sealed class JwtOptions
    {
        public const string SectionName = "Jwt";

        public string Key { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;

        // Krótkotrwały access token dla REST API.
        public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

        public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
    }
}
