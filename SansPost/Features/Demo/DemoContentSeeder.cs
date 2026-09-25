using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
using SansPost.Features.Posts;
using SansPost.Features.Reactions;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Features.Demo
{
    public enum DemoSeedResult
    {
        Disabled,
        AlreadySeeded,
        InsufficientCapacity,
        Seeded
    }

    // Jednorazowe wypełnienie świeżego publicznego demo (PublicDemo:SeedContent = true).
    // - Idempotentny: znacznik = istnienie konta demo (domena DemoContent.EmailDomain); całość w jednej transakcji,
    //   więc albo są wszystkie dane, albo żadne.
    // - Wiele instancji naraz: ta sama blokada wiersza bramy rejestracji (SELECT … FOR UPDATE) co rejestracja
    //   i bootstrap admina — druga instancja czeka, po czym widzi znacznik i nic nie robi. Bez statycznych flag.
    // - Konta demo to zwykłe konta Role = User: zajmują publiczne sloty; przy braku miejsca seed jest pomijany (log).
    // - Nie nadpisuje ani nie usuwa żadnych treści; usunięte/ukryte później posty demo nie wracają.
    public class DemoContentSeeder
    {
        // Niepoprawny hash BCrypt — konta demo nie mogą się zalogować (AuthenticateAsync odrzuca taki rekord).
        private const string NoLoginPasswordHash = "!demo-account-no-login";

        private readonly ApplicationDbContext _context;
        private readonly PublicDemoOptions _options;
        private readonly IAliasGenerator _aliases;
        private readonly TimeProvider _time;
        private readonly ILogger<DemoContentSeeder> _logger;

        public DemoContentSeeder(ApplicationDbContext context, IOptions<PublicDemoOptions> options, IAliasGenerator aliases,
            TimeProvider time, ILogger<DemoContentSeeder> logger)
        {
            _context = context;
            _options = options.Value;
            _aliases = aliases;
            _time = time;
            _logger = logger;
        }

        public async Task<DemoSeedResult> RunAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.SeedContent)
            {
                // Jawnie w logu — pusty feed na świeżej bazie nie może być zagadką.
                _logger.LogInformation("DemoContent: PublicDemo:SeedContent = false — treści startowe nie są tworzone.");
                return DemoSeedResult.Disabled;
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await _context.LockRegistrationGateAsync(cancellationToken);

            // Znacznik ukończonego seeda = konta demo ORAZ ich posty (w dowolnym statusie — ukryte/usunięte posty
            // zostają w bazie i nie są wskrzeszane). Konta demo bez żadnego posta = seed niepełny → dokończ,
            // używając istniejących kont (bez duplikatów, bez naruszania UNIQUE).
            var demoSuffix = IdentityNormalizer.Normalize("@" + DemoContent.EmailDomain);
            var existingAuthors = await _context.Users
                .Where(u => u.NormalizedEmail.EndsWith(demoSuffix))
                .OrderBy(u => u.Id)
                .ToListAsync(cancellationToken);
            var existingIds = existingAuthors.Select(u => u.Id).ToList();

            if (existingAuthors.Count > 0 && await _context.Posts.AnyAsync(p => existingIds.Contains(p.UserId), cancellationToken))
            {
                _logger.LogInformation("DemoContent: treści startowe już istnieją — pomijam.");
                return DemoSeedResult.AlreadySeeded;
            }

            var missingAuthors = Math.Max(0, DemoContent.Authors.Count - existingAuthors.Count);
            var publicAccounts = await _context.Users.CountAsync(u => u.Role == UserRole.User, cancellationToken);
            if (publicAccounts + missingAuthors > _options.MaxPublicAccounts)
            {
                _logger.LogWarning("DemoContent: za mało wolnych kont publicznych ({Used}/{Max}) na {Needed} kont demo — pomijam seed.",
                    publicAccounts, _options.MaxPublicAccounts, missingAuthors);
                return DemoSeedResult.InsufficientCapacity;
            }

            if (existingAuthors.Count > 0)
                _logger.LogWarning("DemoContent: znaleziono {Count} kont demo bez treści (niepełny seed) — dokańczam.", existingAuthors.Count);

            var now = _time.GetUtcNow().UtcDateTime;
            var authors = new List<User>(existingAuthors.Take(DemoContent.Authors.Count));
            foreach (var preferred in DemoContent.Authors.Skip(authors.Count))
            {
                // Przydomek zajęty przez prawdziwe konto — autor demo dostaje inny z tego samego generatora.
                var alias = await _context.Users.AnyAsync(u => u.NormalizedUsername == IdentityNormalizer.Normalize(preferred), cancellationToken)
                    ? await _aliases.SuggestAsync(cancellationToken)
                    : preferred;
                if (alias is null)
                    return DemoSeedResult.InsufficientCapacity;

                var email = $"{alias.ToLowerInvariant()}@{DemoContent.EmailDomain}";
                var author = new User
                {
                    Username = alias,
                    NormalizedUsername = IdentityNormalizer.Normalize(alias),
                    Email = email,
                    NormalizedEmail = IdentityNormalizer.Normalize(email),
                    PasswordHash = NoLoginPasswordHash,
                    Role = UserRole.User,
                    Status = AccountStatus.Active,
                    CreatedAt = now.AddDays(-45 + authors.Count * 3),
                    Subscription = new Subscription { Type = SubscriptionType.Free }
                };
                authors.Add(author);
                _context.Users.Add(author);
                await _context.SaveChangesAsync(cancellationToken); // kolejny SuggestAsync widzi już dodany przydomek
            }

            // Post powitalny: od administratora, jeśli istnieje (np. z BootstrapAdmin), inaczej od pierwszego autora demo.
            var adminId = await _context.Users
                .Where(u => u.Role == UserRole.Admin)
                .OrderBy(u => u.Id)
                .Select(u => (int?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var demo in DemoContent.Posts)
            {
                var authorId = demo.Author < 0 ? adminId ?? authors[0].Id : authors[demo.Author].Id;
                var createdAt = now.AddHours(-demo.AgeHours);

                var post = new Post
                {
                    UserId = authorId,
                    Title = demo.Title,
                    Content = demo.Content,
                    Category = demo.Category,
                    CreatedAt = createdAt
                };
                _context.Posts.Add(post);
                await _context.SaveChangesAsync(cancellationToken);

                foreach (var comment in demo.Comments)
                {
                    _context.Comments.Add(new Comment
                    {
                        PostId = post.Id,
                        UserId = authors[comment.Author].Id,
                        Content = comment.Content,
                        CreatedAt = createdAt.AddHours(comment.HoursAfterPost)
                    });
                }

                foreach (var liker in demo.LikedBy.Distinct())
                {
                    _context.Likes.Add(new Like
                    {
                        PostId = post.Id,
                        UserId = authors[liker].Id,
                        CreatedAt = createdAt.AddHours(1)
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("DemoContent: utworzono {Authors} kont demo i {Posts} postów startowych.",
                authors.Count, DemoContent.Posts.Count);
            return DemoSeedResult.Seeded;
        }
    }

    public sealed class DemoContentHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public DemoContentHostedService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DemoContentSeeder>().RunAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Post przypięty na stronie głównej: jawnie skonfigurowany PublicDemo:FeaturedPostId, a gdy go brak i demo jest
    // seedowane — post powitalny utworzony przez seeder. Zwykły post z bazy; ukryty/usunięty po prostu się nie pokaże.
    public class FeaturedPostLocator
    {
        private readonly ApplicationDbContext _context;
        private readonly PublicDemoOptions _options;

        public FeaturedPostLocator(ApplicationDbContext context, IOptions<PublicDemoOptions> options)
        {
            _context = context;
            _options = options.Value;
        }

        public async Task<int?> GetFeaturedPostIdAsync(CancellationToken cancellationToken = default)
        {
            if (_options.FeaturedPostId is int configured)
                return configured;

            if (!_options.SeedContent)
                return null;

            var demoSuffix = IdentityNormalizer.Normalize("@" + DemoContent.EmailDomain);
            return await _context.Posts
                .AsNoTracking()
                .Where(p => p.Title == DemoContent.WelcomeTitle && p.Status == ContentStatus.Published
                    && (p.User.NormalizedEmail.EndsWith(demoSuffix) || p.User.Role == UserRole.Admin))
                .OrderBy(p => p.Id)
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
