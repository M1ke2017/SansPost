using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Comments
{
    // Celowo bez PostId i UserId — post wynika z trasy, autor z zalogowanego użytkownika.
    public sealed class CommentRequest
    {
        [Required(ErrorMessage = "Treść komentarza jest wymagana.")]
        [StringLength(2000, ErrorMessage = "Komentarz może mieć maksymalnie 2000 znaków.")]
        public string Content { get; set; } = string.Empty;
    }

    public sealed record CommentResponse(
        int Id,
        int PostId,
        string Content,
        DateTime CreatedAt,
        int AuthorId,
        string AuthorUsername);
}
