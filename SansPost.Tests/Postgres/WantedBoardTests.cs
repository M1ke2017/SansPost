using Npgsql;
using SansPost.Features;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Features.Reactions;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace SansPost.Tests.Postgres
{
    // Sprint 17 — tablica Wanted (PostService.GetWantedAsync) na prawdziwym PostgreSQL. Tablica jest globalna (nie ma
    // filtra autora), więc każdy test dostaje własną, zmigrowaną bazę — bez wpływu innych testów kolekcji.
    [Collection(PostgresCollection.Name)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Wanted")]
    public class WantedBoardTests
    {
        private readonly PostgresFixture _pg;
        private readonly ITestOutputHelper _output;

        public WantedBoardTests(PostgresFixture pg, ITestOutputHelper output)
        {
            _pg = pg;
            _output = output;
        }

        private sealed class Board
        {
            private readonly PostgresFixture _pg;
            private int _users;

            public Board(PostgresFixture pg, string connectionString)
            {
                _pg = pg;
                ConnectionString = connectionString;
            }

            public string ConnectionString { get; }

            public ApplicationDbContext Context() => _pg.CreateContext(ConnectionString);

            public async Task<int> UserAsync(AccountStatus status = AccountStatus.Active)
            {
                await using var context = Context();
                var alias = TestUsers.RawName("w") + _users++;
                var email = $"{alias}@example.com";
                var user = new User
                {
                    Username = alias,
                    NormalizedUsername = IdentityNormalizer.Normalize(alias),
                    Email = email,
                    NormalizedEmail = IdentityNormalizer.Normalize(email),
                    PasswordHash = "test-data-no-login",
                    Status = status,
                    CreatedAt = DateTime.UtcNow
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();
                return user.Id;
            }

            // Osobni autorzy — tablica pokazuje jednego autora najwyżej raz.
            public Task<List<int>> AuthorsAsync(int count) => FansAsync(count);

            public async Task<List<int>> FansAsync(int count)
            {
                var fans = new List<int>();
                for (var i = 0; i < count; i++)
                    fans.Add(await UserAsync());
                return fans;
            }

            public async Task<int> PostAsync(int authorId, string title, TimeSpan age, string content = "Treść rozmowy",
                ContentStatus status = ContentStatus.Published, PostCategory category = PostCategory.General)
            {
                await using var context = Context();
                var post = new Post
                {
                    UserId = authorId,
                    Title = title,
                    Content = content,
                    Category = category,
                    CreatedAt = DateTime.UtcNow - age,
                    Status = status,
                    DeletedAt = status == ContentStatus.Deleted ? DateTime.UtcNow : null
                };
                context.Posts.Add(post);
                await context.SaveChangesAsync();
                return post.Id;
            }

            public async Task EngageAsync(int postId, IEnumerable<int> likers, int commenterId = 0, int comments = 0,
                ContentStatus commentStatus = ContentStatus.Published)
            {
                await using var context = Context();
                foreach (var userId in likers)
                    context.Likes.Add(new Like { PostId = postId, UserId = userId, CreatedAt = DateTime.UtcNow });
                for (var i = 0; i < comments; i++)
                    context.Comments.Add(new Comment { PostId = postId, UserId = commenterId, Content = $"K{i}", CreatedAt = DateTime.UtcNow, Status = commentStatus });
                await context.SaveChangesAsync();
            }

            public async Task SetStatusAsync(int postId, ContentStatus status)
            {
                await using var context = Context();
                var post = await context.Posts.FindAsync(postId);
                post!.Status = status;
                await context.SaveChangesAsync();
            }

            public async Task<IReadOnlyList<WantedPosterResponse>> WantedAsync(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
            {
                await using var context = _pg.CreateContext(ConnectionString, interceptors);
                return await PostgresFixture.CreatePostService(context).GetWantedAsync();
            }
        }

        private async Task<Board> NewBoardAsync() =>
            new(_pg, await _pg.CreateMigratedDatabaseAsync($"wanted_{Guid.NewGuid():N}"));

        // 1 — kolejność: reakcje + komentarze, niezależnie od wieku w oknie i od kolejności dodania (różni autorzy).
        [DockerFact]
        public async Task Ranking_OrdersByReactionsPlusComments()
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(3);
            var fans = await board.FansAsync(6);
            var quiet = await board.PostAsync(authors[0], "Cicha", TimeSpan.FromHours(1));
            var loud = await board.PostAsync(authors[1], "Głośna", TimeSpan.FromDays(5));
            var middle = await board.PostAsync(authors[2], "Średnia", TimeSpan.FromHours(30));
            await board.EngageAsync(quiet, fans.Take(1));                                 // 1
            await board.EngageAsync(loud, fans.Take(3), fans[5], comments: 3);            // 6
            await board.EngageAsync(middle, fans.Take(2), fans[5], comments: 2);          // 4

            var wanted = await board.WantedAsync();

            Assert.Equal(new[] { loud, middle, quiet }, wanted.Select(p => p.PostId));
            Assert.Equal(new[] { 1, 2, 3 }, wanted.Select(p => p.Rank));
            Assert.True(wanted[0].IsFeatured);
            Assert.All(wanted.Skip(1), p => Assert.False(p.IsFeatured));
            Assert.Equal((3, 3, 6), (wanted[0].ReactionCount, wanted[0].CommentCount, wanted[0].EngagementScore));
        }

        // 2 — remisy: więcej reakcji → więcej komentarzy → nowsza rozmowa → wyższe PostId (każda rozmowa innego autora).
        [DockerFact]
        public async Task Ranking_TieBreaks_ReactionsThenCommentsThenNewerThenPostId()
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(5);
            var fans = await board.FansAsync(4);
            var sameAge = TimeSpan.FromHours(10);
            var moreComments = await board.PostAsync(authors[0], "Więcej komentarzy", TimeSpan.FromHours(2));
            var moreReactions = await board.PostAsync(authors[1], "Więcej reakcji", TimeSpan.FromHours(20));
            var older = await board.PostAsync(authors[2], "Starsza", TimeSpan.FromHours(30));
            var firstId = await board.PostAsync(authors[3], "Ten sam czas A", sameAge);
            var secondId = await board.PostAsync(authors[4], "Ten sam czas B", sameAge);
            await board.EngageAsync(moreReactions, fans.Take(3), fans[3], comments: 1);   // 4 = 3 + 1
            await board.EngageAsync(moreComments, fans.Take(1), fans[3], comments: 3);    // 4 = 1 + 3
            await board.EngageAsync(older, fans.Take(1), fans[3], comments: 1);           // 2 = 1 + 1, 30 h
            await board.EngageAsync(firstId, fans.Take(1), fans[3], comments: 1);         // 2 = 1 + 1, 10 h
            await board.EngageAsync(secondId, fans.Take(1), fans[3], comments: 1);        // 2 = 1 + 1, 10 h, wyższe Id
            await using (var context = board.Context())
            {
                // Identyczny znacznik czasu — rozstrzyga PostId.
                var createdAt = (await context.Posts.FindAsync(firstId))!.CreatedAt;
                (await context.Posts.FindAsync(secondId))!.CreatedAt = createdAt;
                await context.SaveChangesAsync();
            }

            var wanted = await board.WantedAsync();

            Assert.Equal(new[] { moreReactions, moreComments, secondId, firstId, older }, wanted.Select(p => p.PostId));
        }

        // Ten sam porządek wybiera najlepszą rozmowę JEDNEGO autora (remis w obrębie autora → nowsza, potem wyższe Id).
        [DockerFact]
        public async Task BestConversationPerAuthor_UsesSameRankingAndTieBreaks()
        {
            var board = await NewBoardAsync();
            var author = await board.UserAsync();
            var fans = await board.FansAsync(3);
            var older = await board.PostAsync(author, "Starsza, ten sam wynik", TimeSpan.FromHours(30));
            var newer = await board.PostAsync(author, "Nowsza, ten sam wynik", TimeSpan.FromHours(3));
            var moreComments = await board.PostAsync(author, "Więcej komentarzy", TimeSpan.FromHours(2));
            await board.EngageAsync(older, fans.Take(2), fans[2], comments: 1);           // 3 = 2 + 1
            await board.EngageAsync(newer, fans.Take(2), fans[2], comments: 1);           // 3 = 2 + 1, nowsza
            await board.EngageAsync(moreComments, fans.Take(1), fans[2], comments: 2);    // 3 = 1 + 2 — mniej reakcji

            Assert.Equal(new[] { newer }, (await board.WantedAsync()).Select(p => p.PostId));
        }

        // 3 — okno 7 dni: starsza rozmowa z ogromnym zaangażowaniem nie wygrywa z rozmową z tego tygodnia;
        // gdy w oknie brakuje autorów, tablicę uzupełniają najnowsze wcześniejsze rozmowy innych autorów
        // (po jednej na autora, InWindow = false, zawsze niżej).
        [DockerFact]
        public async Task Window_LastSevenDays_FilledWithNewestOlderConversations_Marked()
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(7);
            var fans = await board.FansAsync(12);
            var weekQuiet = await board.PostAsync(authors[0], "Z tego tygodnia", TimeSpan.FromDays(6));
            var oldHit = await board.PostAsync(authors[1], "Stary hit", TimeSpan.FromDays(8));
            var olderStill = await board.PostAsync(authors[2], "Jeszcze starsza", TimeSpan.FromDays(9));
            var archive = new List<int>();
            for (var i = 0; i < 4; i++)
                archive.Add(await board.PostAsync(authors[3 + i], $"Archiwum {i}", TimeSpan.FromDays(20 + i)));
            await board.EngageAsync(oldHit, fans);                                        // 12 reakcji, ale 8 dni temu
            await board.EngageAsync(olderStill, fans.Take(1));

            var wanted = await board.WantedAsync();

            Assert.Equal(PostLimits.WantedSize, wanted.Count);
            Assert.Equal(weekQuiet, wanted[0].PostId);                                   // Most Wanted — z okna
            Assert.True(wanted[0].InWindow);
            // Uzupełnienie: 4 najnowsze spoza okna (oldHit, olderStill, Archiwum 0, Archiwum 1), między sobą wg rankingu.
            Assert.Equal(new[] { oldHit, olderStill, archive[0], archive[1] }, wanted.Skip(1).Select(p => p.PostId));
            Assert.All(wanted.Skip(1), p => Assert.False(p.InWindow));
            Assert.DoesNotContain(archive[2], wanted.Select(p => p.PostId));
            Assert.DoesNotContain(archive[3], wanted.Select(p => p.PostId));
        }

        // Pełne okno (co najmniej pięciu autorów) — bez uzupełnienia, starsze rozmowy nie trafiają na tablicę.
        [DockerFact]
        public async Task Window_Full_NoFill()
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(7);
            var fans = await board.FansAsync(8);
            var old = await board.PostAsync(authors[6], "Stary hit", TimeSpan.FromDays(7.5));
            await board.EngageAsync(old, fans);
            var week = new List<int>();
            for (var i = 0; i < 6; i++)
                week.Add(await board.PostAsync(authors[i], $"Tydzień {i}", TimeSpan.FromDays(i + 0.5)));

            var wanted = await board.WantedAsync();

            Assert.Equal(PostLimits.WantedSize, wanted.Count);
            Assert.All(wanted, p => Assert.True(p.InWindow));
            Assert.DoesNotContain(old, wanted.Select(p => p.PostId));
            Assert.Equal(week.Take(5), wanted.Select(p => p.PostId));                    // remis 0 → nowsza wyżej
        }

        // ---- Sprint 17-FIX: jeden autor = najwyżej jeden plakat ----------------------------------------------------

        // 1 — jeden autor ma pięć najlepszych rozmów tygodnia: na tablicy tylko jego najlepsza, dalej inni autorzy.
        [DockerFact]
        public async Task UniqueAuthors_AuthorWithTopFiveConversations_GetsOnlyBestPoster()
        {
            var board = await NewBoardAsync();
            var star = await board.UserAsync();
            var others = await board.AuthorsAsync(2);
            var fans = await board.FansAsync(10);
            var starPosts = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var id = await board.PostAsync(star, $"Rozmowa gwiazdy {i}", TimeSpan.FromHours(i + 1));
                await board.EngageAsync(id, fans.Take(10 - i));                           // 10, 9, 8, 7, 6
                starPosts.Add(id);
            }
            var second = await board.PostAsync(others[0], "Druga osoba", TimeSpan.FromHours(2));
            var third = await board.PostAsync(others[1], "Trzecia osoba", TimeSpan.FromHours(3));
            await board.EngageAsync(second, fans.Take(2));
            await board.EngageAsync(third, fans.Take(1));

            var wanted = await board.WantedAsync();

            Assert.Equal(new[] { starPosts[0], second, third }, wanted.Select(p => p.PostId));
            Assert.Equal(wanted.Count, wanted.Select(p => p.Alias).Distinct().Count());
        }

        // 2, 3 — pięciu autorów → pięć różnych plakatów; mniej autorów → mniej plakatów, bez duplikatów.
        [DockerTheory]
        [InlineData(5, 5)]
        [InlineData(7, 5)]
        [InlineData(3, 3)]
        public async Task UniqueAuthors_OnePosterPerAuthor_NoDuplicates(int authorCount, int expected)
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(authorCount);
            var fans = await board.FansAsync(3);
            foreach (var (author, index) in authors.Select((a, i) => (a, i)))
                for (var i = 0; i < 3; i++)
                {
                    var id = await board.PostAsync(author, $"Autor {index} rozmowa {i}", TimeSpan.FromHours(1 + index * 3 + i));
                    await board.EngageAsync(id, fans.Take(i));
                }

            var wanted = await board.WantedAsync();

            Assert.Equal(expected, wanted.Count);
            Assert.Equal(expected, wanted.Select(p => p.Alias).Distinct().Count());
            Assert.All(wanted, p => Assert.EndsWith("rozmowa 2", p.Title));              // najlepsza rozmowa każdego autora
            Assert.All(wanted, p => Assert.True(p.InWindow));
        }

        // 4 — uzupełnienie spoza 7 dni zachowuje unikalność: autor z rozmową w oknie nie wraca starszą rozmową,
        // a autor bez okna ma jeden plakat (swoja najnowsza wcześniejsza rozmowa), nawet przy kilku starszych.
        [DockerFact]
        public async Task UniqueAuthors_FallbackOutsideWindow_KeepsOnePosterPerAuthor()
        {
            var board = await NewBoardAsync();
            var active = await board.UserAsync();
            var quiet = await board.UserAsync();
            var archivist = await board.UserAsync();
            var fans = await board.FansAsync(9);
            var activeWeek = await board.PostAsync(active, "Aktywny w tym tygodniu", TimeSpan.FromDays(1));
            var activeOld = await board.PostAsync(active, "Aktywny — stary hit", TimeSpan.FromDays(8));
            await board.EngageAsync(activeOld, fans);
            var quietNewest = await board.PostAsync(quiet, "Cichy — najnowsza starsza", TimeSpan.FromDays(10));
            var quietOlder = await board.PostAsync(quiet, "Cichy — jeszcze starsza", TimeSpan.FromDays(11));
            await board.EngageAsync(quietOlder, fans);
            var archivistPosts = new List<int>();
            for (var i = 0; i < 4; i++)
                archivistPosts.Add(await board.PostAsync(archivist, $"Archiwum {i}", TimeSpan.FromDays(12 + i)));

            var wanted = await board.WantedAsync();

            Assert.Equal(new[] { activeWeek, quietNewest, archivistPosts[0] }, wanted.Select(p => p.PostId));
            Assert.Equal(new[] { true, false, false }, wanted.Select(p => p.InWindow));
            Assert.Equal(3, wanted.Select(p => p.Alias).Distinct().Count());
        }

        // 5 — ukrycie najlepszej rozmowy autora promuje jego kolejną widoczną rozmowę (ten sam plakat, inna rozmowa).
        [DockerFact]
        public async Task UniqueAuthors_HidingBestConversation_PromotesAuthorsNextConversation()
        {
            var board = await NewBoardAsync();
            var author = await board.UserAsync();
            var rival = await board.UserAsync();
            var fans = await board.FansAsync(6);
            var best = await board.PostAsync(author, "Najlepsza", TimeSpan.FromHours(2));
            var next = await board.PostAsync(author, "Kolejna", TimeSpan.FromHours(3));
            var rivalPost = await board.PostAsync(rival, "Rywal", TimeSpan.FromHours(1));
            await board.EngageAsync(best, fans);                                          // 6
            await board.EngageAsync(next, fans.Take(4));                                  // 4
            await board.EngageAsync(rivalPost, fans.Take(5));                             // 5

            Assert.Equal(new[] { best, rivalPost }, (await board.WantedAsync()).Select(p => p.PostId));

            await board.SetStatusAsync(best, ContentStatus.Hidden);

            Assert.Equal(new[] { rivalPost, next }, (await board.WantedAsync()).Select(p => p.PostId));
        }

        // 4 — ukryta rozmowa znika z tablicy (ten sam publiczny obieg co feed), ukryte komentarze się nie liczą.
        [DockerFact]
        public async Task HiddenPost_Disappears_AndHiddenCommentsDoNotCount()
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(2);
            var fans = await board.FansAsync(5);
            var hit = await board.PostAsync(authors[0], "Hit do ukrycia", TimeSpan.FromHours(3));
            var other = await board.PostAsync(authors[1], "Druga", TimeSpan.FromHours(4));
            await board.EngageAsync(hit, fans);
            await board.EngageAsync(other, fans.Take(1), fans[4], comments: 1);
            await board.EngageAsync(other, Array.Empty<int>(), fans[4], comments: 5, commentStatus: ContentStatus.Hidden);

            var before = await board.WantedAsync();
            Assert.Equal(hit, before[0].PostId);
            Assert.Equal(1, before.Single(p => p.PostId == other).CommentCount);

            await board.SetStatusAsync(hit, ContentStatus.Hidden);
            var after = await board.WantedAsync();

            Assert.Equal(new[] { other }, after.Select(p => p.PostId));
            Assert.Equal(1, after[0].Rank);
        }

        // 5 — usunięte rozmowy i konta, których nie promujemy (zawieszone, zbanowane), nie trafiają na tablicę.
        [DockerFact]
        public async Task DeletedPosts_AndSuspendedOrBannedAuthors_AreExcluded()
        {
            var board = await NewBoardAsync();
            var active = await board.UserAsync();
            var suspended = await board.UserAsync(AccountStatus.Suspended);
            var banned = await board.UserAsync(AccountStatus.Banned);
            var fans = await board.FansAsync(6);
            var visible = await board.PostAsync(active, "Widoczna", TimeSpan.FromHours(5));
            var deleted = await board.PostAsync(active, "Usunięta", TimeSpan.FromHours(1), status: ContentStatus.Deleted);
            var bySuspended = await board.PostAsync(suspended, "Od zawieszonego", TimeSpan.FromHours(1));
            var byBanned = await board.PostAsync(banned, "Od zbanowanego", TimeSpan.FromHours(1));
            var oldDeleted = await board.PostAsync(active, "Stara usunięta", TimeSpan.FromDays(10), status: ContentStatus.Deleted);
            var oldHidden = await board.PostAsync(active, "Stara ukryta", TimeSpan.FromDays(11), status: ContentStatus.Hidden);
            foreach (var id in new[] { deleted, bySuspended, byBanned, oldDeleted, oldHidden })
                await board.EngageAsync(id, fans);

            var wanted = await board.WantedAsync();

            Assert.Equal(new[] { visible }, wanted.Select(p => p.PostId));
        }

        // 6 — DTO: tylko dane prezentacyjne; przydomek zamiast identyfikatora autora, bez emaila i danych konta.
        [DockerFact]
        public async Task Dto_ContainsOnlyPresentationData()
        {
            var allowed = new[]
            {
                "PostId", "Rank", "Alias", "Title", "Category", "CreatedAt", "ReactionCount", "CommentCount", "Preview", "InWindow",
                "IsFeatured", "EngagementScore"
            };
            Assert.Equal(allowed.OrderBy(n => n), typeof(WantedPosterResponse).GetProperties().Select(p => p.Name).OrderBy(n => n));

            var board = await NewBoardAsync();
            var author = await board.UserAsync();
            var longContent = string.Concat(Enumerable.Repeat("Długa treść rozmowy o saloonie. ", 20));
            await board.PostAsync(author, "Rozmowa z długą treścią", TimeSpan.FromHours(1), longContent, category: PostCategory.Travel);

            var poster = Assert.Single(await board.WantedAsync());
            await using var context = board.Context();
            var user = await context.Users.FindAsync(author);
            Assert.Equal(user!.Username, poster.Alias);
            Assert.Equal(PostCategory.Travel, poster.Category);
            Assert.EndsWith("…", poster.Preview);
            Assert.True(poster.Preview.Length <= PostLimits.WantedPreviewLength + 1);
            Assert.StartsWith(poster.Preview.TrimEnd('…'), longContent);

            var json = System.Text.Json.JsonSerializer.Serialize(poster);
            Assert.DoesNotContain(user.Email, json);
            Assert.DoesNotContain("Email", json);
            Assert.DoesNotContain("AuthorId", json);
            Assert.DoesNotContain("UserId", json);
            Assert.DoesNotContain(user.PasswordHash, json);
        }

        // 7 — brak rozmów: pusta tablica (UI: "Tablica czeka na pierwszą rozmowę.").
        [DockerFact]
        public async Task Empty_ReturnsNoPosters()
        {
            var board = await NewBoardAsync();
            await board.UserAsync();

            Assert.Empty(await board.WantedAsync());
        }

        // 8 — jedna rozmowa: jeden, główny plakat. Dwie–cztery: tyle plakatów, ile rozmów (bez sztucznych wpisów).
        [DockerTheory]
        [InlineData(1)]
        [InlineData(3)]
        public async Task FewConversations_AsManyPostersAsExist(int count)
        {
            var board = await NewBoardAsync();
            var authors = await board.AuthorsAsync(count);
            for (var i = 0; i < count; i++)
                await board.PostAsync(authors[i], $"Rozmowa {i}", TimeSpan.FromHours(i + 1));

            var wanted = await board.WantedAsync();

            Assert.Equal(count, wanted.Count);
            Assert.True(wanted[0].IsFeatured);
            Assert.Equal(Enumerable.Range(1, count), wanted.Select(p => p.Rank));
        }

        // SQL: jedno zapytanie (bez N+1: przydomek i liczniki w tym samym SELECT), LIMIT w obu gałęziach, najlepsza
        // rozmowa autora wybierana w PostgreSQL (NOT EXISTS lepszej rozmowy autora, bez pobierania okna do
        // pamięci), okno kandydatów jako zakres na IX_posts_feed (naturalny plan na zbiorze 20 000 rozmów; 40 aktywnych
        // autorów, więc każdy ma w oknie ~8 rozmów — wybór najlepszej rozmowy autora naprawdę porównuje rozmowy).
        [DockerFact]
        public async Task Query_IsSingleSelect_WithLimits_AndUsesFeedIndex()
        {
            var board = await NewBoardAsync();
            await using (var connection = new NpgsqlConnection(board.ConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO users (username, normalizedusername, email, normalizedemail, passwordhash, role, status, createdat)
                    SELECT 'user' || g, 'USER' || g, 'user' || g || '@example.com', 'USER' || g || '@EXAMPLE.COM', 'x', 0,
                           CASE WHEN g % 10 = 0 THEN 'Suspended' ELSE 'Active' END, now()
                    FROM generate_series(1, 2000) g;
                    INSERT INTO posts (userid, title, content, category, createdat, version)
                    SELECT 1 + (g % 40), 'Rozmowa ' || g, repeat('treść ', 60) || g, 'General', now() - g * interval '30 minutes', 1
                    FROM generate_series(1, 20000) g;
                    INSERT INTO likes (postid, userid, createdat) SELECT p.id, 1 + (p.id * 7 % 2000), now() FROM posts p WHERE p.id % 3 = 0;
                    INSERT INTO comments (postid, userid, content, createdat, version) SELECT p.id, 1 + (p.id % 2000), 'k', now(), 1 FROM posts p WHERE p.id % 5 = 0;
                    UPDATE posts SET status = 'Hidden' WHERE id % 20 = 1;
                    ANALYZE;
                    """, connection);
                await seed.ExecuteNonQueryAsync();
            }

            var capture = new CommandCapture();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var wanted = await board.WantedAsync(capture);
            watch.Stop();

            Assert.Equal(PostLimits.WantedSize, wanted.Count);
            Assert.Equal(PostLimits.WantedSize, wanted.Select(p => p.Alias).Distinct().Count());
            var (sql, parameters) = Assert.Single(capture.Commands);
            Assert.Contains("UNION ALL", sql);
            Assert.Contains("NOT EXISTS", sql);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "LIMIT").Count);
            Assert.DoesNotContain("email", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passwordhash", sql, StringComparison.OrdinalIgnoreCase);

            await using var explainConnection = new NpgsqlConnection(board.ConnectionString);
            await explainConnection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, explainConnection);
            explain.Parameters.AddRange(parameters.Select(p => p.Clone()).ToArray());
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    plan.Add(reader.GetString(0));

            _output.WriteLine($"GetWantedAsync (pierwsze wywołanie, z kompilacją zapytania EF): {watch.ElapsedMilliseconds} ms");
            _output.WriteLine(sql);
            _output.WriteLine(string.Join(Environment.NewLine, plan));
            Assert.Contains(plan, line => line.Contains("IX_posts_feed", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("Seq Scan on posts", StringComparison.Ordinal));
        }
    }
}
