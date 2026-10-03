using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SansPost.Features.Identity;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Duels
{
    // Statystyki gracza. Gwiazdki prestiżu = liczba zwycięstw PvP (bez osobnej kolumny — liczone z wyników).
    public sealed record DuelStats(int GamesPlayed, int Wins, int Losses, int Draws)
    {
        public static DuelStats None { get; } = new(0, 0, 0, 0);
        public int PrestigeStars => Wins;
    }

    // Pozycja rankingu — przydomek i liczby, bez UserId i e-maila.
    public sealed record LeaderboardEntry(int Rank, string Alias, int Stars, int GamesPlayed, int Wins, int Losses, int Draws);

    public sealed record TableChampion(string Alias, int Stars);

    // Ranking stołu (Top 10) i Mistrz Stołu (#1 z co najmniej jednym zwycięstwem; null = stół czeka na mistrza).
    public sealed record DuelStandings(IReadOnlyList<LeaderboardEntry> Top, TableChampion? Champion);

    // Trwałe znaczenie pojedynków (Sprint 21): zapis wyniku PvP dokładnie raz, statystyki, Top 10, Mistrz Stołu.
    //   • zasada: zwycięstwo PvP (także przez oddanie) = +1 ★; porażka, remis, trening = 0; gwiazdek się nie odejmuje;
    //   • idempotencja: UNIQUE(DuelId) — powtórzony albo równoległy zapis tego samego pojedynku nie tworzy drugiego wiersza;
    //   • widoczność: ranking i Mistrz tylko z kont aktywnych (ta sama reguła co promocja na tablicy Wanted); historia zostaje;
    //   • ranking: jedno zapytanie agregujące (bez N+1), deterministyczna kolejność, krótka pamięć podręczna procesu
    //     unieważniana nowym wynikiem (plakietka mistrza w sali nie odpytuje bazy przy każdym wejściu).
    public sealed class DuelStandingsService
    {
        public const int TopSize = 10;
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
        private const string CacheKey = "duels:standings";

        private readonly ApplicationDbContext _context;
        private readonly IMemoryCache _cache;

        public DuelStandingsService(ApplicationDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        // true = zapisano; false = ten pojedynek był już zapisany (idempotentnie, bez błędu).
        public async Task<bool> RecordAsync(DuelResultRecord record, CancellationToken cancellationToken = default)
        {
            if (await _context.DuelResults.AnyAsync(r => r.DuelId == record.DuelId, cancellationToken))
                return false;

            _context.DuelResults.Add(record);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Równoległy zapis tego samego pojedynku wygrał wyścig (UNIQUE) — wynik jest już w bazie; inny błąd — dalej.
                _context.Entry(record).State = EntityState.Detached;
                if (await _context.DuelResults.AsNoTracking().AnyAsync(r => r.DuelId == record.DuelId, cancellationToken))
                    return false;
                throw;
            }

            _cache.Remove(CacheKey);
            return true;
        }

        // Statystyki jednego gracza — jedno zapytanie (trzy liczniki po indeksach graczy i zwycięzcy).
        public async Task<DuelStats> GetStatsAsync(int userId, CancellationToken cancellationToken = default)
        {
            var row = await _context.DuelResults
                .AsNoTracking()
                .Where(r => r.PlayerOneId == userId || r.PlayerTwoId == userId)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Games = g.Count(),
                    Wins = g.Count(r => r.WinnerId == userId),
                    Draws = g.Count(r => r.ResultType == DuelResultType.Draw)
                })
                .SingleOrDefaultAsync(cancellationToken);   // grupa po stałej: najwyżej jeden wiersz (bez ostrzeżenia EF o First bez OrderBy)

            return row is null ? DuelStats.None : new DuelStats(row.Games, row.Wins, row.Games - row.Wins - row.Draws, row.Draws);
        }

        public async Task<DuelStandings> GetStandingsAsync(CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(CacheKey, out DuelStandings? cached) && cached is not null)
                return cached;

            // Udział w pojedynku z perspektywy każdego z dwóch graczy (UNION ALL), agregacja per gracz, tylko konta aktywne.
            var sides = _context.DuelResults.AsNoTracking()
                .Select(r => new { PlayerId = r.PlayerOneId, Win = r.WinnerId == r.PlayerOneId ? 1 : 0, Draw = r.ResultType == DuelResultType.Draw ? 1 : 0 })
                .Concat(_context.DuelResults.AsNoTracking()
                    .Select(r => new { PlayerId = r.PlayerTwoId, Win = r.WinnerId == r.PlayerTwoId ? 1 : 0, Draw = r.ResultType == DuelResultType.Draw ? 1 : 0 }));

            var rows = await sides
                .GroupBy(s => s.PlayerId)
                .Select(g => new { PlayerId = g.Key, Games = g.Count(), Wins = g.Sum(s => s.Win), Draws = g.Sum(s => s.Draw) })
                .Join(_context.Users.Where(u => u.Status == AccountStatus.Active), s => s.PlayerId, u => u.Id,
                    (s, u) => new { u.Username, u.NormalizedUsername, s.Games, s.Wins, s.Draws })
                // Deterministycznie: więcej zwycięstw, potem mniej rozegranych (skuteczność), potem przydomek.
                .OrderByDescending(x => x.Wins)
                .ThenBy(x => x.Games)
                .ThenBy(x => x.NormalizedUsername)
                .Take(TopSize)
                .ToListAsync(cancellationToken);

            var top = rows.Select((x, i) => new LeaderboardEntry(i + 1, x.Username, x.Wins, x.Games, x.Wins, x.Games - x.Wins - x.Draws, x.Draws)).ToList();
            var champion = top.FirstOrDefault() is { Stars: > 0 } first ? new TableChampion(first.Alias, first.Stars) : null;
            var standings = new DuelStandings(top, champion);
            _cache.Set(CacheKey, standings, CacheLifetime);
            return standings;
        }
    }
}
