using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Reactions;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Reakcje jako jawny stan docelowy (bez toggle):
    //   PUT    /api/posts/{id}/like → post JEST polubiony (idempotentne)
    //   DELETE /api/posts/{id}/like → post NIE JEST polubiony (idempotentne)
    // Obie zwracają 200 z aktualnym stanem — ta sama odpowiedź przy ponowieniu.
    [Route("api/posts/{postId:int}")]
    public class ReactionsController : ApiControllerBase
    {
        private readonly ILikeService _likeService;

        public ReactionsController(ILikeService likeService)
        {
            _likeService = likeService;
        }

        // Anonimowo; jeśli podano JWT, LikedByCurrentUser uwzględnia użytkownika.
        [AllowAnonymous]
        [HttpGet("likes")]
        public async Task<IActionResult> GetSummary(int postId, CancellationToken cancellationToken)
        {
            var summary = await _likeService.GetSummaryAsync(postId, User.GetUserId(), cancellationToken);
            return summary is null ? NotFound() : Ok(summary);
        }

        [HttpPut("like")]
        public async Task<IActionResult> Like(int postId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _likeService.LikeAsync(userId, postId, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [HttpDelete("like")]
        public async Task<IActionResult> Unlike(int postId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _likeService.UnlikeAsync(userId, postId, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }
    }
}
