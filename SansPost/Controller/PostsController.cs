using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SansPost.Models;
using SansPost.Services;
using System.Security.Claims;

[ApiController]
[Route("posts")]
public class PostsController : ControllerBase
{
    private readonly PostService _postService;

    public PostsController(PostService postService)
    {
        _postService = postService;
    }

    [HttpPost("add")]
    public async Task<IActionResult> AddPost([FromBody] PostRequest request)
    {
        var result = await _postService.AddPost(request.UserId, request.Title, request.Content, request.Category.ToString(), request.ImageUrl);
        if (!result)
            return BadRequest("Nie udało się dodać posta.");

        return Ok(new { message = "Post dodany pomyślnie!" });
    }

    [HttpGet("{postId}")]
    public async Task<IActionResult> GetPost(int postId)
    {
        var post = await _postService.GetPostById(postId);
        if (post == null)
            return NotFound("Post nie istnieje.");

        return Ok(post);
    }

    [HttpGet("user/{userId}")]
    public async Task<IActionResult> GetUserPosts(int userId)
    {
        var posts = await _postService.GetUserPosts(userId);
        return Ok(posts);
    }

    
    [HttpPut("{postId}")]
    public async Task<IActionResult> EditPost(int postId, [FromBody] PostRequest request)
    {
        
        var userIdRaw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!int.TryParse(userIdRaw, out int userId) || userId == 0)
        {
            return Unauthorized("Brak autoryzacji użytkownika.");
        }

        
        var post = await _postService.GetPostById(postId);

        if (post == null)
        {
            return NotFound("Post nie istnieje.");
        }

        
        if (post.UserId != userId)
        {
            return Unauthorized("Nie masz uprawnień do edytowania tego posta.");
        }

        
        post.Title = request.Title;
        post.Content = request.Content;
        post.Category = request.Category.ToString();  // Jeśli enum
        post.ImageUrl = request.ImageUrl;

        await _postService.UpdatePost(post);

        return Ok(new { message = "Post zaktualizowany!" });
    }

    [HttpDelete("delete/{postId}")]
    public async Task<IActionResult> DeletePost(int postId)
    {
        var userIdRaw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdRaw, out int userId) || userId == 0)
        {
            return Unauthorized("Brak autoryzacji użytkownika.");
        }

        var post = await _postService.GetPostById(postId);
        if (post == null)
        {
            return NotFound("Post nie istnieje.");
        }

        if (post.UserId != userId)
        {
            return Unauthorized("Nie masz uprawnień do usunięcia tego posta.");
        }

        var success = await _postService.DeletePost(userId, postId);

        if (!success)
        {
            return BadRequest("Nie udało się usunąć posta.");
        }

        return Ok(new { message = "Post usunięty!" });
    }

    
}
