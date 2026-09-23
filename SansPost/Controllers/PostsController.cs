using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Optimistic concurrency przez standardowe HTTP: GET → ETag, PUT/DELETE → If-Match.
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
            var post = await _postService.GetByIdAsync(postId, cancellationToken);
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
            if (!TryGetIfMatchVersion(out var expectedVersion, out var ifMatchError))
                return ifMatchError!;
            if (expectedVersion is null)
                return Problem(detail: "Wymagany nagłówek If-Match z ETag pobranym przez GET.", statusCode: StatusCodes.Status428PreconditionRequired);

            var result = await _postService.UpdateAsync(userId, postId, expectedVersion.Value, request, cancellationToken);
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

            // If-Match opcjonalny: jeśli podany, usunięcie wymaga zgodnej wersji.
            if (!TryGetIfMatchVersion(out var expectedVersion, out var ifMatchError))
                return ifMatchError!;

            var result = await _postService.DeleteAsync(userId, postId, expectedVersion, cancellationToken);
            return result.Succeeded ? NoContent() : this.ToProblem(result);
        }

        private async Task<IActionResult> FeedAsync(PostFeedQuery query, CancellationToken cancellationToken)
        {
            var result = await _postService.GetFeedAsync(query, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        private void SetETag(int version) => Response.Headers.ETag = PostETag.Format(version);

        // true + null = brak nagłówka; false = nagłówek nieprawidłowy (odpowiedź 400 w error).
        private bool TryGetIfMatchVersion(out int? version, out IActionResult? error)
        {
            version = null;
            error = null;

            var header = Request.Headers.IfMatch.ToString();
            if (string.IsNullOrEmpty(header))
                return true;

            if (PostETag.TryParse(header, out var parsed))
            {
                version = parsed;
                return true;
            }

            error = Problem(detail: "Nieprawidłowy nagłówek If-Match. Oczekiwany pojedynczy ETag, np. \"3\".", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
    }

    public static class PostETag
    {
        // Silny ETag = wersja posta, np. "3".
        public static string Format(int version) => new EntityTagHeaderValue($"\"{version}\"").ToString();

        public static bool TryParse(string header, out int version)
        {
            version = 0;
            if (!EntityTagHeaderValue.TryParse(header, out var tag) || tag.IsWeak || tag == EntityTagHeaderValue.Any)
                return false;

            return int.TryParse(tag.Tag.AsSpan().Trim('"'), out version) && version > 0;
        }
    }
}
