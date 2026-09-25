using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using SansPost.Features.Identity;

namespace SansPost.Infrastructure.Security
{
    // Otwarty circuit Blazor nie wysyła nowych żądań HTTP, więc OnValidatePrincipal cookie go nie obejmuje.
    // Co minutę sprawdzamy AuthVersion/ban — nieaktualna sesja staje się anonimowa także w otwartej karcie.
    // Zapisy i tak blokuje WriteGuard natychmiast (sprawdzenie w bazie przy każdej operacji).
    public sealed class RevalidatingAuthStateProvider : RevalidatingServerAuthenticationStateProvider
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public RevalidatingAuthStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
            : base(loggerFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

        protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
        {
            if (authenticationState.User.Identity?.IsAuthenticated != true)
                return true;

            using var scope = _scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AuthStateValidator>()
                .IsCurrentAsync(authenticationState.User, cancellationToken);
        }
    }
}
