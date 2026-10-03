using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;
using SansPost.Features.Comments;
using SansPost.Features;
using SansPost.Features.Identity;
using SansPost.Features.Moderation;
using SansPost.Features.Notifications;
using SansPost.Features.Posts;
using SansPost.Features.Reactions;

namespace SansPost.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Post> Posts { get; set; }
        public DbSet<Subscription> Subscriptions { get; set; }
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Like> Likes { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<Report> Reports { get; set; }
        public DbSet<ModerationAction> ModerationActions { get; set; }
        public DbSet<RegistrationGate> RegistrationGates { get; set; }
        public DbSet<Notification> Notifications { get; set; }
        public DbSet<SansPost.Features.Duels.DuelResultRecord> DuelResults { get; set; }
        public DbSet<SansPost.Features.Usage.DailyUserUsage> DailyUserUsages { get; set; }

        private const string PublishedOnly = "status = 'Published'";

        // Enum zapisywany jako nazwa + CHECK zamykający zbiór wartości w bazie.
        private static void EnumAsText<TEntity, TEnum>(
            Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
            System.Linq.Expressions.Expression<Func<TEntity, TEnum>> property,
            string column, string table)
            where TEntity : class
            where TEnum : struct, Enum
        {
            entity.Property(property).HasConversion<string>().HasMaxLength(32);
            entity.ToTable(t => t.HasCheckConstraint(
                $"CK_{table}_{column}",
                $"{column} IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})"));
        }

        // Elementy specyficzne dla PostgreSQL (FTS, operator classes). Szybkie testy na SQLite ich nie mapują;
        // wyszukiwanie jest testowane wyłącznie na PostgreSQL (Testcontainers).
        private static void ConfigurePostgresDiscovery(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Post>(post =>
            {
                // Generowana kolumna STORED utrzymywana przez PostgreSQL przy każdym INSERT/UPDATE tytułu lub treści.
                // Shadow property: nie jest częścią domeny ani DTO. Aplikacja nigdy jej nie zapisuje ani nie odczytuje
                // po zapisie (brak RETURNING kilku kB tsvector przy każdej edycji).
                var searchVector = post.Property<NpgsqlTsVector>(TextSearch.PostSearchVector)
                    .HasComputedColumnSql(TextSearch.PostSearchVectorSql, stored: true)
                    .ValueGeneratedNever();
                searchVector.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
                searchVector.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

                // WHERE searchvector @@ websearch_to_tsquery(...)
                post.HasIndex(TextSearch.PostSearchVector)
                    .HasMethod("gin")
                    .HasDatabaseName("IX_posts_search");
            });

            // Prefix lookup autorów: normalizedusername LIKE 'PREFIX%'. Istniejący UNIQUE index (collation bazy)
            // nie obsługuje LIKE poza collation "C" — text_pattern_ops porównuje bajtowo i obsługuje prefiksy.
            modelBuilder.Entity<User>()
                .HasIndex(u => u.NormalizedUsername, "IX_users_username_prefix")
                .HasOperators("text_pattern_ops");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(user =>
            {
                // Unikalność tożsamości wymuszana przez bazę, nie tylko przez AnyAsync w serwisie.
                user.HasIndex(u => u.NormalizedEmail).IsUnique();
                user.HasIndex(u => u.NormalizedUsername).IsUnique();

                // Status konta — osobny od roli. Brak indeksu: żadne zapytanie nie wybiera kont po statusie
                // (walidacja sesji i WriteGuard czytają pojedynczy wiersz po PK; tablica Wanted sprawdza status autora
                // kandydatów przy złączeniu posts → users, które prowadzi okno IX_posts_feed).
                EnumAsText(user, u => u.Status, "status", "users");

                user.HasOne(u => u.Subscription)
                    .WithOne(s => s.User)
                    .HasForeignKey<Subscription>(s => s.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Post>(post =>
            {
                post.Property(p => p.Title).HasMaxLength(PostLimits.TitleMaxLength);
                post.Property(p => p.Content).HasMaxLength(PostLimits.ContentMaxLength);
                post.Property(p => p.ImageUrl).HasMaxLength(PostLimits.ImageUrlMaxLength);

                // Kategoria jako czytelna nazwa + CHECK zamykający zbiór wartości w bazie.
                EnumAsText(post, p => p.Category, "category", "posts");

                // Optimistic concurrency: UPDATE/DELETE ... WHERE version = @original.
                post.Property(p => p.Version).IsConcurrencyToken();

                EnumAsText(post, p => p.Status, "status", "posts");

                post.HasOne(p => p.User)
                    .WithMany()
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Feed główny: ORDER BY createdat DESC, id DESC (+ keyset WHERE). Sort "oldest" = skan wsteczny.
                // Częściowy (tylko Published): wszyscy konsumenci (Newest, okno Popular) filtrują status = 'Published';
                // ukryte/usunięte wiersze nie zajmują indeksu ani nie są przeglądane przez skan.
                post.HasIndex(p => new { p.CreatedAt, p.Id })
                    .IsDescending(true, true)
                    .HasFilter(PublishedOnly)
                    .HasDatabaseName("IX_posts_feed");

                // Feed autora + COUNT(*) WHERE userid w limicie postów. Zastępuje indeks FK na samym userid.
                post.HasIndex(p => new { p.UserId, p.CreatedAt, p.Id })
                    .IsDescending(false, true, true)
                    .HasDatabaseName("IX_posts_author_feed");

                // Feed kategorii: WHERE category = @c ORDER BY createdat DESC, id DESC.
                post.HasIndex(p => new { p.Category, p.CreatedAt, p.Id })
                    .IsDescending(false, true, true)
                    .HasFilter(PublishedOnly)
                    .HasDatabaseName("IX_posts_category_feed");
            });

            modelBuilder.Entity<Comment>(comment =>
            {
                comment.Property(c => c.Content).HasMaxLength(CommentLimits.ContentMaxLength);

                // Optimistic concurrency: UPDATE/DELETE ... WHERE version = @expected.
                comment.Property(c => c.Version).IsConcurrencyToken();

                EnumAsText(comment, c => c.Status, "status", "comments");

                // FK = ostateczna gwarancja braku osieroconych komentarzy; usunięcie posta kasuje jego komentarze.
                comment.HasOne(c => c.Post)
                    .WithMany()
                    .HasForeignKey(c => c.PostId)
                    .OnDelete(DeleteBehavior.Cascade);

                comment.HasOne(c => c.User)
                    .WithMany()
                    .HasForeignKey(c => c.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Wątek posta: WHERE postid = @p ORDER BY createdat, id (+ keyset); COUNT(*) WHERE postid w feedzie.
                // Częściowe (tylko Published): wątek i liczniki w feedzie/profilu zawsze filtrują opublikowane —
                // COUNT(*) pozostaje Index Only Scan bez sięgania do heap po kolumnę status.
                comment.HasIndex(c => new { c.PostId, c.CreatedAt, c.Id })
                    .HasFilter(PublishedOnly)
                    .HasDatabaseName("IX_comments_post_thread");

                // Aktywność autora na profilu: WHERE userid ORDER BY createdat DESC, id DESC; COUNT(*) WHERE userid.
                comment.HasIndex(c => new { c.UserId, c.CreatedAt, c.Id })
                    .IsDescending(false, true, true)
                    .HasFilter(PublishedOnly)
                    .HasDatabaseName("IX_comments_author");
            });

            modelBuilder.Entity<Like>(like =>
            {
                // Jeden użytkownik = co najwyżej jedno polubienie posta. Wspiera też COUNT(*) WHERE postid
                // oraz lookup "LikedByCurrentUser" (postid, userid).
                like.HasIndex(l => new { l.PostId, l.UserId })
                    .IsUnique()
                    .HasDatabaseName("UX_likes_post_user");

                like.HasOne(l => l.Post)
                    .WithMany()
                    .HasForeignKey(l => l.PostId)
                    .OnDelete(DeleteBehavior.Cascade);

                like.HasOne(l => l.User)
                    .WithMany()
                    .HasForeignKey(l => l.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Report>(report =>
            {
                EnumAsText(report, r => r.TargetType, "targettype", "reports");
                EnumAsText(report, r => r.Reason, "reason", "reports");
                EnumAsText(report, r => r.Status, "status", "reports");
                report.Property(r => r.Details).HasMaxLength(ModerationLimits.DetailsMaxLength);

                report.HasOne(r => r.Reporter)
                    .WithMany()
                    .HasForeignKey(r => r.ReporterUserId)
                    .OnDelete(DeleteBehavior.Cascade);

                report.HasOne<User>()
                    .WithMany()
                    .HasForeignKey(r => r.ReviewedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                // Jedno trwające zgłoszenie danego zasobu per reporter — gwarancja w bazie (ON CONFLICT w ReportService).
                report.HasIndex(r => new { r.ReporterUserId, r.TargetType, r.TargetId })
                    .IsUnique()
                    .HasFilter("status = 'Pending'")
                    .HasDatabaseName("UX_reports_pending_per_reporter");

                // Kolejka moderacji: WHERE status = 'Pending' ORDER BY createdat, id (keyset, FIFO).
                report.HasIndex(r => new { r.CreatedAt, r.Id })
                    .HasFilter("status = 'Pending'")
                    .HasDatabaseName("IX_reports_pending_queue");

                // Ukrycie treści rozstrzyga jej trwające zgłoszenia: WHERE targettype AND targetid AND status = 'Pending'.
                report.HasIndex(r => new { r.TargetType, r.TargetId })
                    .HasFilter("status = 'Pending'")
                    .HasDatabaseName("IX_reports_pending_target");
            });

            modelBuilder.Entity<ModerationAction>(action =>
            {
                EnumAsText(action, a => a.ActionType, "actiontype", "moderationactions");
                EnumAsText(action, a => a.TargetType, "targettype", "moderationactions");
                action.Property(a => a.Reason).HasMaxLength(ModerationLimits.ReasonMaxLength);

                // Audyt przetrwa zmiany kont: brak kaskadowego usuwania wpisów razem z administratorem.
                action.HasOne(a => a.Admin)
                    .WithMany()
                    .HasForeignKey(a => a.AdminUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Historia akcji: ORDER BY createdat DESC, id DESC (keyset).
                action.HasIndex(a => new { a.CreatedAt, a.Id })
                    .IsDescending(true, true)
                    .HasDatabaseName("IX_moderationactions_history");
            });

            modelBuilder.Entity<RegistrationGate>(gate =>
            {
                gate.Property(g => g.Id).ValueGeneratedNever();
                gate.ToTable(t => t.HasCheckConstraint("CK_registrationgate_singleton", $"id = {RegistrationGate.SingletonId}"));
                gate.HasData(new RegistrationGate { Id = RegistrationGate.SingletonId });
            });

            modelBuilder.Entity<RefreshToken>(token =>
            {
                token.Property(t => t.TokenHash).HasMaxLength(64);
                token.HasIndex(t => t.TokenHash).IsUnique();

                token.HasOne(t => t.User)
                    .WithMany()
                    .HasForeignKey(t => t.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                token.HasOne<RefreshToken>()
                    .WithMany()
                    .HasForeignKey(t => t.ReplacedByTokenId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Notification>(notification =>
            {
                EnumAsText(notification, n => n.Type, "type", "notifications");

                // CASCADE tylko dla fizycznego usunięcia (poziom bazy) — spójnie z komentarzami i polubieniami.
                // Aplikacja usuwa wyłącznie miękko (Status), więc ukrycie/usunięcie posta lub komentarza NIE kasuje historii.
                notification.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
                notification.HasOne(n => n.Actor).WithMany().HasForeignKey(n => n.ActorUserId).OnDelete(DeleteBehavior.Cascade);
                notification.HasOne(n => n.Post).WithMany().HasForeignKey(n => n.PostId).OnDelete(DeleteBehavior.Cascade);
                notification.HasOne(n => n.Comment).WithMany().HasForeignKey(n => n.CommentId).OnDelete(DeleteBehavior.Cascade);

                // Lista odbiorcy: WHERE userid = @u ORDER BY createdat DESC, id DESC (+ keyset).
                notification.HasIndex(n => new { n.UserId, n.CreatedAt, n.Id })
                    .IsDescending(false, true, true)
                    .HasDatabaseName("IX_notifications_user_feed");

                // Licznik nieprzeczytanych: COUNT(*) WHERE userid = @u AND readat IS NULL — indeks tylko nieprzeczytanych
                // (maleje wraz z czytaniem, zamiast rosnąć z całą historią jak (userid, readat)).
                notification.HasIndex(n => n.UserId)
                    .HasFilter("readat IS NULL")
                    .HasDatabaseName("IX_notifications_unread");
            });

            // Dzienne limity biznesowe (v1.0): jeden wiersz na użytkownika i dzień UTC — klucz złożony (UNIQUE z definicji),
            // atomowy upsert w DailyQuota. Wiersze znikają razem z kontem (kaskada); liczniki nieujemne.
            modelBuilder.Entity<SansPost.Features.Usage.DailyUserUsage>(usage =>
            {
                usage.HasKey(u => new { u.UserId, u.DateUtc });
                usage.HasOne(u => u.User).WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
                usage.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_dailyuserusages_counts", "postscreated >= 0 AND commentscreated >= 0 AND gamesstarted >= 0");
                });
            });

            modelBuilder.Entity<SansPost.Features.Duels.DuelResultRecord>(result =>
            {
                EnumAsText(result, r => r.ResultType, "resulttype", "duelresults");
                EnumAsText(result, r => r.FinishReason, "finishreason", "duelresults");

                // Historia wyników zostaje — konta nie są usuwane fizycznie, a gdyby były, baza tego nie pozwoli po cichu.
                result.HasOne(r => r.PlayerOne).WithMany().HasForeignKey(r => r.PlayerOneId).OnDelete(DeleteBehavior.Restrict);
                result.HasOne(r => r.PlayerTwo).WithMany().HasForeignKey(r => r.PlayerTwoId).OnDelete(DeleteBehavior.Restrict);
                result.HasOne(r => r.Winner).WithMany().HasForeignKey(r => r.WinnerId).OnDelete(DeleteBehavior.Restrict);

                // Idempotencja: jeden wynik na pojedynek (drugi zapis tego samego DuelId kończy się naruszeniem UNIQUE).
                result.HasIndex(r => r.DuelId).IsUnique().HasDatabaseName("UX_duelresults_duel");
                // Statystyki gracza i ranking: udział po stronie jednego albo drugiego gracza, zwycięstwa.
                result.HasIndex(r => r.PlayerOneId).HasDatabaseName("IX_duelresults_playerone");
                result.HasIndex(r => r.PlayerTwoId).HasDatabaseName("IX_duelresults_playertwo");
                result.HasIndex(r => r.WinnerId).HasDatabaseName("IX_duelresults_winner");

                result.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_duelresults_players", "playeroneid <> playertwoid");
                    t.HasCheckConstraint("CK_duelresults_winner",
                        "(resulttype = 'Draw' AND winnerid IS NULL) OR (resulttype = 'Win' AND winnerid IN (playeroneid, playertwoid))");
                    t.HasCheckConstraint("CK_duelresults_rounds", "roundcount >= 0");
                });
            });

            if (Database.IsNpgsql())
                ConfigurePostgresDiscovery(modelBuilder);

            // Nazwy tabel i kolumn małymi literami (zgodnie z istniejącymi migracjami).
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                entity.SetTableName(entity.GetTableName()!.ToLower());

                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(property.GetColumnName().ToLower());
                }
            }
        }
    }
}
