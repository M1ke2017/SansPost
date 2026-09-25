using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Infrastructure.Hosting
{
    // Readiness: PostgreSQL osiągalny w krótkim czasie. Odpowiedź publiczna to wyłącznie "Healthy"/"Unhealthy"
    // (bez opisu, wyjątku ani connection stringu); szczegół trafia tylko do logu operatora.
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        public const string ReadyTag = "ready";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DatabaseHealthCheck> _logger;

        public DatabaseHealthCheck(IServiceScopeFactory scopeFactory, ILogger<DatabaseHealthCheck> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (await db.Database.CanConnectAsync(timeout.Token))
                    return HealthCheckResult.Healthy();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Readiness: database check failed ({ExceptionType}).", ex.GetType().Name);
            }

            SansPostTelemetry.DatabaseFailures.Add(1, new KeyValuePair<string, object?>("source", "readiness"));
            return HealthCheckResult.Unhealthy();
        }
    }
}
