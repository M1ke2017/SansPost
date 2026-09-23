using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Comments
{
    public interface ICommentService
    {
        // Płaska dyskusja: CreatedAt ASC, Id ASC (keyset). NotFound, gdy post nie istnieje.
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

        private static readonly Expression<Func<Comment, CommentResponse>> ToResponse = c => new CommentResponse(
            c.Id, c.PostId, c.Content, c.CreatedAt, c.UpdatedAt, c.UserId, c.User.Username, c.Version);

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;

        public CommentService(ApplicationDbContext context, TimeProvider time)
        {
            _context = context;
            _time = time;
        }

        public async Task<ServiceResult<KeysetPage<CommentResponse>>> GetByPostAsync(int postId, CommentPageQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > CommentLimits.MaxPageSize)
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{CommentLimits.MaxPageSize}.");

            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, CursorScope, out cursor))
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return ServiceResult<KeysetPage<CommentResponse>>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            var comments = _context.Comments.AsNoTracking().Where(c => c.PostId == postId);

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
            return await _context.Comments
                .AsNoTracking()
                .Where(c => c.Id == commentId)
                .Select(ToResponse)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<AuthorCommentResponse>> GetRecentByAuthorAsync(int userId, int limit, CancellationToken cancellationToken = default)
        {
            return await _context.Comments
                .AsNoTracking()
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

        // Bez jawnej transakcji: pojedynczy INSERT. FK comments→posts jest ostatecznym zabezpieczeniem integralności.
        public async Task<ServiceResult<CommentResponse>> AddAsync(int actorUserId, int postId, CommentRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (RequestValidator.Validate(normalized) is { } error)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Validation, error);

            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            var comment = new Comment
            {
                PostId = postId,
                UserId = actorUserId,
                Content = normalized.Content,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                Version = 1
            };

            _context.Comments.Add(comment);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Post usunięty między sprawdzeniem a INSERT → naruszenie FK. Komentarz nie powstał (brak sieroty).
                _context.ChangeTracker.Clear();
                if (await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                    throw;

                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);
            }

            var created = await GetByIdAsync(comment.Id, cancellationToken);
            return created is null
                ? ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage)  // post (i kaskadowo komentarz) usunięty tuż po INSERT
                : ServiceResult<CommentResponse>.Success(created);
        }

        // Bez jawnej transakcji: jeden UPDATE ... WHERE version = @expected.
        public async Task<ServiceResult<CommentResponse>> UpdateAsync(int actorUserId, int commentId, int expectedVersion, CommentRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (RequestValidator.Validate(normalized) is { } error)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Validation, error);

            var comment = await _context.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
            if (comment is null)
                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, "Komentarz nie istnieje.");
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
                    var failure = await ResolveConcurrencyFailureAsync(commentId, cancellationToken);
                    return ServiceResult<CommentResponse>.Fail(failure.Error, failure.Message!);
                }
            }

            var updated = await GetByIdAsync(commentId, cancellationToken);
            return updated is null
                ? ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, "Komentarz nie istnieje.")
                : ServiceResult<CommentResponse>.Success(updated);
        }

        // Bez jawnej transakcji: jeden DELETE ... WHERE version = @expected.
        public async Task<ServiceResult> DeleteAsync(int actorUserId, int commentId, int expectedVersion, CancellationToken cancellationToken = default)
        {
            var comment = await _context.Comments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
            if (comment is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Komentarz nie istnieje.");
            if (comment.UserId != actorUserId)
                return ServiceResult.Fail(ServiceError.Forbidden, "Nie masz uprawnień do usunięcia tego komentarza.");
            if (comment.Version != expectedVersion)
                return ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            _context.Comments.Remove(comment);
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

        private async Task<ServiceResult> ResolveConcurrencyFailureAsync(int commentId, CancellationToken cancellationToken)
        {
            _context.ChangeTracker.Clear();

            return await _context.Comments.AnyAsync(c => c.Id == commentId, cancellationToken)
                ? ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage)
                : ServiceResult.Fail(ServiceError.NotFound, "Komentarz został usunięty.");
        }

        private static CommentRequest Normalize(CommentRequest request) => new()
        {
            Content = request.Content?.Trim() ?? string.Empty
        };
    }
}
