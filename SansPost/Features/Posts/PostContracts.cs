using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Posts
{
    // Request tworzenia i edycji posta. Celowo bez UserId — autorem jest zaufana tożsamość wykonawcy.
    public sealed class PostRequest
    {
        [Required(ErrorMessage = "Tytuł jest wymagany.")]
        [StringLength(PostLimits.TitleMaxLength, MinimumLength = PostLimits.TitleMinLength,
            ErrorMessage = "Tytuł musi mieć od 3 do 150 znaków.")]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "Treść jest wymagana.")]
        [StringLength(PostLimits.ContentMaxLength, ErrorMessage = "Treść może mieć maksymalnie 10000 znaków.")]
        public string Content { get; set; } = string.Empty;

        [Required(ErrorMessage = "Kategoria jest wymagana.")]
        public PostCategory? Category { get; set; }

        [StringLength(PostLimits.ImageUrlMaxLength)]
        public string? ImageUrl { get; set; }
    }

    public enum PostSort
    {
        Newest,
        Oldest
    }

    // Parametry feedu (query string). Keyset pagination: Cursor z poprzedniej odpowiedzi.
    public sealed class PostFeedQuery
    {
        public string? Cursor { get; set; }
        public int Limit { get; set; } = PostLimits.DefaultPageSize;
        public PostSort Sort { get; set; } = PostSort.Newest;
        public PostCategory? Category { get; set; }
        public int? AuthorId { get; set; }
    }

    // Element feedu — skrócona treść liczona w SQL, bez pełnego Content.
    public sealed record PostSummaryResponse(
        int Id,
        string Title,
        string ContentPreview,
        bool IsContentTruncated,
        PostCategory Category,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        int AuthorId,
        string AuthorUsername);

    public sealed record PostDetailsResponse(
        int Id,
        string Title,
        string Content,
        PostCategory Category,
        string? ImageUrl,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        int AuthorId,
        string AuthorUsername,
        int Version);

    public sealed record PostFeedResponse(
        IReadOnlyList<PostSummaryResponse> Items,
        string? NextCursor,
        bool HasMore);

    public sealed record PostQuotaResponse(int Limit, int Used, int Remaining);
}
