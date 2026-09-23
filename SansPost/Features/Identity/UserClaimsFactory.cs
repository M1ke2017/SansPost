using System.Security.Claims;

namespace SansPost.Features.Identity
{
    // Uwierzytelniony użytkownik — bez PasswordHash, wspólny wynik logowania dla Cookie i JWT.
    public sealed record AuthenticatedUser(int Id, string Username, string Email, UserRole Role)
    {
        public static AuthenticatedUser From(User user) => new(user.Id, user.Username, user.Email, user.Role);
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
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            return new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role);
        }
    }
}
