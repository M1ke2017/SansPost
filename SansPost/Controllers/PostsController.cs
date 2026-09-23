using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Optimistic concurrency przez standardowe HTTP: GET → ETag, PUT/DELETE → wymagany If-Match.
    [Route("api/posts")]
    public class PostsController : ApiControllerBase
    {
        private readonly IPostService _postService;

        public PostsController(IPostService postService)
        {
            _postService = postService;
        }

        [AllowAnonymous]
        [HttpGet]
        public Task<IActionResult> GetFeed([FromQuery] PostFeedQuery query, CancellationToken cancellationToken)
        {
            return FeedAsync(query, cancellationToken);
        }

        [AllowAnonymous]
        [HttpGet("{postId:int}")]
        public async Task<IActionResult> GetById(int postId, CancellationToken cancellationToken)
        {
            var post = await _postService.GetByIdAsync(postId, User.GetUserId(), cancellationToken);
            if (post is null)
                return NotFound();

            SetETag(post.Version);
            return Ok(post);
        }

        [AllowAnonymous]
        [HttpGet("/api/users/{userId:int}/posts")]
        public Task<IActionResult> GetByAuthor(int userId, [FromQuery] PostFeedQuery query, CancellationToken cancellationToken)
        {
            query.AuthorId = userId;
            return FeedAsync(query, cancellationToken);
        }

        [HttpGet("mine")]
        public Task<IActionResult> GetMine([FromQuery] PostFeedQuery query, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Task.FromResult<IActionResult>(Unauthorized());

            query.AuthorId = userId;
            return FeedAsync(query, cancellationToken);
        }

        [HttpGet("quota")]
        public async Task<IActionResult> GetQuota(CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var quota = await _postService.GetQuotaAsync(userId, cancellationToken);
            if (quota is null)
                return NotFound();

            return Ok(quota);
        }

        [HttpPost]
        public async Task<IActionResult> Create(PostRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            var result = await _postService.CreateAsync(userId, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            SetETag(result.Value!.Version);
            return CreatedAtAction(nameof(GetById), new { postId = result.Value.Id }, result.Value);
        }

        [HttpPut("{postId:int}")]
        public async Task<IActionResult> Update(int postId, PostRequest request, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            // Edycja bez wskazania wersji mogłaby po cichu nadpisać cudze zmiany (lost update).
            if (RequireIfMatch(out var expectedVersion) is { } ifMatchError)
                return ifMatchError;

            var result = await _postService.UpdateAsync(userId, postId, expectedVersion, request, cancellationToken);
            if (!result.Succeeded)
                return this.ToProblem(result);

            SetETag(result.Value!.Version);
            return Ok(result.Value);
        }

        [HttpDelete("{postId:int}")]
        public async Task<IActionResult> Delete(int postId, CancellationToken cancellationToken)
        {
            if (User.GetUserId() is not int userId)
                return Unauthorized();

            // Usuwa się tylko wersję, którą klient widział — tak samo jak przy edycji.
            if (RequireIfMatch(out var expectedVersion) is { } ifMatchError)
                return ifMatchError;

            var result = await _postService.DeleteAsync(userId, postId, expectedVersion, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }

        private async Task<IActionResult> FeedAsync(PostFeedQuery query, CancellationToken cancellationToken)
        {
            var result = await _postService.GetFeedAsync(query, User.GetUserId(), cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }
    }
}
