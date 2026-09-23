using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Comments;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    [Route("api")]
    public class CommentsController : ApiControllerBase
    {
        private readonly ICommentService _commentService;

        public CommentsController(ICommentService commentService)
        {
            _commentService = commentService;
        }

        [AllowAnonymous]
        [HttpGet("posts/{postId:int}/comments")]
        public async Task<IActionResult> GetByPost(int postId, CancellationToken cancellationToken)
        {
            var comments = await _commentService.GetByPostAsync(postId, cancellationToken);
            if (comments is null)
                return NotFound();

            return Ok(comments);
        }

        [HttpPost("posts/{postId:int}/comments")]
        public async Task<IActionResult> Add(int postId, CommentRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _commentService.AddAsync(userId, postId, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            return StatusCode(StatusCodes.Status201Created, result.Value);
        }

        [HttpDelete("comments/{commentId:int}")]
        public async Task<IActionResult> Delete(int commentId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _commentService.DeleteAsync(userId, commentId, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }
    }
}
