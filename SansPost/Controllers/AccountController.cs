using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Logowanie Blazor UI przez zwykły HTTP POST (nie z circuitu SignalR) — ustawia/usuwa cookie.
    // Używa tego samego IAuthService co REST API.
    [Route("auth")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [AutoValidateAntiforgeryToken]
    public class AccountController : Controller
    {
        private const string DefaultReturnUrl = "/posts";

        private readonly IAuthService _authService;

        public AccountController(IAuthService authService)
        {
            _authService = authService;
        }

        public sealed class LoginForm
        {
            public string? Email { get; set; }
            public string? Password { get; set; }
            public string? ReturnUrl { get; set; }
        }

        public sealed class RegisterForm
        {
            public string? Username { get; set; }
            public string? Email { get; set; }
            public string? Password { get; set; }
            public string? ConfirmPassword { get; set; }
        }

        [EnableRateLimiting(AuthRateLimitOptions.PolicyName)]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromForm] LoginForm form, CancellationToken cancellationToken)
        {
            var returnUrl = Url.IsLocalUrl(form.ReturnUrl) ? form.ReturnUrl! : DefaultReturnUrl;

            var user = await _authService.AuthenticateAsync(form.Email, form.Password, cancellationToken);
            if (user is null)
                return LocalRedirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}");

            var principal = new ClaimsPrincipal(UserClaimsFactory.CreateIdentity(user, AuthSchemes.Cookie));
            await HttpContext.SignInAsync(AuthSchemes.Cookie, principal);

            return LocalRedirect(returnUrl);
        }

        [EnableRateLimiting(AuthRateLimitOptions.PolicyName)]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromForm] RegisterForm form, CancellationToken cancellationToken)
        {
            if (form.Password != form.ConfirmPassword)
                return LocalRedirect("/register?error=mismatch");

            var result = await _authService.RegisterAsync(new RegisterRequest
            {
                Username = form.Username ?? string.Empty,
                Email = form.Email ?? string.Empty,
                Password = form.Password ?? string.Empty
            }, cancellationToken);

            if (result.Succeeded)
                return LocalRedirect("/login?registered=1");

            return LocalRedirect(result.Error == ServiceError.Conflict ? "/register?error=conflict" : "/register?error=invalid");
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(AuthSchemes.Cookie);
            return LocalRedirect("/");
        }
    }
}
