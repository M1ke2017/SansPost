using Microsoft.EntityFrameworkCore;
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
