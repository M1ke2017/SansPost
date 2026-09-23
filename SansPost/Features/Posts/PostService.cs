using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Posts
{
    // Granica modułu Posts. Konsumenci (Controllers, Blazor) znają tylko ten kontrakt i DTO — nie encje ani DbContext.
    public interface IPostService
    {
        Task<ServiceResult<PostFeedResponse>> GetFeedAsync(PostFeedQuery query, CancellationToken cancellationToken = default);

        Task<PostDetailsResponse?> GetByIdAsync(int postId, CancellationToken cancellationToken = default);

        Task<PostQuotaResponse?> GetQuotaAsync(int userId, CancellationToken cancellationToken = default);

        Task<ServiceResult<PostDetailsResponse>> CreateAsync(int actorUserId, PostRequest request, CancellationToken cancellationToken = default);

        // expectedVersion = wersja, którą klient edytował (ETag / If-Match).
        Task<ServiceResult<PostDetailsResponse>> UpdateAsync(int actorUserId, int postId, int expectedVersion, PostRequest request, CancellationToken cancellationToken = default);

        // expectedVersion opcjonalne — jeśli podane, usunięcie wymaga zgodnej wersji.
        Task<ServiceResult> DeleteAsync(int actorUserId, int postId, int? expectedVersion, CancellationToken cancellationToken = default);
    }

    public class PostService : IPostService
    {
        private const string StaleVersionMessage = "Post został w międzyczasie zmieniony. Odśwież go i spróbuj ponownie.";

        private static readonly Expression<Func<Post, PostSummaryResponse>> ToSummary = p => new PostSummaryResponse(
            p.Id,
            p.Title,
            p.Content.Length > PostLimits.PreviewLength ? p.Content.Substring(0, PostLimits.PreviewLength) : p.Content,
            p.Content.Length > PostLimits.PreviewLength,
            p.Category,
            p.CreatedAt,
            p.UpdatedAt,
            p.UserId,
            p.User.Username);

        private static readonly Expression<Func<Post, PostDetailsResponse>> ToDetails = p => new PostDetailsResponse(
            p.Id, p.Title, p.Content, p.Category, p.ImageUrl, p.CreatedAt, p.UpdatedAt, p.UserId, p.User.Username, p.Version);

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;

        public PostService(ApplicationDbContext context, TimeProvider time)
        {
            _context = context;
            _time = time;
        }

        public async Task<ServiceResult<PostFeedResponse>> GetFeedAsync(PostFeedQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > PostLimits.MaxPageSize)
                return ServiceResult<PostFeedResponse>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{PostLimits.MaxPageSize}.");
            if (!Enum.IsDefined(query.Sort) || (query.Category is { } c && !Enum.IsDefined(c)))
                return ServiceResult<PostFeedResponse>.Fail(ServiceError.Validation, "Nieprawidłowe sortowanie lub kategoria.");

            FeedCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !FeedCursor.TryDecode(query.Cursor, query.Sort, out cursor))
                return ServiceResult<PostFeedResponse>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            var posts = _context.Posts.AsNoTracking();

            if (query.Category is { } category)
                posts = posts.Where(p => p.Category == category);
            if (query.AuthorId is { } authorId)
                posts = posts.Where(p => p.UserId == authorId);

            // Keyset pagination: stabilny porządek (CreatedAt, Id), bez OFFSET.
            // Postać "createdat <= c AND (createdat < c OR id < i)" daje PostgreSQL warunek zakresu na indeksie (Index Cond),
            // więc skan zaczyna się od kursora zamiast od początku feedu.
            if (query.Sort == PostSort.Newest)
            {
                if (cursor is not null)
                    posts = posts.Where(p => p.CreatedAt <= cursor.CreatedAt && (p.CreatedAt < cursor.CreatedAt || p.Id < cursor.Id));
                posts = posts.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id);
            }
            else
            {
                if (cursor is not null)
                    posts = posts.Where(p => p.CreatedAt >= cursor.CreatedAt && (p.CreatedAt > cursor.CreatedAt || p.Id > cursor.Id));
                posts = posts.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id);
            }

            // limit + 1 → informacja o kolejnej stronie bez COUNT(*).
            var page = await posts.Take(query.Limit + 1).Select(ToSummary).ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new FeedCursor(query.Sort, page[^1].CreatedAt, page[^1].Id).Encode() : null;
            return ServiceResult<PostFeedResponse>.Success(new PostFeedResponse(page, nextCursor, hasMore));
        }

        public async Task<PostDetailsResponse?> GetByIdAsync(int postId, CancellationToken cancellationToken = default)
        {
            return await _context.Posts
                .AsNoTracking()
                .Where(p => p.Id == postId)
                .Select(ToDetails)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<PostQuotaResponse?> GetQuotaAsync(int userId, CancellationToken cancellationToken = default)
        {
            if (!await _context.Users.AnyAsync(u => u.Id == userId, cancellationToken))
                return null;

            var limit = await GetPostLimitAsync(userId, cancellationToken);
            var used = await _context.Posts.CountAsync(p => p.UserId == userId, cancellationToken);

            return new PostQuotaResponse(limit, used, Math.Max(0, limit - used));
        }

        public async Task<ServiceResult<PostDetailsResponse>> CreateAsync(int actorUserId, PostRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (Validate(normalized) is { } error)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Validation, error);

            // Jawna transakcja: COUNT → INSERT musi być atomowe względem innych requestów tego samego użytkownika.
            // Blokada wiersza users serializuje je w PostgreSQL (także między instancjami aplikacji).
            // Wyjście bez Commit (limit, wyjątek, anulowanie) = rollback przy Dispose.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            if (!await _context.LockUserRowAsync(actorUserId, cancellationToken))
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");

            var limit = await GetPostLimitAsync(actorUserId, cancellationToken);
            var used = await _context.Posts.CountAsync(p => p.UserId == actorUserId, cancellationToken);
            if (used >= limit)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Forbidden, $"Osiągnięto limit postów ({limit}).");

            var post = new Post
            {
                UserId = actorUserId,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                Version = 1
            };
            Apply(post, normalized);

            _context.Posts.Add(post);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ServiceResult<PostDetailsResponse>.Success((await GetByIdAsync(post.Id, cancellationToken))!);
        }

        // Bez jawnej transakcji: jeden UPDATE z warunkiem na Version (SaveChanges jest atomowe).
        public async Task<ServiceResult<PostDetailsResponse>> UpdateAsync(int actorUserId, int postId, int expectedVersion, PostRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (Validate(normalized) is { } error)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Validation, error);

            var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);
            if (post is null)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.NotFound, "Post nie istnieje.");
            if (post.UserId != actorUserId)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Forbidden, "Nie masz uprawnień do edycji tego posta.");
            if (post.Version != expectedVersion)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            if (Apply(post, normalized))
            {
                post.Version++;
                post.UpdatedAt = _time.GetUtcNow().UtcDateTime;

                try
                {
                    // UPDATE posts SET ... WHERE id = @id AND version = @expectedVersion
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    var failure = await ResolveConcurrencyFailureAsync(postId, versionWasExpected: true, cancellationToken);
                    return ServiceResult<PostDetailsResponse>.Fail(failure.Error, failure.Message!);
                }
            }

            var updated = await GetByIdAsync(postId, cancellationToken);
            return updated is null
                ? ServiceResult<PostDetailsResponse>.Fail(ServiceError.NotFound, "Post nie istnieje.")
                : ServiceResult<PostDetailsResponse>.Success(updated);
        }

        public async Task<ServiceResult> DeleteAsync(int actorUserId, int postId, int? expectedVersion, CancellationToken cancellationToken = default)
        {
            var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);
            if (post is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Post nie istnieje.");
            if (post.UserId != actorUserId)
                return ServiceResult.Fail(ServiceError.Forbidden, "Nie masz uprawnień do usunięcia tego posta.");
            if (expectedVersion is { } version && post.Version != version)
                return ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            _context.Posts.Remove(post);
            try
            {
                // DELETE ... WHERE id = @id AND version = @loadedVersion; komentarze/polubienia usuwa FK CASCADE.
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return await ResolveConcurrencyFailureAsync(postId, expectedVersion is not null, cancellationToken);
            }

            return ServiceResult.Success();
        }

        private async Task<ServiceResult> ResolveConcurrencyFailureAsync(int postId, bool versionWasExpected, CancellationToken cancellationToken)
        {
            _context.ChangeTracker.Clear();

            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return ServiceResult.Fail(ServiceError.NotFound, "Post został usunięty.");

            return versionWasExpected
                ? ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage)
                : ServiceResult.Fail(ServiceError.Conflict, StaleVersionMessage);
        }

        private async Task<int> GetPostLimitAsync(int userId, CancellationToken cancellationToken)
        {
            var subscription = await _context.Subscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

            return Subscription.EffectiveTypeOf(subscription, _time.GetUtcNow().UtcDateTime) == SubscriptionType.Premium
                ? PostLimits.PremiumPostLimit
                : PostLimits.FreePostLimit;
        }

        private static PostRequest Normalize(PostRequest request) => new()
        {
            Title = request.Title?.Trim() ?? string.Empty,
            Content = request.Content?.Trim() ?? string.Empty,
            Category = request.Category,
            ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim()
        };

        private static string? Validate(PostRequest request)
        {
            if (RequestValidator.Validate(request) is { } error)
                return error;

            return Enum.IsDefined(request.Category!.Value) ? null : "Nieprawidłowa kategoria.";
        }

        // Zwraca true tylko przy realnej zmianie — wtedy rośnie Version i ustawiany jest UpdatedAt.
        private static bool Apply(Post post, PostRequest request)
        {
            var category = request.Category!.Value;
            var changed = post.Title != request.Title
                || post.Content != request.Content
                || post.Category != category
                || post.ImageUrl != request.ImageUrl;

            post.Title = request.Title;
            post.Content = request.Content;
            post.Category = category;
            post.ImageUrl = request.ImageUrl;

            return changed;
        }
    }
}
