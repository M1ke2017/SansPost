using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Search
{
    // Granica modułu Search. Dziś: PostgreSQL full-text search in-process.
    // Konsumenci znają tylko ten kontrakt — implementację można wymienić bez zmian w Controllers/Blazor.
    public interface ISearchService
    {
        Task<ServiceResult<KeysetPage<PostSearchResult>>> SearchPostsAsync(PostSearchQuery query, int? viewerUserId, CancellationToken cancellationToken = default);

        Task<ServiceResult<IReadOnlyList<UserSearchResult>>> SearchUsersAsync(string? prefix, int limit, CancellationToken cancellationToken = default);
    }

    public class PostgresSearchService : ISearchService
    {
        // ts_rank (real) × skala → bigint: deterministyczny, dokładny klucz sortowania i kursora (float nie jest separatorem).
        private const double RankScale = 1_000_000.0;

        private readonly ApplicationDbContext _context;

        public PostgresSearchService(ApplicationDbContext context)
        {
            _context = context;
        }

        private Expression<Func<Post, PostSearchResult>> ToResult(int? viewerUserId) => p => new PostSearchResult(
            p.Id,
            p.Title,
            p.Content.Length > PostLimits.PreviewLength ? p.Content.Substring(0, PostLimits.PreviewLength) : p.Content,
            p.Content.Length > PostLimits.PreviewLength,
            p.Category,
            p.CreatedAt,
            p.UserId,
            p.User.Username,
            _context.Likes.Count(l => l.PostId == p.Id),
            _context.Comments.Count(c => c.PostId == p.Id),
            viewerUserId != null && _context.Likes.Any(l => l.PostId == p.Id && l.UserId == viewerUserId));

        public async Task<ServiceResult<KeysetPage<PostSearchResult>>> SearchPostsAsync(PostSearchQuery query, int? viewerUserId, CancellationToken cancellationToken = default)
        {
            var text = query.Q?.Trim() ?? string.Empty;
            if (text.Length < SearchLimits.QueryMinLength || text.Length > SearchLimits.QueryMaxLength)
                return Fail<KeysetPage<PostSearchResult>>($"Zapytanie musi mieć od {SearchLimits.QueryMinLength} do {SearchLimits.QueryMaxLength} znaków.");
            if (query.Limit is < 1 or > SearchLimits.MaxPageSize)
                return Fail<KeysetPage<PostSearchResult>>($"Limit musi mieścić się w zakresie 1–{SearchLimits.MaxPageSize}.");
            if (query.Category is { } c && !Enum.IsDefined(c))
                return Fail<KeysetPage<PostSearchResult>>("Nieprawidłowa kategoria.");

            // Kursor powiązany z konkretnym zapytaniem i filtrem — kursor z innego wyszukiwania jest odrzucany.
            var cursorScope = $"search.posts.{Fingerprint(text, query.Category)}";
            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor)
                && (!KeysetCursor.TryDecode(query.Cursor, cursorScope, out cursor) || cursor!.Rank is null))
            {
                return Fail<KeysetPage<PostSearchResult>>("Nieprawidłowy kursor.");
            }

            // websearch_to_tsquery: bezpieczna dla dowolnego inputu (cudzysłowy, "or", "-") — nigdy błąd składni,
            // tekst trafia jako parametr, nigdy do SQL. Konfiguracja 'simple' — patrz TextSearch.Configuration.
            var matches = _context.Posts
                .AsNoTracking()
                .Where(p => EF.Property<NpgsqlTsVector>(p, TextSearch.PostSearchVector)
                    .Matches(EF.Functions.WebSearchToTsQuery(TextSearch.Configuration, text)));

            if (query.Category is { } category)
                matches = matches.Where(p => p.Category == category);

            var scored = matches.Select(p => new Scored<Post>
            {
                Entity = p,
                Score = (long)Math.Round(
                    EF.Property<NpgsqlTsVector>(p, TextSearch.PostSearchVector)
                        .Rank(EF.Functions.WebSearchToTsQuery(TextSearch.Configuration, text), NpgsqlTsRankingNormalization.DivideBy1PlusLogLength)
                    * RankScale)
            });

            // Ranking: trafność DESC, CreatedAt DESC, Id DESC — pełny, deterministyczny klucz keyset.
            if (cursor is not null)
            {
                var rank = cursor.Rank!.Value;
                scored = scored.Where(r => r.Score < rank
                    || (r.Score == rank && (r.Entity.CreatedAt < cursor.CreatedAt
                        || (r.Entity.CreatedAt == cursor.CreatedAt && r.Entity.Id < cursor.Id))));
            }

            var page = await scored
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Entity.CreatedAt)
                .ThenByDescending(r => r.Entity.Id)
                .Take(query.Limit + 1)
                .Select(ScoredProjection.Of(ToResult(viewerUserId)))
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore
                ? new KeysetCursor(cursorScope, page[^1].Item.CreatedAt, page[^1].Item.Id, page[^1].Score).Encode()
                : null;

            return ServiceResult<KeysetPage<PostSearchResult>>.Success(
                new KeysetPage<PostSearchResult>(page.Select(r => r.Item).ToList(), nextCursor, hasMore));
        }

        public async Task<ServiceResult<IReadOnlyList<UserSearchResult>>> SearchUsersAsync(string? prefix, int limit, CancellationToken cancellationToken = default)
        {
            var text = prefix?.Trim() ?? string.Empty;
            if (text.Length < SearchLimits.UserPrefixMinLength || text.Length > SearchLimits.UserPrefixMaxLength)
                return Fail<IReadOnlyList<UserSearchResult>>($"Prefiks musi mieć od {SearchLimits.UserPrefixMinLength} do {SearchLimits.UserPrefixMaxLength} znaków.");
            if (limit is < 1 or > SearchLimits.MaxUserResults)
                return Fail<IReadOnlyList<UserSearchResult>>($"Limit musi mieścić się w zakresie 1–{SearchLimits.MaxUserResults}.");

            // Wyłącznie publiczna nazwa (NormalizedUsername) — nigdy email ani pola auth.
            // StartsWith → LIKE 'PREFIX%' (EF escapuje % i _) na indeksie text_pattern_ops.
            var normalized = IdentityNormalizer.Normalize(text);
            var users = await _context.Users
                .AsNoTracking()
                .Where(u => u.NormalizedUsername.StartsWith(normalized))
                .OrderBy(u => u.NormalizedUsername)
                .Take(limit)
                .Select(u => new UserSearchResult(u.Id, u.Username, u.CreatedAt, _context.Posts.Count(p => p.UserId == u.Id)))
                .ToListAsync(cancellationToken);

            return ServiceResult<IReadOnlyList<UserSearchResult>>.Success(users);
        }

        private static ServiceResult<T> Fail<T>(string message) => ServiceResult<T>.Fail(ServiceError.Validation, message);

        private static string Fingerprint(string text, PostCategory? category)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{text.ToUpperInvariant()}|{category}"));
            return Convert.ToHexString(hash, 0, 6);
        }
    }
}
