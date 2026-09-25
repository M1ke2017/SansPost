using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    [Route("api/users")]
    public class UsersController : ApiControllerBase
    {
        private readonly IUserService _userService;
        private readonly ModerationService _moderation;

        public UsersController(IUserService userService, ModerationService moderation)
        {
            _userService = userService;
            _moderation = moderation;
        }

        [AllowAnonymous]
        [HttpGet("{userId:int}")]
        public async Task<IActionResult> GetUser(int userId, CancellationToken cancellationToken)
        {
            var user = await _userService.GetByIdAsync(userId, cancellationToken);
            if (user is null)
                return NotFound();

            return Ok(user);
        }

        // Akcje administracyjne z audytem i unieważnieniem sesji (AuthVersion) — wykonuje moduł Moderation.
        [Authorize(Policy = AuthPolicies.ApiAdmin)]
        [HttpPut("{userId:int}/role")]
        public async Task<IActionResult> ChangeRole(int userId, ChangeRoleRequest request, CancellationToken cancellationToken)
        {
            var result = await _moderation.ChangeRoleAsync(User.GetUserId()!.Value, userId, request.Role!.Value, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }

        [Authorize(Policy = AuthPolicies.ApiAdmin)]
        [HttpPut("{userId:int}/subscription")]
        public async Task<IActionResult> SetSubscription(int userId, SetSubscriptionRequest request, CancellationToken cancellationToken)
        {
            var result = await _moderation.SetSubscriptionAsync(User.GetUserId()!.Value, userId, request.Type!.Value, request.ExpiresAt, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }
    }
}
