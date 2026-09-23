using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Reactions;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    [Route("api/posts/{postId:int}/likes")]
    public class ReactionsController : ApiControllerBase
    {
        private readonly ILikeService _likeService;

        public ReactionsController(ILikeService likeService)
        {
            _likeService = likeService;
        }

        // Anonimowo; jeśli podano JWT, LikedByCurrentUser uwzględnia użytkownika.
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> GetSummary(int postId, CancellationToken cancellationToken)
        {
            var summary = await _likeService.GetSummaryAsync(postId, User.GetUserId(), cancellationToken);
            if (summary is null)
                return NotFound();

            return Ok(summary);
        }

        [HttpPost("toggle")]
        public async Task<IActionResult> Toggle(int postId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _likeService.ToggleAsync(userId, postId, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }
    }
}
