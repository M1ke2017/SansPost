using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Reactions
{
    public sealed record LikeSummaryResponse(int PostId, int Count, bool LikedByCurrentUser);

    public interface ILikeService
    {
        // null = post nie istnieje.
        Task<LikeSummaryResponse?> GetSummaryAsync(int postId, int? currentUserId, CancellationToken cancellationToken = default);

        Task<ServiceResult<LikeSummaryResponse>> ToggleAsync(int actorUserId, int postId, CancellationToken cancellationToken = default);
    }

    public class LikeService : ILikeService
    {
        private readonly ApplicationDbContext _context;

        public LikeService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<LikeSummaryResponse?> GetSummaryAsync(int postId, int? currentUserId, CancellationToken cancellationToken = default)
        {
            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return null;

            var count = await _context.Likes.CountAsync(l => l.PostId == postId, cancellationToken);
            var liked = currentUserId is int userId
                && await _context.Likes.AnyAsync(l => l.PostId == postId && l.UserId == userId, cancellationToken);

            return new LikeSummaryResponse(postId, count, liked);
        }

        public async Task<ServiceResult<LikeSummaryResponse>> ToggleAsync(int actorUserId, int postId, CancellationToken cancellationToken = default)
        {
            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return ServiceResult<LikeSummaryResponse>.Fail(ServiceError.NotFound, "Post nie istnieje.");

            var existing = await _context.Likes
                .FirstOrDefaultAsync(l => l.PostId == postId && l.UserId == actorUserId, cancellationToken);

            if (existing is not null)
                _context.Likes.Remove(existing);
            else
                _context.Likes.Add(new Like { PostId = postId, UserId = actorUserId });

            await _context.SaveChangesAsync(cancellationToken);

            var summary = await GetSummaryAsync(postId, actorUserId, cancellationToken);
            return ServiceResult<LikeSummaryResponse>.Success(summary!);
        }
    }
}
