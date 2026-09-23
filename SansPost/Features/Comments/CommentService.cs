using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Comments
{
    public interface ICommentService
    {
        // null = post nie istnieje.
        Task<IReadOnlyList<CommentResponse>?> GetByPostAsync(int postId, CancellationToken cancellationToken = default);

        Task<ServiceResult<CommentResponse>> AddAsync(int actorUserId, int postId, CommentRequest request, CancellationToken cancellationToken = default);

        Task<ServiceResult> DeleteAsync(int actorUserId, int commentId, CancellationToken cancellationToken = default);
    }

    public class CommentService : ICommentService
    {
        private static readonly Expression<Func<Comment, CommentResponse>> ToResponse = c => new CommentResponse(
            c.Id, c.PostId, c.Content, c.CreatedAt, c.UserId, c.User.Username);

        private readonly ApplicationDbContext _context;

        public CommentService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<CommentResponse>?> GetByPostAsync(int postId, CancellationToken cancellationToken = default)
        {
            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return null;

            return await _context.Comments
                .AsNoTracking()
                .Where(c => c.PostId == postId)
                .OrderByDescending(c => c.CreatedAt)
                .Select(ToResponse)
                .ToListAsync(cancellationToken);
        }

        public async Task<ServiceResult<CommentResponse>> AddAsync(int actorUserId, int postId, CommentRequest request, CancellationToken cancellationToken = default)
        {
            if (RequestValidator.Validate(request) is { } error)
                return ServiceResult<CommentResponse>.Fail(ServiceError.Validation, error);

            if (!await _context.Posts.AnyAsync(p => p.Id == postId, cancellationToken))
                return ServiceResult<CommentResponse>.Fail(ServiceError.NotFound, "Post nie istnieje.");

            var comment = new Comment
            {
                PostId = postId,
                UserId = actorUserId,
                Content = request.Content.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _context.Comments.Add(comment);
            await _context.SaveChangesAsync(cancellationToken);

            var created = await _context.Comments
                .AsNoTracking()
                .Where(c => c.Id == comment.Id)
                .Select(ToResponse)
                .FirstAsync(cancellationToken);

            return ServiceResult<CommentResponse>.Success(created);
        }

        public async Task<ServiceResult> DeleteAsync(int actorUserId, int commentId, CancellationToken cancellationToken = default)
        {
            var comment = await _context.Comments.FindAsync(new object[] { commentId }, cancellationToken);
            if (comment is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Komentarz nie istnieje.");
            if (comment.UserId != actorUserId)
                return ServiceResult.Fail(ServiceError.Forbidden, "Nie masz uprawnień do usunięcia tego komentarza.");

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync(cancellationToken);
            return ServiceResult.Success();
        }
    }
}
