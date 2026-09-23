using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SansPost.Infrastructure.Persistence
{
    public sealed class DatabaseOptions
    {
        public string ConnectionString { get; set; } = string.Empty;
    }

    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services)
        {
            // Connection string z User Secrets (Development) lub ConnectionStrings__DefaultConnection (deployment).
            services.AddOptions<DatabaseOptions>()
                .Configure<IConfiguration>((options, configuration) =>
                    options.ConnectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty)
                .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                    "ConnectionStrings:DefaultConnection nie jest skonfigurowany. " +
                    "Development: dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"...\". " +
                    "Deployment: zmienna środowiskowa ConnectionStrings__DefaultConnection.")
                .ValidateOnStart();

            services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
                options.UseNpgsql(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

            return services;
        }
    }
}
