namespace SansPost.Infrastructure.Security
{
    // Sekcja "RateLimiting:Auth" — limit prób na adres IP dla login/register/refresh.
    public sealed class AuthRateLimitOptions
    {
        public const string SectionName = "RateLimiting:Auth";
        public const string PolicyName = "auth";

        public int PermitLimit { get; set; } = 10;
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    }
}
