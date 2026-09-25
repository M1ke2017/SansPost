using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SansPost.Infrastructure.Persistence
{
    // ConnectionStrings:DefaultConnection + sekcja "Database". Hasło może przyjść osobno (Database:Password, np. plik
    // Docker secret) — connection string w konfiguracji/compose nie musi zawierać sekretu.
    public sealed class DatabaseOptions
    {
        public const string SectionName = "Database";

        // Sprint 10: 100 wirtualnych użytkowników utrzymywało ~99 połączeń przy domyślnej puli Npgsql (100) i domyślnym
        // max_connections PostgreSQL (100). 30 zostawia ~70 połączeń na admina, migracje, monitoring, backup (pg_dump)
        // i ewentualną drugą instancję; pomiar z pulą 30 — raport Sprintu 11.
        public const int DefaultMaxPoolSize = 30;

        public string ConnectionString { get; set; } = string.Empty;

        public string? Password { get; set; }

        public int MaxPoolSize { get; set; } = DefaultMaxPoolSize;

        // Migracje przy starcie (pojedyncza instancja). Przy wielu instancjach: osobny job wdrożeniowy.
        public bool MigrateOnStartup { get; set; } = true;

        // Ograniczone ponawianie przy starcie (PostgreSQL może wstawać wolniej niż aplikacja).
        public int StartupRetries { get; set; } = 10;
        public TimeSpan StartupRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

        // Pełny connection string do użycia przez EF/Npgsql (pula ograniczona, opcjonalne hasło z sekretu).
        public string BuildConnectionString()
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                MaxPoolSize = MaxPoolSize,
                ApplicationName = "SansPost"
            };
            if (!string.IsNullOrEmpty(Password))
                builder.Password = Password.Trim();
            return builder.ConnectionString;
        }

        // Tylko do diagnostyki: host/port/baza — bez użytkownika i hasła.
        public string DescribeTarget()
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }

        internal static bool HasValidFormat(string connectionString)
        {
            try
            {
                _ = new NpgsqlConnectionStringBuilder(connectionString);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services)
        {
            // Connection string z User Secrets (Development) lub ConnectionStrings__DefaultConnection (deployment).
            // Komunikaty walidacji nigdy nie zawierają wartości.
            services.AddOptions<DatabaseOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                {
                    configuration.GetSection(DatabaseOptions.SectionName).Bind(options);
                    options.ConnectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
                })
                .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                    "ConnectionStrings:DefaultConnection nie jest skonfigurowany. " +
                    "Development: dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"...\". " +
                    "Deployment: zmienna środowiskowa ConnectionStrings__DefaultConnection.")
                .Validate(o => string.IsNullOrWhiteSpace(o.ConnectionString) || DatabaseOptions.HasValidFormat(o.ConnectionString),
                    "ConnectionStrings:DefaultConnection ma nieprawidłowy format.")
                .Validate(o => o.MaxPoolSize is >= 5 and <= 200,
                    "Database:MaxPoolSize musi mieścić się w zakresie 5–200 i zostawiać zapas poniżej max_connections PostgreSQL.")
                .Validate(o => o.StartupRetries is >= 0 and <= 60 && o.StartupRetryDelay > TimeSpan.Zero && o.StartupRetryDelay <= TimeSpan.FromMinutes(1),
                    "Database:StartupRetries (0–60) lub Database:StartupRetryDelay (do 1 min) ma nieprawidłową wartość.")
                .ValidateOnStart();

            services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
                options.UseNpgsql(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.BuildConnectionString()));

            return services;
        }
    }
}
