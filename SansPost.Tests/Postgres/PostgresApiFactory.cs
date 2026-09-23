using Microsoft.EntityFrameworkCore;
using SansPost.Infrastructure.Persistence;
using SansPost.Tests.TestInfrastructure;

namespace SansPost.Tests.Postgres
{
    // Pełny pipeline HTTP (auth, polityki, JWT, rate limiting) na prawdziwym PostgreSQL z Testcontainers.
    // Schemat pochodzi z migracji wykonanych przez PostgresFixture — tu nic nie jest tworzone.
    public sealed class PostgresApiFactory : SansPostFactory
    {
        private readonly string _connectionString;

        public PostgresApiFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        protected override void ConfigureDatabase(DbContextOptionsBuilder options) => options.UseNpgsql(_connectionString);

        protected override void InitializeDatabase(ApplicationDbContext context)
        {
        }
    }
}
