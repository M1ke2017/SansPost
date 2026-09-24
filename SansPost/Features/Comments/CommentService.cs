using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Features.Notifications;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Comments
{
    public interface ICommentService
    {
        // Płaska dyskusja: CreatedAt ASC, Id ASC (keyset). NotFound, gdy post nie istnieje lub nie jest publiczny.
        Task<ServiceResult<KeysetPage<CommentResponse>>> GetByPostAsync(int postId, CommentPageQuery query, CancellationToken cancellationToken = default);

        Task<CommentResponse?> GetByIdAsync(int commentId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<AuthorCommentResponse>> GetRecentByAuthorAsync(int userId, int limit, CancellationToken cancellationToken = default);

        Task<ServiceResult<CommentResponse>> AddAsync(int actorUserId, int postId, CommentRequest request, CancellationToken cancellationToken = default);

        Task<ServiceResult<CommentResponse>> UpdateAsync(int actorUserId, int commentId, int expectedVersion, CommentRequest request, CancellationToken cancellationToken = default);

        Task<ServiceResult> DeleteAsync(int actorUserId, int commentId, int expectedVersion, CancellationToken cancellationToken = default);
    }

    public class CommentService : ICommentService
    {
        private const string CursorScope = "comments";
        private const string StaleVersionMessage = "Komentarz został w międzyczasie zmieniony. Odśwież go i spróbuj ponownie.";
        private const string PostNotFoundMessage = "Post nie istnieje.";
        private const string CommentNotFoundMessage = "Komentarz nie istnieje.";

        private static readonly Expression<Func<Comment, CommentResponse>> ToResponse = c => new CommentResponse(
            c.Id, c.PostId, c.Content, c.CreatedAt, c.UpdatedAt, c.UserId, c.User.Username, c.Version);

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly IWriteGuard _writeGuard;
        private readonly INotificationService _notifications;

        public CommentService(ApplicationDbContext context, TimeProvider time, IWriteGuard writeGuard, INotificationService notifications)
        {
            _context = context;
            _time = time;
            _writeGuard = writeGuard;
            _notifications = notifications;
        }

        // Publicznie widoczny komentarz = Published na Published poście. Jedno źródło dla wszystkich odczytów.
        private IQueryable<Comment> VisibleComments => _context.Comments
            .AsNoTracking()
            .Where(c => c.Status == ContentStatus.Published && c.Post.Status == ContentStatus.Published);

        private Task<bool> PublishedPostExistsAsync(int postId, CancellationToken cancellationToken) =>
            _context.Posts.AnyAsync(p => p.Id == postId && p.Status == ContentStatus.Published, cancellationToken);

        public async Task<ServiceResult<KeysetPage<CommentResponse>>> GetByPostAsync(int postId, CommentPageQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > CommentLimits.MaxPageSize)
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{CommentLimits.MaxPageSize}.");

            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, CursorScope, out cursor))
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            if (!await PublishedPostExistsAsync(postId, cancellationToken))
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            // Post już sprawdzony — wystarczy status komentarza (predykat częściowego IX_comments_post_thread).
            var comments = _context.Comments.AsNoTracking()
                .Where(c => c.PostId == postId && c.Status == ContentStatus.Published);

            // Postać sargable: "createdat >= c AND (createdat > c OR id > i)" → Index Cond na IX_comments_post_thread.
            if (cursor is not null)
                comments = comments.Where(c => c.CreatedAt >= cursor.CreatedAt && (c.CreatedAt > cursor.CreatedAt || c.Id > cursor.Id));

            var page = await comments
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Take(query.Limit + 1)
                .Select(ToResponse)
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new KeysetCursor(CursorScope, page[^1].CreatedAt, page[^1].Id).Encode() : null;
            return ServiceResult<KeysetPage<CommentResponse>>.Success(new KeysetPage<CommentResponse>(page, nextCursor, hasMore));
        }

        public async Task<CommentResponse?> GetByIdAsync(int commentId, CancellationToken cancellationToken = default)
        {
            return await VisibleComments
                .Where(c => c.Id == commentId)
                .Select(ToResponse)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<AuthorCommentResponse>> GetRecentByAuthorAsync(int userId, int limit, CancellationToken cancellationToken = default)
        {
            return await VisibleComments
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Take(Math.Clamp(limit, 1, CommentLimits.MaxPageSize))
                .Select(c => new AuthorCommentResponse(
                    c.Id,
                    c.PostId,
                    c.Post.Title,
                    c.Content.Length > CommentLimits.ProfilePreviewLength ? c.Content.Substring(0, CommentLimits.ProfilePreviewLength) : c.Content,
                    c.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        // Bez jawnej transakcji: komentarz i (opcjonalnie) powiadomienie dla autora posta w JEDNYM SaveChanges —
        // atomowo: nie ma powiadomienia bez komentarza. FK comments→posts jest ostatecznym zabezpieczeniem integralności.
        public async Task<ServiceResult<CommentResponse>> AddAsync(int actorUserId, int postId, CommentRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (RequestValidator.Validate(normalized) is { } error)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Validation, error);
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<CommentResponse>.From(denied);

            var postAuthorId = await _context.Posts
                .Where(p => p.Id == postId && p.Status == ContentStatus.Published)
                .Select(p => (int?)p.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (postAuthorId is null)
                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            var comment = new Comment
            {
                PostId = postId,
                UserId = actorUserId,
                Content = normalized.Content,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                Version = 1,
                Status = ContentStatus.Published
            };

            _context.Comments.Add(comment);
            _notifications.StageCommentOnPost(postAuthorId.Value, comment);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Post fizycznie usunięty między sprawdzeniem a INSERT → naruszenie FK. Komentarz nie powstał.
                _context.ChangeTracker.Clear();
                if (await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                    throw;

                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);
            }

            // Post ukryty/usunięty tuż po INSERT → komentarz niewidoczny publicznie (404), dane zachowane.
            var created = await GetByIdAsync(comment.Id, cancellationToken);
            return created is null
                ? ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage)
                : ServiceResult<CommentResponse>.Success(created);
        }

        // Bez jawnej transakcji: jeden UPDATE ... WHERE version = @expected.
        public async Task<ServiceResult<CommentResponse>> UpdateAsync(int actorUserId, int commentId, int expectedVersion, CommentRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (RequestValidator.Validate(normalized) is { } error)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Validation, error);
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<CommentResponse>.From(denied);

            var comment = await LoadEditableAsync(commentId, cancellationToken);
            if (comment is null)
                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, CommentNotFoundMessage);
            if (comment.UserId != actorUserId)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Forbidden, "Nie masz uprawnień do edycji tego komentarza.");
            if (comment.Version != expectedVersion)
                return ServiceResult<CommentResponse>.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            if (comment.Content != normalized.Content)
            {
                comment.Content = normalized.Content;
                comment.Version++;
                comment.UpdatedAt = _time.GetUtcNow().UtcDateTime;

                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return ServiceResult<CommentResponse>.From(await ResolveConcurrencyFailureAsync(commentId, cancellationToken));
                }
            }

            var updated = await GetByIdAsync(commentId, cancellationToken);
            return updated is null
                ? ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, CommentNotFoundMessage)
                : ServiceResult<CommentResponse>.Success(updated);
        }

        // Soft delete: Status = Deleted, jeden UPDATE ... WHERE version = @expected.
        public async Task<ServiceResult> DeleteAsync(int actorUserId, int commentId, int expectedVersion, CancellationToken cancellationToken = default)
        {
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return denied;

            var comment = await LoadEditableAsync(commentId, cancellationToken);
            if (comment is null)
                return ServiceResult.Fail(ServiceError.NotFound, CommentNotFoundMessage);
            if (comment.UserId != actorUserId)
                return ServiceResult.Fail(ServiceError.Forbidden, "Nie masz uprawnień do usunięcia tego komentarza.");
            if (comment.Version != expectedVersion)
                return ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            comment.Status = ContentStatus.Deleted;
            comment.DeletedAt = _time.GetUtcNow().UtcDateTime;
            comment.Version++;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return await ResolveConcurrencyFailureAsync(commentId, cancellationToken);
            }

            return ServiceResult.Success();
        }

        // Śledzony komentarz, który autor może zmienić: widoczny publicznie (Published na Published poście).
        private Task<Comment?> LoadEditableAsync(int commentId, CancellationToken cancellationToken) =>
            _context.Comments.FirstOrDefaultAsync(
                c => c.Id == commentId && c.Status == ContentStatus.Published && c.Post.Status == ContentStatus.Published,
                cancellationToken);

        private async Task<ServiceResult> ResolveConcurrencyFailureAsync(int commentId, CancellationToken cancellationToken)
        {
            _context.ChangeTracker.Clear();

            return await VisibleComments.AnyAsync(c => c.Id == commentId, cancellationToken)
                ? ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage)
                : ServiceResult.Fail(ServiceError.NotFound, "Komentarz został usunięty.");
        }

        private static CommentRequest Normalize(CommentRequest request) => new()
        {
            Content = request.Content?.Trim() ?? string.Empty
        };
    }
}
