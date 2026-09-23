using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Security;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests
{
    public class JwtTokenServiceTests
    {
        private static JwtTokenService CreateService(MutableTimeProvider time) =>
            new(Options.Create(TestDatabase.JwtOptions), time);

        [Fact]
        public void AccessToken_UsesConfiguredLifetimeIssuerAndAudience()
        {
            var time = new MutableTimeProvider();
            var identity = UserClaimsFactory.CreateIdentity(
                new AuthenticatedUser(42, "anna", "anna@example.com", UserRole.Admin), AuthSchemes.Jwt);

            var (token, expiresAt) = CreateService(time).CreateAccessToken(identity);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

            Assert.Equal(TestDatabase.JwtOptions.AccessTokenLifetime, expiresAt - time.GetUtcNow().UtcDateTime);
            Assert.Equal(TestDatabase.JwtOptions.Issuer, jwt.Issuer);
            Assert.Contains(TestDatabase.JwtOptions.Audience, jwt.Audiences);
            Assert.Equal("HS256", jwt.Header.Alg);
        }

        [Fact]
        public void ClaimsFactory_ProducesExactlyTheUnifiedClaimSet()
        {
            var identity = UserClaimsFactory.CreateIdentity(
                new AuthenticatedUser(7, "bob", "bob@example.com", UserRole.User), AuthSchemes.Cookie);

            Assert.Equal(
                new[] { ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Email, ClaimTypes.Role },
                identity.Claims.Select(c => c.Type));
            Assert.Equal(7, new ClaimsPrincipal(identity).GetUserId());
            Assert.True(new ClaimsPrincipal(identity).IsInRole(nameof(UserRole.User)));
        }
    }
}
