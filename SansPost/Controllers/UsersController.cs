using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    [Route("api/users")]
    public class UsersController : ApiControllerBase
    {
        private readonly IUserService _userService;

        public UsersController(IUserService userService)
        {
            _userService = userService;
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

        [Authorize(Policy = AuthPolicies.ApiAdmin)]
        [HttpPut("{userId:int}/role")]
        public async Task<IActionResult> ChangeRole(int userId, ChangeRoleRequest request, CancellationToken cancellationToken)
        {
            var result = await _userService.ChangeRoleAsync(userId, request.Role!.Value, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }

        [Authorize(Policy = AuthPolicies.ApiAdmin)]
        [HttpPut("{userId:int}/subscription")]
        public async Task<IActionResult> SetSubscription(int userId, SetSubscriptionRequest request, CancellationToken cancellationToken)
        {
            var result = await _userService.SetSubscriptionAsync(userId, request.Type!.Value, request.ExpiresAt, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }
    }
}
