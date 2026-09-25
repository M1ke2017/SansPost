using Microsoft.EntityFrameworkCore;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Profiles
{
    // Publiczny profil — wyłącznie dane bezpieczne do pokazania każdemu.
    // Celowo bez: Email, NormalizedEmail, PasswordHash, Role, Subscription, refresh tokenów.
    public sealed record PublicProfileResponse(
        int UserId,
        string Username,
        DateTime JoinedAt,
        int PostCount,
        int CommentCount,
        IReadOnlyList<PostSummaryResponse> RecentPosts,
        IReadOnlyList<AuthorCommentResponse> RecentComments);

    public class ProfileService
    {
        private const int RecentItems = 5;

        private readonly ApplicationDbContext _context;
        private readonly IPostService _posts;
        private readonly CommentService _comments;

        public ProfileService(ApplicationDbContext context, IPostService posts, CommentService comments)
        {
            _context = context;
            _posts = posts;
            _comments = comments;
        }

        // null = użytkownik nie istnieje. Wyszukiwanie po nazwie bez względu na wielkość liter.
        public async Task<PublicProfileResponse?> GetByUsernameAsync(string username, int? viewerUserId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                return null;

            var normalized = IdentityNormalizer.Normalize(username);

            // Jedno zapytanie: bezpieczne pola użytkownika + liczniki (podzapytania), bez materializacji encji User.
            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.NormalizedUsername == normalized)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.CreatedAt,
                    // Tylko treści publiczne — ukryte/usunięte nie są liczone ani pokazywane.
                    PostCount = _context.Posts.Count(p => p.UserId == u.Id && p.Status == ContentStatus.Published),
                    CommentCount = _context.Comments.Count(c => c.UserId == u.Id
                        && c.Status == ContentStatus.Published && c.Post.Status == ContentStatus.Published)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (user is null)
                return null;

            // Aktywność przez kontrakty modułów Posts/Comments (nie ich tabele) — łatwe do wydzielenia w przyszłości.
            var recentPosts = await _posts.GetFeedAsync(new PostFeedQuery { AuthorId = user.Id, Limit = RecentItems }, viewerUserId, cancellationToken);
            var recentComments = await _comments.GetRecentByAuthorAsync(user.Id, RecentItems, cancellationToken);

            return new PublicProfileResponse(
                user.Id,
                user.Username,
                user.CreatedAt,
                user.PostCount,
                user.CommentCount,
                recentPosts.Value?.Items ?? Array.Empty<PostSummaryResponse>(),
                recentComments);
        }
    }
}
