using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using SansPost.Models;
using SansPost.Models.Enum;

namespace SansPost.Data
{
    public class AppliactionDbContext : DbContext
    {
        public AppliactionDbContext(DbContextOptions<AppliactionDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Post> Posts { get; set; }
        public DbSet<Subscription> Subscriptions { get; set; }
        public DbSet<Comment> Comments { get; set; }
        public DbSet<Like> Likes { get; set; }
    

    protected override void OnModelCreating(ModelBuilder modelBuilder) //Mapowanie tabeli 
        {
            base.OnModelCreating(modelBuilder);

            // Automatyczne zmienianie nazw tabel na małe litery
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                entity.SetTableName(entity.GetTableName().ToLower());

                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(property.GetColumnName().ToLower());
                }
            }

            // Konwersja enum/Category na string w bazie danych
            modelBuilder.Entity<Post>()
                .Property(p => p.Category)
                .HasConversion<string>();

            modelBuilder.Entity<User>()
                .HasOne(u => u.Subscription)
                .WithOne(s => s.User)
                .HasForeignKey<Subscription>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }


        public async Task<bool> ExecuteTransactionsAsync(Func<Task> action)
        {
            using var transaction = await this.Database.BeginTransactionAsync();
            try
            {
                await action();
                await transaction.CommitAsync();
                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                return false;
            }

        }
    }
}

