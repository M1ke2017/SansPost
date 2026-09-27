using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Posts
{
    // Granica modułu Posts. Konsumenci (Controllers, Blazor, Profiles) znają tylko ten kontrakt i DTO — nie encje ani DbContext.
    // viewerUserId = zaufana tożsamość odbiorcy (null = anonim), używana tylko do LikedByCurrentUser.
    public interface IPostService
    {
        Task<ServiceResult<KeysetPage<PostSummaryResponse>>> GetFeedAsync(PostFeedQuery query, int? viewerUserId, CancellationToken cancellationToken = default);

        Task<PostDetailsResponse?> GetByIdAsync(int postId, int? viewerUserId, CancellationToken cancellationToken = default);

        Task<PostQuotaResponse?> GetQuotaAsync(int userId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CategorySummaryResponse>> GetCategoriesAsync(CancellationToken cancellationToken = default);

        // Tablica Wanted: do PostLimits.WantedSize najbardziej angażujących rozmów (ranking — patrz implementacja).
        Task<IReadOnlyList<WantedPosterResponse>> GetWantedAsync(CancellationToken cancellationToken = default);

        Task<ServiceResult<PostDetailsResponse>> CreateAsync(int actorUserId, PostRequest request, CancellationToken cancellationToken = default);

        // expectedVersion = wersja, którą klient edytował (ETag / If-Match).
        Task<ServiceResult<PostDetailsResponse>> UpdateAsync(int actorUserId, int postId, int expectedVersion, PostRequest request, CancellationToken cancellationToken = default);

        // Usunięcie również wymaga wersji — nie da się usunąć posta, którego aktualnej treści klient nie widział.
        Task<ServiceResult> DeleteAsync(int actorUserId, int postId, int expectedVersion, CancellationToken cancellationToken = default);
    }

    public class PostService : IPostService
    {
        private const string StaleVersionMessage = "Post został w międzyczasie zmieniony. Odśwież go i spróbuj ponownie.";

        private readonly ApplicationDbContext _context;
        private readonly TimeProvider _time;
        private readonly WriteGuard _writeGuard;

        public PostService(ApplicationDbContext context, TimeProvider time, WriteGuard writeGuard)
        {
            _context = context;
            _time = time;
            _writeGuard = writeGuard;
        }

        // Jedyne źródło publicznych odczytów postów — Hidden/Deleted nigdy tu nie trafiają.
        // Stała (nie parametr) w SQL: 'Published' pasuje do predykatu częściowych indeksów feedu.
        private static readonly Expression<Func<Post, bool>> IsPublished = p => p.Status == ContentStatus.Published;

        private IQueryable<Post> PublishedPosts => _context.Posts.AsNoTracking().Where(IsPublished);

        // Autor, którego tablica Wanted może promować (konto aktywne) — jedna reguła dla plakatów i bramki uzupełnienia.
        private static readonly Expression<Func<Post, bool>> HasPromotableAuthor = p => p.User.Status == AccountStatus.Active;

        // Posty wliczane do limitu: opublikowane i ukryte przez moderację (ukrycie nie zwalnia slotu),
        // bez usuniętych przez autora.
        private IQueryable<Post> QuotaPosts(int userId) =>
            _context.Posts.Where(p => p.UserId == userId && p.Status != ContentStatus.Deleted);

        // Read model: liczniki jako skorelowane podzapytania w JEDNYM SELECT (brak N+1, brak materializacji Likes/Comments).
        // Zależność od tabel Comments/Likes dotyczy wyłącznie odczytu — Posts nie wywołuje ich logiki.
        private Expression<Func<Post, PostSummaryResponse>> ToSummary(int? viewerUserId) => p => new PostSummaryResponse(
            p.Id,
            p.Title,
            p.Content.Length > PostLimits.PreviewLength ? p.Content.Substring(0, PostLimits.PreviewLength) : p.Content,
            p.Content.Length > PostLimits.PreviewLength,
            p.Category,
            p.CreatedAt,
            p.UpdatedAt,
            p.UserId,
            p.User.Username,
            _context.Likes.Count(l => l.PostId == p.Id),
            _context.Comments.Count(c => c.PostId == p.Id && c.Status == ContentStatus.Published),
            viewerUserId != null && _context.Likes.Any(l => l.PostId == p.Id && l.UserId == viewerUserId));

        private Expression<Func<Post, PostDetailsResponse>> ToDetails(int? viewerUserId) => p => new PostDetailsResponse(
            p.Id,
            p.Title,
            p.Content,
            p.Category,
            p.ImageUrl,
            p.CreatedAt,
            p.UpdatedAt,
            p.UserId,
            p.User.Username,
            p.Version,
            _context.Likes.Count(l => l.PostId == p.Id),
            _context.Comments.Count(c => c.PostId == p.Id && c.Status == ContentStatus.Published),
            viewerUserId != null && _context.Likes.Any(l => l.PostId == p.Id && l.UserId == viewerUserId));

        public async Task<ServiceResult<KeysetPage<PostSummaryResponse>>> GetFeedAsync(PostFeedQuery query, int? viewerUserId, CancellationToken cancellationToken = default)
        {
            if (query.Limit is < 1 or > PostLimits.MaxPageSize)
                return ServiceResult<KeysetPage<PostSummaryResponse>>.Fail(ServiceError.Validation, $"Limit musi mieścić się w zakresie 1–{PostLimits.MaxPageSize}.");
            if (!Enum.IsDefined(query.Sort) || (query.Category is { } c && !Enum.IsDefined(c)))
                return ServiceResult<KeysetPage<PostSummaryResponse>>.Fail(ServiceError.Validation, "Nieprawidłowe sortowanie lub kategoria.");

            var cursorScope = $"posts.{query.Sort}";
            KeysetCursor? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !KeysetCursor.TryDecode(query.Cursor, cursorScope, out cursor))
                return ServiceResult<KeysetPage<PostSummaryResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            var posts = PublishedPosts;

            if (query.Category is { } category)
                posts = posts.Where(p => p.Category == category);
            if (query.AuthorId is { } authorId)
                posts = posts.Where(p => p.UserId == authorId);

            // Ten sam feed (i te same filtry) dla wszystkich trybów — różni się tylko klucz sortowania.
            if (query.Sort == PostSort.Popular)
                return await GetPopularPageAsync(posts, query, cursorScope, cursor, viewerUserId, cancellationToken);

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
            var page = await posts.Take(query.Limit + 1).Select(ToSummary(viewerUserId)).ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore ? new KeysetCursor(cursorScope, page[^1].CreatedAt, page[^1].Id).Encode() : null;
            return ServiceResult<KeysetPage<PostSummaryResponse>>.Success(new KeysetPage<PostSummaryResponse>(page, nextCursor, hasMore));
        }

        // Popular: ranking w SQL, klucz (score DESC, createdat DESC, id DESC), score jako bigint.
        private async Task<ServiceResult<KeysetPage<PostSummaryResponse>>> GetPopularPageAsync(
            IQueryable<Post> posts, PostFeedQuery query, string cursorScope, KeysetCursor? cursor, int? viewerUserId, CancellationToken cancellationToken)
        {
            if (cursor is not null && (cursor.Rank is null || cursor.AsOf is null))
                return ServiceResult<KeysetPage<PostSummaryResponse>>.Fail(ServiceError.Validation, "Nieprawidłowy kursor.");

            // Moment odniesienia stały dla wszystkich stron — inaczej wynik zależny od czasu przesuwałby się między requestami.
            var asOf = cursor?.AsOf ?? _time.GetUtcNow().UtcDateTime;
            var windowStart = asOf.AddDays(-PostLimits.PopularWindowDays);

            var scored = posts
                .Where(p => p.CreatedAt <= asOf && p.CreatedAt > windowStart)
                .Select(p => new Scored<Post>
                {
                    Entity = p,
                    Score = (long)Math.Round(
                        (PostLimits.PopularHoursPerEngagementUnit
                            * Math.Log(1
                                + _context.Likes.Count(l => l.PostId == p.Id)
                                + PostLimits.PopularCommentWeight * _context.Comments.Count(c => c.PostId == p.Id && c.Status == ContentStatus.Published))
                         - (asOf - p.CreatedAt).TotalHours)
                        * PostLimits.ScoreScale)
                });

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
                .Select(ScoredProjection.Of(ToSummary(viewerUserId)))
                .ToListAsync(cancellationToken);

            var hasMore = page.Count > query.Limit;
            if (hasMore)
                page.RemoveAt(page.Count - 1);

            var nextCursor = hasMore
                ? new KeysetCursor(cursorScope, page[^1].Item.CreatedAt, page[^1].Item.Id, page[^1].Score, asOf).Encode()
                : null;

            return ServiceResult<KeysetPage<PostSummaryResponse>>.Success(
                new KeysetPage<PostSummaryResponse>(page.Select(r => r.Item).ToList(), nextCursor, hasMore));
        }

        // Tablica Wanted — prosty, wyjaśnialny ranking na istniejących danych (bez nowej tabeli i bez wag):
        //   engagementScore = reakcje + opublikowane komentarze (te same liczniki co feed),
        //   remis: więcej reakcji → więcej komentarzy → nowsza rozmowa → wyższe PostId.
        // Tablica pokazuje LUDZI przez ich najlepszą rozmowę: jeden autor = co najwyżej jeden plakat.
        //   Okno: rozmowy z ostatnich WantedWindowDays dni; dla każdego autora jego najwyżej sklasyfikowana rozmowa
        //   (w SQL: NOT EXISTS lepszej rozmowy tego samego autora w oknie), potem WantedSize najlepszych autorów tym
        //   samym rankingiem.
        //   Uzupełnienie (gdy w oknie jest mniej autorów niż WantedSize): autorzy BEZ rozmowy w oknie, każdy ze swoją
        //   najnowszą wcześniejszą rozmową, najświeżsi pierwsi (InWindow = false) — zawsze pod autorami z okna,
        //   między sobą tym samym rankingiem. Autor z oknem nigdy nie pojawia się drugi raz w uzupełnieniu.
        // Widoczność: ten sam publiczny obieg co feed (PublishedPosts — ukryte i usunięte nie istnieją) i tylko konta
        // aktywne: tablica promuje autora, więc konto zawieszone albo zbanowane nie trafia na plakat (jego rozmowy
        // zostają tam, gdzie pokazuje je feed). Ukrycie najlepszej rozmowy autora promuje jego kolejną widoczną.
        // Jedno zapytanie SQL: dwie gałęzie z LIMIT (UNION ALL), ranking i unikalność autorów w PostgreSQL.
        public async Task<IReadOnlyList<WantedPosterResponse>> GetWantedAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var windowStart = now.AddDays(-PostLimits.WantedWindowDays);
            var candidates = PublishedPosts.Where(HasPromotableAuthor).Where(p => p.CreatedAt <= now);

            // Rozmowy z okna z licznikami (skorelowane podzapytania po UX_likes_post_user / IX_comments_post_thread).
            var scored = candidates
                .Where(p => p.CreatedAt > windowStart)
                .Select(p => new WantedCandidate
                {
                    Post = p,
                    Reactions = _context.Likes.Count(l => l.PostId == p.Id),
                    Comments = _context.Comments.Count(c => c.PostId == p.Id && c.Status == ContentStatus.Published)
                });

            // Najlepsza rozmowa autora = nie istnieje rozmowa tego samego autora w oknie, która stoi wyżej w rankingu
            // (wynik → reakcje → komentarze → nowsza → wyższe Id; to ten sam porządek co między autorami).
            var week = scored
                .Where(x => !scored.Any(y => y.Post.UserId == x.Post.UserId && y.Post.Id != x.Post.Id
                    && (y.Reactions + y.Comments > x.Reactions + x.Comments
                        || (y.Reactions + y.Comments == x.Reactions + x.Comments
                            && (y.Reactions > x.Reactions
                                || (y.Reactions == x.Reactions
                                    && (y.Comments > x.Comments
                                        || (y.Comments == x.Comments
                                            && (y.Post.CreatedAt > x.Post.CreatedAt
                                                || (y.Post.CreatedAt == x.Post.CreatedAt && y.Post.Id > x.Post.Id))))))))))
                .OrderByDescending(x => x.Reactions + x.Comments)
                .ThenByDescending(x => x.Reactions)
                .ThenByDescending(x => x.Comments)
                .ThenByDescending(x => x.Post.CreatedAt)
                .ThenByDescending(x => x.Post.Id)
                .Select(x => x.Post)
                .Select(ToWantedRow)
                .Take(PostLimits.WantedSize);

            // Najnowsza wcześniejsza rozmowa autora bez rozmowy w oknie: brak nowszej (albo tak samo starej o wyższym Id)
            // rozmowy tego autora — anty-złączenie po IX_posts_author_feed, skan IX_posts_feed kończy się po LIMIT.
            // Gałąź działa tylko wtedy, gdy w oknie jest mniej autorów niż plakatów: warunek bez odwołania do wiersza
            // PostgreSQL liczy raz (One-Time Filter), więc przy pełnym oknie archiwum nie jest w ogóle skanowane.
            // Podzapytanie zaczyna się od DbSet w samym wyrażeniu (te same reguły: IsPublished, HasPromotableAuthor) —
            // zmienną IQueryable z .Count() EF wykonałby osobno, po stronie klienta, zamiast wysłać ją w tym samym SQL.
            var older = candidates
                .Where(p => _context.Posts.Where(IsPublished).Where(HasPromotableAuthor)
                        .Where(w => w.CreatedAt > windowStart && w.CreatedAt <= now)
                        .Select(w => w.UserId).Distinct().Count() < PostLimits.WantedSize
                    && p.CreatedAt <= windowStart
                    && !candidates.Any(n => n.UserId == p.UserId
                        && (n.CreatedAt > p.CreatedAt || (n.CreatedAt == p.CreatedAt && n.Id > p.Id))))
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id)
                .Select(ToWantedRow)
                .Take(PostLimits.WantedSize);

            var rows = await week.Concat(older).ToListAsync(cancellationToken);

            // Kolejność końcowa (najwyżej 2 × WantedSize wierszy, już wybranych w SQL): autorzy z okna, potem uzupełnienie.
            var inWindow = rows.Where(r => r.CreatedAt > windowStart).ToList();
            var fill = rows.Where(r => r.CreatedAt <= windowStart)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.PostId)
                .Take(PostLimits.WantedSize - inWindow.Count);

            return Ranked(inWindow).Concat(Ranked(fill))
                .Select((r, index) => new WantedPosterResponse(r.PostId, index + 1, r.Alias, r.Title, r.Category, r.CreatedAt,
                    r.Reactions, r.Comments, r.Preview, r.CreatedAt > windowStart))
                .ToList();

            static IEnumerable<WantedRow> Ranked(IEnumerable<WantedRow> rows) => rows
                .OrderByDescending(r => r.Reactions + r.Comments)
                .ThenByDescending(r => r.Reactions)
                .ThenByDescending(r => r.Comments)
                .ThenByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.PostId);
        }

        // Rozmowa z okna z licznikami — do wyboru najlepszej rozmowy autora i rankingu autorów w SQL.
        private sealed class WantedCandidate
        {
            public Post Post { get; init; } = null!;
            public int Reactions { get; init; }
            public int Comments { get; init; }
        }

        // Wiersz plakatu: liczniki jako skorelowane podzapytania (jak ToSummary), fragment liczony w SQL.
        private sealed class WantedRow
        {
            public int PostId { get; init; }
            public string Alias { get; init; } = "";
            public string Title { get; init; } = "";
            public PostCategory Category { get; init; }
            public DateTime CreatedAt { get; init; }
            public int Reactions { get; init; }
            public int Comments { get; init; }
            public string Preview { get; init; } = "";
        }

        private Expression<Func<Post, WantedRow>> ToWantedRow => p => new WantedRow
        {
            PostId = p.Id,
            Alias = p.User.Username,
            Title = p.Title,
            Category = p.Category,
            CreatedAt = p.CreatedAt,
            Reactions = _context.Likes.Count(l => l.PostId == p.Id),
            Comments = _context.Comments.Count(c => c.PostId == p.Id && c.Status == ContentStatus.Published),
            Preview = p.Content.Length > PostLimits.WantedPreviewLength
                ? p.Content.Substring(0, PostLimits.WantedPreviewLength).TrimEnd() + "…"
                : p.Content
        };

        public async Task<IReadOnlyList<CategorySummaryResponse>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        {
            // Jeden GROUP BY w SQL; kategorie bez postów uzupełniane z enuma (zamknięty zestaw, bez tabeli Categories).
            var stats = await PublishedPosts
                .GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Count = g.Count(), Latest = g.Max(p => p.CreatedAt) })
                .ToListAsync(cancellationToken);

            return Enum.GetValues<PostCategory>()
                .Select(category =>
                {
                    var stat = stats.FirstOrDefault(s => s.Category == category);
                    return new CategorySummaryResponse(category, stat?.Count ?? 0, stat?.Latest);
                })
                .ToList();
        }

        public async Task<PostDetailsResponse?> GetByIdAsync(int postId, int? viewerUserId, CancellationToken cancellationToken = default)
        {
            // Hidden/Deleted zachowują się jak brak zasobu (404) — nie zdradzamy, że istnieją.
            return await PublishedPosts
                .Where(p => p.Id == postId)
                .Select(ToDetails(viewerUserId))
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<PostQuotaResponse?> GetQuotaAsync(int userId, CancellationToken cancellationToken = default)
        {
            if (!await _context.Users.AnyAsync(u => u.Id == userId, cancellationToken))
                return null;

            var limit = await GetPostLimitAsync(userId, cancellationToken);
            var used = await QuotaPosts(userId).CountAsync(cancellationToken);

            return new PostQuotaResponse(limit, used, Math.Max(0, limit - used));
        }

        public async Task<ServiceResult<PostDetailsResponse>> CreateAsync(int actorUserId, PostRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (Validate(normalized) is { } error)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Validation, error);
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<PostDetailsResponse>.From(denied);

            // Jawna transakcja: COUNT → INSERT musi być atomowe względem innych requestów tego samego użytkownika.
            // Blokada wiersza users serializuje je w PostgreSQL (także między instancjami aplikacji).
            // Wyjście bez Commit (limit, wyjątek, anulowanie) = rollback przy Dispose.
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            if (!await _context.LockUserRowAsync(actorUserId, cancellationToken))
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.NotFound, "Użytkownik nie istnieje.");

            var limit = await GetPostLimitAsync(actorUserId, cancellationToken);
            var used = await QuotaPosts(actorUserId).CountAsync(cancellationToken);
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

            return ServiceResult<PostDetailsResponse>.Success((await GetByIdAsync(post.Id, actorUserId, cancellationToken))!);
        }

        // Bez jawnej transakcji: jeden UPDATE z warunkiem na Version (SaveChanges jest atomowe).
        public async Task<ServiceResult<PostDetailsResponse>> UpdateAsync(int actorUserId, int postId, int expectedVersion, PostRequest request, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(request);
            if (Validate(normalized) is { } error)
                return ServiceResult<PostDetailsResponse>.Fail(ServiceError.Validation, error);
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return ServiceResult<PostDetailsResponse>.From(denied);

            var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == postId && p.Status == ContentStatus.Published, cancellationToken);
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
                    var failure = await ResolveConcurrencyFailureAsync(postId, cancellationToken);
                    return ServiceResult<PostDetailsResponse>.Fail(failure.Error, failure.Message!);
                }
            }

            var updated = await GetByIdAsync(postId, actorUserId, cancellationToken);
            return updated is null
                ? ServiceResult<PostDetailsResponse>.Fail(ServiceError.NotFound, "Post nie istnieje.")
                : ServiceResult<PostDetailsResponse>.Success(updated);
        }

        // Soft delete: Status = Deleted (historia zachowana dla moderacji). Nadal jeden UPDATE ... WHERE version.
        public async Task<ServiceResult> DeleteAsync(int actorUserId, int postId, int expectedVersion, CancellationToken cancellationToken = default)
        {
            if (await _writeGuard.CheckAsync(actorUserId, cancellationToken) is { } denied)
                return denied;

            var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == postId && p.Status == ContentStatus.Published, cancellationToken);
            if (post is null)
                return ServiceResult.Fail(ServiceError.NotFound, "Post nie istnieje.");
            if (post.UserId != actorUserId)
                return ServiceResult.Fail(ServiceError.Forbidden, "Nie masz uprawnień do usunięcia tego posta.");
            if (post.Version != expectedVersion)
                return ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage);

            post.Status = ContentStatus.Deleted;
            post.DeletedAt = _time.GetUtcNow().UtcDateTime;
            post.Version++;
            try
            {
                // UPDATE posts SET status = 'Deleted' ... WHERE id = @id AND version = @expectedVersion.
                // Komentarze i polubienia zostają w bazie, ale znikają publicznie razem z postem.
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return await ResolveConcurrencyFailureAsync(postId, cancellationToken);
            }

            return ServiceResult.Success();
        }

        private async Task<ServiceResult> ResolveConcurrencyFailureAsync(int postId, CancellationToken cancellationToken)
        {
            _context.ChangeTracker.Clear();

            return await _context.Posts.AnyAsync(p => p.Id == postId && p.Status == ContentStatus.Published, cancellationToken)
                ? ServiceResult.Fail(ServiceError.PreconditionFailed, StaleVersionMessage)
                : ServiceResult.Fail(ServiceError.NotFound, "Post został usunięty.");
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
            Category = request.Category
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
                || post.Category != category;

            post.Title = request.Title;
            post.Content = request.Content;
            post.Category = category;

            return changed;
        }
    }
}
