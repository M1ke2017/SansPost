using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Comments
{
    public static class CommentLimits
    {
        public const int ContentMaxLength = 2000;
        public const int ProfilePreviewLength = 200;
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;
    }

    // Jedyne pole ustawiane przez klienta. Post wynika z trasy, autor z zaufanej tożsamości,
    // Version/CreatedAt/UpdatedAt ustala serwer.
    public sealed class CommentRequest
    {
        [Required(ErrorMessage = "Treść komentarza jest wymagana.")]
        [StringLength(CommentLimits.ContentMaxLength, ErrorMessage = "Komentarz może mieć maksymalnie 2000 znaków.")]
        public string Content { get; set; } = string.Empty;
    }

    public sealed class CommentPageQuery
    {
        public string? Cursor { get; set; }
        public int Limit { get; set; } = CommentLimits.DefaultPageSize;
    }

    public sealed record CommentResponse(
        int Id,
        int PostId,
        string Content,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        int AuthorId,
        string AuthorUsername,
        int Version);

    // Ostatnia aktywność na profilu: komentarz z kontekstem posta, skrócona treść.
    public sealed record AuthorCommentResponse(
        int Id,
        int PostId,
        string PostTitle,
        string ContentPreview,
        DateTime CreatedAt);
}
