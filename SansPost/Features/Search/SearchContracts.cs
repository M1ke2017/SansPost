using SansPost.Features.Posts;

namespace SansPost.Features.Search
{
    public static class SearchLimits
    {
        public const int QueryMinLength = 2;

        // Ogranicza koszt zapytania (liczbę leksemów w tsquery) przy dowolnym inpucie.
        public const int QueryMaxLength = 100;

        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;

        public const int UserPrefixMinLength = 2;
        public const int UserPrefixMaxLength = 50;
        public const int DefaultUserResults = 10;
        public const int MaxUserResults = 20;
    }

    // Kontrakt wyszukiwania — niezależny od implementacji (dziś PostgreSQL FTS, w przyszłości np. zewnętrzny serwis).
    public sealed class PostSearchQuery
    {
        public string? Q { get; set; }
        public PostCategory? Category { get; set; }
        public string? Cursor { get; set; }
        public int Limit { get; set; } = SearchLimits.DefaultPageSize;
    }

    // Wynik listy: podgląd zamiast pełnej treści; bez danych implementacyjnych FTS (tsvector, surowy ranking).
    public sealed record PostSearchResult(
        int Id,
        string Title,
        string Preview,
        bool IsPreviewTruncated,
        PostCategory Category,
        DateTime CreatedAt,
        int AuthorId,
        string AuthorUsername,
        int LikeCount,
        int CommentCount,
        bool LikedByCurrentUser);

    // Publiczny lookup autorów — wyłącznie dane z publicznego profilu.
    public sealed record UserSearchResult(int UserId, string Username, DateTime JoinedAt, int PostCount);
}
