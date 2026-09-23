using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Comments;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Komentarze: GET → ETag, PUT/DELETE → wymagany If-Match (428 bez nagłówka, 412 dla nieaktualnej wersji).
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
        public async Task<IActionResult> GetByPost(int postId, [FromQuery] CommentPageQuery query, CancellationToken cancellationToken)
        {
            var result = await _commentService.GetByPostAsync(postId, query, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [AllowAnonymous]
        [HttpGet("comments/{commentId:int}")]
        public async Task<IActionResult> GetById(int commentId, CancellationToken cancellationToken)
        {
            var comment = await _commentService.GetByIdAsync(commentId, cancellationToken);
            if (comment is null)
                return NotFound();

            SetETag(comment.Version);
            return Ok(comment);
        }

        [HttpPost("posts/{postId:int}/comments")]
        public async Task<IActionResult> Add(int postId, CommentRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _commentService.AddAsync(userId, postId, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            SetETag(result.Value!.Version);
            return CreatedAtAction(nameof(GetById), new { commentId = result.Value.Id }, result.Value);
        }

        [HttpPut("comments/{commentId:int}")]
        public async Task<IActionResult> Update(int commentId, CommentRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();
            if (RequireIfMatch(out var expectedVersion) is { } ifMatchError)
                return ifMatchError;

            var result = await _commentService.UpdateAsync(userId, commentId, expectedVersion, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            SetETag(result.Value!.Version);
            return Ok(result.Value);
        }

        [HttpDelete("comments/{commentId:int}")]
        public async Task<IActionResult> Delete(int commentId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();
            if (RequireIfMatch(out var expectedVersion) is { } ifMatchError)
                return ifMatchError;

            var result = await _commentService.DeleteAsync(userId, commentId, expectedVersion, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }
    }
}
