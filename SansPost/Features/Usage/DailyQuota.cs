using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Usage
{
    // Dzienne limity biznesowe (v1.0): posty 10, komentarze 20, rozpoczęte gry 15 na użytkownika na dobę UTC.
    // To nie są limity HTTP (te zostają w RateLimiting / WriteGuard) — liczy się udana operacja, nie request.
    // Źródłem prawdy jest PostgreSQL (tabela dailyuserusages, jeden wiersz na użytkownika i dzień UTC): licznik przeżywa
    // restart, działa przy wielu instancjach, reset to po prostu nowa data (bez zadań w tle).
    public enum QuotaKind
    {
        Post,
        Comment,
        Game
    }

    // Zużycie limitów jednego użytkownika w jednym dniu UTC. Usunięcie posta/komentarza niczego nie zwraca — licznik
    // mówi, ile udało się utworzyć, nie ile istnieje.
    public class DailyUserUsage
    {
        public int UserId { get; set; }
        public User User { get; set; } = null!;
        public DateOnly DateUtc { get; set; }
        public int PostsCreated { get; set; }
        public int CommentsCreated { get; set; }
        public int GamesStarted { get; set; }
    }

    public static class DailyQuota
    {
        public const int Posts = 10;
        public const int Comments = 20;
        public const int Games = 15;

        public const string PostLimitCode = "daily-post-limit-reached";
        public const string CommentLimitCode = "daily-comment-limit-reached";
        public const string GameLimitCode = "daily-game-limit-reached";
        public const string OpponentGameLimitCode = "duel-player-daily-limit-reached";

        public static int LimitOf(QuotaKind kind) => kind switch
        {
            QuotaKind.Post => Posts,
            QuotaKind.Comment => Comments,
            _ => Games
        };

        public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

        // Do północy UTC (Retry-After odpowiedzi 429).
        public static TimeSpan UntilReset(DateTimeOffset now) =>
            Today(now).AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - now.UtcDateTime;

        // Nazwy kolumn z zamkniętego zbioru (enum) — nigdy z danych wejściowych.
        private static string Column(QuotaKind kind) => kind switch
        {
            QuotaKind.Post => "postscreated",
            QuotaKind.Comment => "commentscreated",
            _ => "gamesstarted"
        };

        // Atomowe zużycie jednej jednostki limitu: INSERT nowego dnia albo UPDATE licznika tylko poniżej limitu —
        // jedna instrukcja, blokada wiersza w PostgreSQL serializuje równoległe requesty (także z innych instancji).
        // Wykonywane w bieżącej transakcji wywołującego: operacja biznesowa, która się nie uda (rollback), nie zużywa limitu.
        // false = limit na dziś wyczerpany (nic nie zmienione).
        public static async Task<bool> TryConsumeAsync(this ApplicationDbContext context, int userId, QuotaKind kind, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var column = Column(kind);
            var sql =
                $"INSERT INTO dailyuserusages (userid, dateutc, postscreated, commentscreated, gamesstarted) " +
                $"VALUES ({{0}}, {{1}}, {(kind == QuotaKind.Post ? 1 : 0)}, {(kind == QuotaKind.Comment ? 1 : 0)}, {(kind == QuotaKind.Game ? 1 : 0)}) " +
                $"ON CONFLICT (userid, dateutc) DO UPDATE SET {column} = dailyuserusages.{column} + 1 " +
                $"WHERE dailyuserusages.{column} < {{2}} " +
                $"RETURNING {column} AS \"Value\"";
            var used = await context.Database.SqlQueryRaw<int>(sql, userId, Today(now), LimitOf(kind)).ToListAsync(cancellationToken);
            return used.Count == 1;
        }

        public static async Task<int> UsedTodayAsync(this ApplicationDbContext context, int userId, QuotaKind kind, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            var today = Today(now);
            var row = context.DailyUserUsages.AsNoTracking().Where(u => u.UserId == userId && u.DateUtc == today);
            return kind switch
            {
                QuotaKind.Post => await row.Select(u => u.PostsCreated).FirstOrDefaultAsync(cancellationToken),
                QuotaKind.Comment => await row.Select(u => u.CommentsCreated).FirstOrDefaultAsync(cancellationToken),
                _ => await row.Select(u => u.GamesStarted).FirstOrDefaultAsync(cancellationToken)
            };
        }

        // 429 ze stabilnym kodem i Retry-After do północy UTC. Tekst PL z serwera; EN z zasobów po kodzie ("code:…").
        public static ServiceResult LimitReached(QuotaKind kind, DateTimeOffset now) => kind switch
        {
            QuotaKind.Post => ServiceResult.Fail(ServiceError.RateLimited,
                $"Wykorzystano dzisiejszy limit postów ({Posts}). Kolejne możesz dodać po północy UTC.", PostLimitCode, UntilReset(now)),
            QuotaKind.Comment => ServiceResult.Fail(ServiceError.RateLimited,
                $"Wykorzystano dzisiejszy limit komentarzy ({Comments}). Kolejne możesz dodać po północy UTC.", CommentLimitCode, UntilReset(now)),
            _ => ServiceResult.Fail(ServiceError.RateLimited,
                $"Wykorzystano dzisiejszy limit gier ({Games}). Kolejne możesz rozpocząć po północy UTC.", GameLimitCode, UntilReset(now))
        };

        // Przeciwnik nie może dziś zagrać — bez ujawniania jego licznika.
        public static ServiceResult OpponentLimitReached() =>
            ServiceResult.Fail(ServiceError.RateLimited, "Przeciwnik wykorzystał dzisiejszy limit gier. Pojedynek nie rozpoczął się.", OpponentGameLimitCode);
    }
}
