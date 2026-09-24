using System.Security.Claims;

namespace SansPost.Features.Identity
{
    public static class SansPostClaimTypes
    {
        // Wersja stanu uwierzytelnienia z chwili logowania (porównywana z User.AuthVersion przy każdym żądaniu).
        public const string AuthVersion = "sp_auth_version";
    }

    // Uwierzytelniony użytkownik — bez PasswordHash, wspólny wynik logowania dla Cookie i JWT.
    public sealed record AuthenticatedUser(int Id, string Username, string Email, UserRole Role, int AuthVersion)
    {
        public static AuthenticatedUser From(User user) => new(user.Id, user.Username, user.Email, user.Role, user.AuthVersion);
    }

    // Jedyne miejsce tworzenia claims — identyczne dla cookie (Blazor) i access tokena (REST).
    public static class UserClaimsFactory
    {
        public static ClaimsIdentity CreateIdentity(AuthenticatedUser user, string authenticationType)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(SansPostClaimTypes.AuthVersion, user.AuthVersion.ToString())
            };

            return new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role);
        }
    }
}
