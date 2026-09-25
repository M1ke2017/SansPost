using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Features.Posts;
using SansPost.Features.Search;
using SansPost.Infrastructure.Security;

namespace SansPost.Controllers
{
    // Publiczne wyszukiwanie. Tożsamość odbiorcy (LikedByCurrentUser) wyłącznie z JWT — nigdy z query/body.
    [Route("api/search")]
    public class SearchController : ApiControllerBase
    {
        private readonly ISearchService _searchService;

        public SearchController(ISearchService searchService)
        {
            _searchService = searchService;
        }

        // Publiczne i kosztowne (ranking wszystkich trafień) — wspólny limit wyszukiwania (ten sam co w UI Blazor).
        [AllowAnonymous]
        [SearchRateLimit]
        [HttpGet("posts")]
        public async Task<IActionResult> Posts([FromQuery] PostSearchQuery query, CancellationToken cancellationToken)
        {
            var result = await _searchService.SearchPostsAsync(query, User.GetUserId(), cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }

        [AllowAnonymous]
        [SearchRateLimit]
        [HttpGet("users")]
        public async Task<IActionResult> Users([FromQuery] string? q, CancellationToken cancellationToken, [FromQuery] int limit = SearchLimits.DefaultUserResults)
        {
            var result = await _searchService.SearchUsersAsync(q, limit, cancellationToken);
            return result.Succeeded ? Ok(result.Value) : this.ToProblem(result);
        }
    }

    [Route("api/categories")]
    public class CategoriesController : ApiControllerBase
    {
        private readonly IPostService _postService;

        public CategoriesController(IPostService postService)
        {
            _postService = postService;
        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            return Ok(await _postService.GetCategoriesAsync(cancellationToken));
        }
    }
}
