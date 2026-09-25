using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Comments;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Notifications
{
    public class NotificationService : INotificationService
    {
        private const string CursorScope = "notifications";
        private const string NotFoundMessage = "Powiadomienie nie istnieje.";

        // Tytuł tylko dla posta Published — ukryty/usunięty post nie ujawnia tytułu. Treści komentarza nie ma wcale.
        private static readonly Expression<Func<Notification, NotificationResponse>> ToResponse = n => new NotificationResponse(
            n.Id,
            n.Type,
            n.Actor.Username,
            n.PostId,
            n.CommentId,
            n.Post.Status == ContentStatus.Published ? n.Post.Title : null,
            n.Post.Status == ContentStatus.Published && (n.Comment == null || n.Comment.Status == ContentStatus.Published),
            n.CreatedAt,
            n.ReadAt);

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;

        public NotificationService(ApplicationDbContext context, TimeProvider time)
        {
            _context = context;
            _time = time;
        }

        public void StageCommentOnPost(int postAuthorId, Comment comment)
        {
            if (postAuthorId == comment.UserId)
                return;

            // Nawigacja do (jeszcze niezapisanego) komentarza — EF ustawi CommentId w tym samym SaveChanges.
            _context.Notifications.Add(new Notification
            {
                UserId = postAuthorId,
                Type = NotificationType.CommentOnPost,
                ActorUserId = comment.UserId,
                PostId = comment.PostId,
                Comment = comment,
                CreatedAt = comment.CreatedAt
            });
            SansPost.Infrastructure.Hosting.SansPostTelemetry.NotificationsCreated.Add(1);
        }

        public async Task<ServiceResult<KeysetPage<NotificationResponse>>> GetAsync(int userId, NotificationPageQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > NotificationLimits.MaxPageSize)
                return ServiceResult<KeysetPage<NotificationResponse>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{NotificationLimits.MaxPageSize}.");

            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, CursorScope, out cursor))
                return ServiceResult<KeysetPage<NotificationResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            var notifications = _context.Notifications.AsNoTracking().Where(n => n.UserId == userId);

            // CreatedAt DESC, Id DESC — postać sargable pod IX_notifications_user_feed.
            if (cursor is not null)
                notifications = notifications.Where(n => n.CreatedAt <= cursor.CreatedAt && (n.CreatedAt < cursor.CreatedAt || n.Id < cursor.Id));

            var page = await notifications
                .OrderByDescending(n => n.CreatedAt)
                .ThenByDescending(n => n.Id)
                .Take(query.Limit + 1)
                .Select(ToResponse)
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new KeysetCursor(CursorScope, page[^1].CreatedAt, page[^1].Id).Encode() : null;
            return ServiceResult<KeysetPage<NotificationResponse>>.Success(new KeysetPage<NotificationResponse>(page, nextCursor, hasMore));
        }

        // COUNT w SQL (częściowy IX_notifications_unread) — bez ładowania powiadomień do pamięci.
        public Task<int> GetUnreadCountAsync(int userId, CancellationToken cancellationToken = default) =>
            _context.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

        public async Task<ServiceResult<NotificationResponse>> MarkReadAsync(int userId, int notificationId, CancellationToken cancellationToken = default)
        {
            // Jeden UPDATE z warunkiem właściciela; ReadAt IS NULL → pierwszy odczyt zostaje (idempotentnie).
            await _context.Notifications
                .Where(n => n.Id == notificationId && n.UserId == userId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, _time.GetUtcNow().UtcDateTime), cancellationToken);

            var current = await _context.Notifications
                .AsNoTracking()
                .Where(n => n.Id == notificationId && n.UserId == userId)
                .Select(ToResponse)
                .FirstOrDefaultAsync(cancellationToken);

            return current is null
                ? ServiceResult<NotificationResponse>.Fail(ServiceError.NotFound, NotFoundMessage)
                : ServiceResult<NotificationResponse>.Success(current);
        }

        public Task<int> MarkAllReadAsync(int userId, CancellationToken cancellationToken = default) =>
            _context.Notifications
                .Where(n => n.UserId == userId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, _time.GetUtcNow().UtcDateTime), cancellationToken);
    }
}
