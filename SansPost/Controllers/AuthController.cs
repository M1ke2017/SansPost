using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Uwierzytelnianie klientów REST: JWT access token + rotowany refresh token.
    [Route("api/auth")]
    public class AuthController : ApiControllerBase
    {
        private const string InvalidCredentialsMessage = "Nieprawidłowe dane logowania.";
        private const string InvalidRefreshTokenMessage = "Nieprawidłowy lub wygasły refresh token.";

        private readonly IAuthService _authService;
        private readonly IApiTokenService _tokenService;
        private readonly IUserService _userService;
        private readonly IAliasGenerator _aliases;

        public AuthController(IAuthService authService, IApiTokenService tokenService, IUserService userService, IAliasGenerator aliases)
        {
            _authService = authService;
            _tokenService = tokenService;
            _userService = userService;
            _aliases = aliases;
        }

        // Propozycja przydomka dla formularza rejestracji ("Losuj inny"). Niczego nie tworzy ani nie rezerwuje.
        // Rejestracja bez Username dostaje przydomek przydzielony przez serwer.
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Search)]
        [HttpGet("alias-suggestion")]
        public async Task<IActionResult> SuggestAlias(CancellationToken cancellationToken)
        {
            var alias = await _aliases.SuggestAsync(cancellationToken);
            return alias is null
                ? Problem(detail: "Brak wolnych przydomków.", statusCode: StatusCodes.Status409Conflict)
                : Ok(new AliasSuggestionResponse(alias));
        }

        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
        {
            var result = await _authService.RegisterAsync(request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            return CreatedAtAction(nameof(UsersController.GetUser), "Users", new { userId = result.Value!.Id }, result.Value);
        }

        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
        {
            var user = await _authService.AuthenticateAsync(request.Email, request.Password, cancellationToken);
            if (user is null)
            {
                SecurityEvents.FailedLogin(HttpContext, "api");
                return Problem(detail: InvalidCredentialsMessage, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Ok(await _tokenService.IssueAsync(user, cancellationToken));
        }

        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
        {
            var tokens = await _tokenService.RefreshAsync(request.RefreshToken, cancellationToken);
            if (tokens is null)
                return Problem(detail: InvalidRefreshTokenMessage, statusCode: StatusCodes.Status401Unauthorized);

            return Ok(tokens);
        }

        // Unieważnia refresh token sesji. Działa także po wygaśnięciu access tokena; zawsze 204 (brak wyroczni).
        [AllowAnonymous]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        [HttpPost("logout")]
        public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken cancellationToken)
        {
            await _tokenService.RevokeAsync(request.RefreshToken, cancellationToken);
            return NoContent();
        }

        [HttpGet("me")]
        public async Task<IActionResult> Me(CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var user = await _userService.GetByIdAsync(userId, cancellationToken);
            return user is null ? Unauthorized() : Ok(user);
        }
    }
}
