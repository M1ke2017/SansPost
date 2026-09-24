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
        private readonly IDictionary<string, string?>? _extraSettings;

        public PostgresApiFactory(string connectionString, IDictionary<string, string?>? extraSettings = null)
        {
            _connectionString = connectionString;
            _extraSettings = extraSettings;
        }

        protected override IDictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                foreach (var (key, value) in _extraSettings ?? new Dictionary<string, string?>())
                    settings[key] = value;
                return settings;
            }
        }

        protected override void ConfigureDatabase(DbContextOptionsBuilder options) => options.UseNpgsql(_connectionString);

        protected override void InitializeDatabase(ApplicationDbContext context)
        {
        }
    }
}
