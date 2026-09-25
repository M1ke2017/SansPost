using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Reactions
{
    public sealed record LikeSummaryResponse(int PostId, int LikeCount, bool LikedByCurrentUser);

    // API "desired state" zamiast toggle: Like = "po operacji post JEST polubiony", Unlike = "NIE JEST".
    // Obie operacje są idempotentne — bezpieczne przy retry i równoległych requestach.
    public class LikeService
    {
        private const string PostNotFoundMessage = "Post nie istnieje.";

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly WriteGuard _writeGuard;

        public LikeService(ApplicationDbContext context, TimeProvider time, WriteGuard writeGuard)
        {
            _context = context;
            _time = time;
            _writeGuard = writeGuard;
        }

        // null = post nie istnieje lub nie jest publiczny.
        public async Task<LikeSummaryResponse?> GetSummaryAsync(int postId, int? viewerUserId, CancellationToken cancellationToken = default)
        {
            // Jedno zapytanie: liczba polubień + stan dla bieżącego użytkownika, bez ładowania rekordów Like.
            return await _context.Posts
                .AsNoTracking()
                .Where(p => p.Id == postId && p.Status == ContentStatus.Published)
                .Select(p => new LikeSummaryResponse(
                    p.Id,
                    _context.Likes.Count(l => l.PostId == p.Id),
                    viewerUserId != null && _context.Likes.Any(l => l.PostId == p.Id && l.UserId == viewerUserId)))
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Pojedynczy atomowy INSERT ... ON CONFLICT DO NOTHING: równoległe PUT tego samego użytkownika
        // nie tworzą duplikatu ani wyjątku UNIQUE. Składnia działa w PostgreSQL i SQLite.
        public async Task<ServiceResult<LikeSummaryResponse>> LikeAsync(int actorUserId, int postId, CancellationToken cancellationToken = default)
        {
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<LikeSummaryResponse>.From(denied);
            if (!await PublishedPostExistsAsync(postId, cancellationToken))
                return ServiceResult<LikeSummaryResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            var now = _time.GetUtcNow().UtcDateTime;
            try
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO likes (postid, userid, createdat) VALUES ({postId}, {actorUserId}, {now}) ON CONFLICT (postid, userid) DO NOTHING",
                    cancellationToken);
            }
            catch (DbException)
            {
                // Post fizycznie usunięty między sprawdzeniem a INSERT → naruszenie FK; polubienie nie powstało.
                if (await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                    throw;

                return ServiceResult<LikeSummaryResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);
            }

            return await SummaryResultAsync(postId, actorUserId, cancellationToken);
        }

        // Pojedynczy atomowy DELETE; brak rekordu to też sukces (stan docelowy osiągnięty).
        public async Task<ServiceResult<LikeSummaryResponse>> UnlikeAsync(int actorUserId, int postId, CancellationToken cancellationToken = default)
        {
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<LikeSummaryResponse>.From(denied);
            if (!await PublishedPostExistsAsync(postId, cancellationToken))
                return ServiceResult<LikeSummaryResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage);

            await _context.Likes
                .Where(l => l.PostId == postId && l.UserId == actorUserId)
                .ExecuteDeleteAsync(cancellationToken);

            return await SummaryResultAsync(postId, actorUserId, cancellationToken);
        }

        private async Task<ServiceResult<LikeSummaryResponse>> SummaryResultAsync(int postId, int actorUserId, CancellationToken cancellationToken)
        {
            var summary = await GetSummaryAsync(postId, actorUserId, cancellationToken);
            return summary is null
                ? ServiceResult<LikeSummaryResponse>.Fail(ServiceError.NotFound, PostNotFoundMessage)
                : ServiceResult<LikeSummaryResponse>.Success(summary);
        }

        private Task<bool> PublishedPostExistsAsync(int postId, CancellationToken cancellationToken) =>
            _context.Posts.AnyAsync(p => p.Id == postId && p.Status == ContentStatus.Published, cancellationToken);
    }
}
