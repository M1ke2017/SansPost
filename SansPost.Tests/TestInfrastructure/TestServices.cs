using Microsoft.Extensions.Options;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;
using SansPost.Infrastructure.Security;

namespace SansPost.Tests.TestInfrastructure
{
    // Serwisy tworzone bezpośrednio w testach (bez hosta): prawdziwa brama zapisu (status konta sprawdzany w bazie)
    // z limitem na tyle wysokim, że nie wpływa na testy niezwiązane z rate limitingiem.
    public static class TestServices
    {
        private static readonly UserWriteRateLimiter GenerousLimiter = new(new RateLimitWindowOptions
        {
            PermitLimit = 1_000_000,
            Window = TimeSpan.FromMinutes(1)
        });

        public static IWriteGuard Guard(ApplicationDbContext context) => new WriteGuard(context, GenerousLimiter);

        public static AuthService Auth(ApplicationDbContext context, PublicDemoOptions? demo = null) =>
            new(context, Options.Create(demo ?? new PublicDemoOptions { MaxPublicAccounts = int.MaxValue }));
    }
}
