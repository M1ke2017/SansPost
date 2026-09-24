namespace SansPost.Features.Notifications
{
    public static class NotificationLimits
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;
    }

    // Celowo bez UserId odbiorcy — tożsamość wyłącznie z uwierzytelnienia.
    public sealed class NotificationPageQuery
    {
        public string? Cursor { get; set; }
        public int Limit { get; set; } = NotificationLimits.DefaultPageSize;
    }

    // Publiczny widok powiadomienia: przydomek aktora, tytuł posta, znaczniki czasu, identyfikatory celu.
    // Bez emaili, hashy, AuthVersion, danych moderacji i bez treści komentarza.
    // TargetAvailable = false → post ukryty/usunięty: tytuł nie jest zwracany, UI pokazuje "treść niedostępna".
    public sealed record NotificationResponse(
        int Id,
        NotificationType Type,
        string ActorUsername,
        int PostId,
        int? CommentId,
        string? PostTitle,
        bool TargetAvailable,
        DateTime CreatedAt,
        DateTime? ReadAt)
    {
        public bool IsRead => ReadAt is not null;
    }

    public sealed record UnreadCountResponse(int Count);

    public sealed record MarkAllReadResponse(int Updated);
}
