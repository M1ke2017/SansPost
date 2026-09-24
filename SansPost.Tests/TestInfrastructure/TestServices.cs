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

        // Komentarze z prawdziwym serwisem powiadomień na tym samym DbContext (jak w DI: jeden scope).
        public static SansPost.Features.Comments.CommentService Comments(ApplicationDbContext context, TimeProvider? time = null) =>
            new(context, time ?? TimeProvider.System, Guard(context), new SansPost.Features.Notifications.NotificationService(context, time ?? TimeProvider.System));

        public static AuthService Auth(ApplicationDbContext context, PublicDemoOptions? demo = null) =>
            new(context, Options.Create(demo ?? new PublicDemoOptions { MaxPublicAccounts = int.MaxValue }), new AliasGenerator(context));
    }
}
