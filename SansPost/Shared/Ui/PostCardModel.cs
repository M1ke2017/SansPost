using SansPost.Features.Posts;
using SansPost.Features.Search;

namespace SansPost.Shared.Ui
{
    // Wspólny model karty dla feedu, wyników wyszukiwania i profilu (różne DTO, ten sam wygląd).
    public sealed record PostCardModel(
        int Id,
        string Title,
        string Preview,
        bool IsTruncated,
        PostCategory Category,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        int AuthorId,
        string AuthorUsername,
        int LikeCount,
        int CommentCount,
        bool LikedByCurrentUser)
    {
        public static PostCardModel From(PostSummaryResponse p) => new(p.Id, p.Title, p.ContentPreview, p.IsContentTruncated,
            p.Category, p.CreatedAt, p.UpdatedAt, p.AuthorId, p.AuthorUsername, p.LikeCount, p.CommentCount, p.LikedByCurrentUser);

        public static PostCardModel From(PostSearchResult r) => new(r.Id, r.Title, r.Preview, r.IsPreviewTruncated,
            r.Category, r.CreatedAt, null, r.AuthorId, r.AuthorUsername, r.LikeCount, r.CommentCount, r.LikedByCurrentUser);

        // Wyróżniony post: szczegóły skracane do podglądu po stronie UI (pełna treść nie trafia do karty).
        public static PostCardModel From(PostDetailsResponse d)
        {
            var truncated = d.Content.Length > PostLimits.PreviewLength;
            return new(d.Id, d.Title, truncated ? d.Content[..PostLimits.PreviewLength] : d.Content, truncated,
                d.Category, d.CreatedAt, d.UpdatedAt, d.AuthorId, d.AuthorUsername, d.LikeCount, d.CommentCount, d.LikedByCurrentUser);
        }
    }
}
