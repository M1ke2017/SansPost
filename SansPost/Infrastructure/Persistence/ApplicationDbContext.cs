using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;
using SansPost.Features.Comments;
using SansPost.Features.Identity;
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
                post.Property(p => p.Category)
                    .HasConversion<string>()
                    .HasMaxLength(PostLimits.CategoryMaxLength);
                post.ToTable(t => t.HasCheckConstraint(
                    "CK_posts_category",
                    $"category IN ({string.Join(", ", Enum.GetNames<PostCategory>().Select(n => $"'{n}'"))})"));

                // Optimistic concurrency: UPDATE/DELETE ... WHERE version = @original.
                post.Property(p => p.Version).IsConcurrencyToken();

                post.HasOne(p => p.User)
                    .WithMany()
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Feed główny: ORDER BY createdat DESC, id DESC (+ keyset WHERE). Sort "oldest" = skan wsteczny.
                post.HasIndex(p => new { p.CreatedAt, p.Id })
                    .IsDescending(true, true)
                    .HasDatabaseName("IX_posts_feed");

                // Feed autora + COUNT(*) WHERE userid w limicie postów. Zastępuje indeks FK na samym userid.
                post.HasIndex(p => new { p.UserId, p.CreatedAt, p.Id })
                    .IsDescending(false, true, true)
                    .HasDatabaseName("IX_posts_author_feed");

                // Feed kategorii: WHERE category = @c ORDER BY createdat DESC, id DESC.
                post.HasIndex(p => new { p.Category, p.CreatedAt, p.Id })
                    .IsDescending(false, true, true)
                    .HasDatabaseName("IX_posts_category_feed");
            });

            modelBuilder.Entity<Comment>(comment =>
            {
                comment.Property(c => c.Content).HasMaxLength(CommentLimits.ContentMaxLength);

                // Optimistic concurrency: UPDATE/DELETE ... WHERE version = @expected.
                comment.Property(c => c.Version).IsConcurrencyToken();

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
                comment.HasIndex(c => new { c.PostId, c.CreatedAt, c.Id })
                    .HasDatabaseName("IX_comments_post_thread");

                // Aktywność autora na profilu: WHERE userid ORDER BY createdat DESC, id DESC; COUNT(*) WHERE userid.
                comment.HasIndex(c => new { c.UserId, c.CreatedAt, c.Id })
                    .IsDescending(false, true, true)
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
