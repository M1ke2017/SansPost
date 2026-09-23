using System.Security.Claims;

namespace SansPost.Infrastructure.Security
{
    public static class ClaimsPrincipalExtensions
    {
        // Id zalogowanego użytkownika z claimu NameIdentifier; null gdy brak lub niepoprawny.
        public static int? GetUserId(this ClaimsPrincipal principal)
        {
            return int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) && userId > 0
                ? userId
                : null;
        }
    }
}
