using System.ComponentModel.DataAnnotations;

namespace SansPost.Features.Posts
{
    // Request tworzenia i edycji posta. Celowo bez UserId — autorem jest zaufana tożsamość wykonawcy.
    // Bez ImageUrl (Sprint 10): pole legacy bez uploadu i bez użycia w UI — nieznane pole JSON jest ignorowane,
    // istniejąca wartość w bazie zostaje (kolumna legacy, tylko do odczytu w PostDetailsResponse).
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
    }

    public enum PostSort
    {
        Newest,
        Oldest,

        // Zaangażowanie (polubienia, komentarze) z karą za wiek — liczone w SQL. Formuła: PostLimits.Popular*.
        Popular
    }

    // Parametry feedu (query string). Keyset pagination: Cursor z poprzedniej odpowiedzi.
    // Celowo bez "viewer" — tożsamość odbiorcy (LikedByCurrentUser) pochodzi wyłącznie z uwierzytelnienia.
    public sealed class PostFeedQuery
    {
        public string? Cursor { get; set; }
        public int Limit { get; set; } = PostLimits.DefaultPageSize;
        public PostSort Sort { get; set; } = PostSort.Newest;
        public PostCategory? Category { get; set; }
        public int? AuthorId { get; set; }
    }

    // Element feedu — skrócona treść i liczniki liczone w SQL (jedna projekcja), bez pełnego Content.
    // LikedByCurrentUser = false dla anonimowego odbiorcy.
    public sealed record PostSummaryResponse(
        int Id,
        string Title,
        string ContentPreview,
        bool IsContentTruncated,
        PostCategory Category,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        int AuthorId,
        string AuthorUsername,
        int LikeCount,
        int CommentCount,
        bool LikedByCurrentUser);

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
        int Version,
        int LikeCount,
        int CommentCount,
        bool LikedByCurrentUser);

    public sealed record PostQuotaResponse(int Limit, int Used, int Remaining);

    // Discovery kategorii: wszystkie wartości zamkniętego zestawu, także bez postów.
    public sealed record CategorySummaryResponse(PostCategory Category, int PostCount, DateTime? LatestPostAt);
}
