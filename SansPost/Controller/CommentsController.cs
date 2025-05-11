using Microsoft.AspNetCore.Mvc;
using SansPost.Models;
using SansPost.Services;

[ApiController]
[Route("comments")]
public class CommentsController : ControllerBase
{
    private readonly CommentService _commentService;

    public CommentsController(CommentService commentService)
    {
        _commentService = commentService;
    }

    [HttpPost("add")]
    public async Task<IActionResult> AddComment([FromBody] Comment comment)
    {
        if (comment == null || string.IsNullOrWhiteSpace(comment.Content))
        {
            return BadRequest("Nieprawidłowe dane wejściowe.");
        }

        var result = await _commentService.AddComment(comment.PostId, comment.UserId, comment.Content);

        if (!result)
        {
            return BadRequest("Nie udało się dodać komentarza.");
        }

        return Ok(new { message = "Komentarz dodany pomyślnie!" });
    }

    [HttpGet("post/{postId}")]
    public async Task<IActionResult> GetCommentsByPost(int postId)
    {
        var comments = await _commentService.GetCommentsByPost(postId);

        if (comments == null || !comments.Any())
        {
            return NotFound("Brak komentarzy dla tego posta.");
        }

        return Ok(comments);
    }
}
