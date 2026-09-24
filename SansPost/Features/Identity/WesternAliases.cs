using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Identity
{
    // Jedyne źródło nazw użytkowników publicznych kont: przydomek = Prefiks + Sufiks (PascalCase, bez spacji i znaków losowych).
    // Brak nicków wpisywanych ręcznie = brak potrzeby filtrowania wulgaryzmów, podszywania się i moderacji nazw.
    // Słowa: neutralne, pustynne/frontierowe, bez dwuznaczności. Zmiana listy = przegląd całej listy, nie pojedynczego słowa.
    public static class WesternAliases
    {
        public static readonly IReadOnlyList<string> Prefixes = new[]
        {
            "Dusty", "Silver", "Red", "Lone", "Copper", "Sage", "Golden", "Midnight", "Desert", "Canyon",
            "Rusty", "Wild", "Quiet", "Amber", "Iron", "Mesa", "Dawn", "Sunset", "Prairie", "Cedar",
            "Juniper", "Timber", "Saddle", "River", "Sandy", "Stormy", "Windy", "Brave", "Swift", "Bright",
            "Misty", "Rocky", "Autumn", "Frontier", "Starry", "Harvest", "Pinyon", "Cobalt", "Coyote", "Willow"
        };

        public static readonly IReadOnlyList<string> Suffixes = new[]
        {
            "Raven", "Coyote", "Rider", "Trail", "Fox", "Hawk", "Jack", "Pine", "Mustang", "Scout",
            "Creek", "Mesa", "Canyon", "Wolf", "Sparrow", "Lantern", "Walker", "Stone", "Arrow", "Duster",
            "Rose", "Ridge", "Falcon", "Bison", "Owl", "Wren", "Spur", "Saddle", "Rover", "Wagon",
            "Compass", "Lark", "Drifter", "Bluff", "Sage", "Heron", "Badger", "Juniper", "Tumbleweed", "Horizon"
        };

        // Pełna przestrzeń przydomków (bez par typu "MesaMesa").
        public static readonly IReadOnlyList<string> All = Prefixes
            .SelectMany(prefix => Suffixes.Where(suffix => suffix != prefix).Select(suffix => prefix + suffix))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        private static readonly HashSet<string> Allowed = new(All, StringComparer.Ordinal);

        // Dokładnie w formie z generatora (wielkość liter ma znaczenie) — "dustyraven" czy "Dusty Raven" nie przejdą.
        public static bool IsCurated(string? alias) => alias is not null && Allowed.Contains(alias);

        public static IReadOnlyList<string> RandomSample(int count) =>
            All.OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
    }

    public interface IAliasGenerator
    {
        // Wolny w chwili sprawdzenia przydomek albo null, gdy cała przestrzeń jest zajęta.
        // Bez rezerwacji: ostateczną gwarancją unikalności jest UNIQUE index (i brama rejestracji).
        Task<string?> SuggestAsync(CancellationToken cancellationToken = default);
    }

    public class AliasGenerator : IAliasGenerator
    {
        private const int SampleSize = 24;

        private readonly ApplicationDbContext _context;

        public AliasGenerator(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<string?> SuggestAsync(CancellationToken cancellationToken = default)
        {
            // Zwykle wystarcza losowa próbka sprawdzona jednym zapytaniem; pełna przestrzeń dopiero przy dużym zapełnieniu.
            var free = await FreeAsync(WesternAliases.RandomSample(SampleSize), cancellationToken);
            if (free.Count == 0)
                free = await FreeAsync(WesternAliases.All, cancellationToken);

            return free.Count == 0 ? null : free[Random.Shared.Next(free.Count)];
        }

        private async Task<List<string>> FreeAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
        {
            var normalized = candidates.Select(IdentityNormalizer.Normalize).ToList();
            var taken = await _context.Users
                .AsNoTracking()
                .Where(u => normalized.Contains(u.NormalizedUsername))
                .Select(u => u.NormalizedUsername)
                .ToListAsync(cancellationToken);

            var takenSet = new HashSet<string>(taken, StringComparer.Ordinal);
            return candidates.Where(c => !takenSet.Contains(IdentityNormalizer.Normalize(c))).ToList();
        }
    }
}
