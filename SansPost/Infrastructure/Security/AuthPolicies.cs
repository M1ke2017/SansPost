using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace SansPost.Infrastructure.Security
{
    public static class AuthSchemes
    {
        // Blazor Server UI.
        public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;

        // REST API.
        public const string Jwt = JwtBearerDefaults.AuthenticationScheme;
    }

    public static class AuthPolicies
    {
        // REST: wyłącznie JWT — cookie UI nigdy nie uwierzytelnia wywołań /api.
        public const string ApiUser = "ApiUser";
        public const string ApiAdmin = "ApiAdmin";

        // Stół gry na żywo (DuelHub): przeglądarka łączy się z cookie UI, klienci API — z JWT. Oba schematy walidują
        // AuthVersion (ban / zawieszenie kończy sesję), status konta dla akcji sprawdza serwis gry.
        public const string DuelPlayer = "DuelPlayer";
    }
}
