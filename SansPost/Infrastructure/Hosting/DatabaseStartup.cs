using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using SansPost.Infrastructure.Persistence;

namespace SansPost.Infrastructure.Hosting
{
    // Start pojedynczej instancji: baza osiągalna (ograniczone ponawianie) → migracje → dopiero potem ruch.
    // Każdy błąd przerywa start (proces kończy się kodem ≠ 0) — aplikacja nie obsługuje ruchu na niezgodnym schemacie.
    // Wiele instancji: równoległe migracje trzeba przenieść do osobnego joba wdrożeniowego (Database:MigrateOnStartup=false).
    public static class DatabaseStartup
    {
        public static async Task MigrateDatabaseAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
        {
            var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            if (!options.MigrateOnStartup)
            {
                logger.LogInformation("Database migrations on startup are disabled (Database:MigrateOnStartup=false).");
                return;
            }

            await WaitForDatabaseAsync(services, options, logger, cancellationToken);

            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("Database schema is up to date ({Target}).", options.DescribeTarget());
                return;
            }

            logger.LogInformation("Applying {MigrationCount} database migration(s) to {Target}.", pending.Count, options.DescribeTarget());
            var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            foreach (var migration in pending)
            {
                // Każda migracja osobno, żeby w logu było widać, która się nie powiodła.
                await migrator.MigrateAsync(migration, cancellationToken);
                logger.LogInformation("Migration {MigrationName} applied.", migration);
            }
        }

        private static async Task WaitForDatabaseAsync(IServiceProvider services, DatabaseOptions options, ILogger logger, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var scope = services.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await context.Database.OpenConnectionAsync(cancellationToken);
                    await context.Database.CloseConnectionAsync();
                    if (attempt > 1)
                        logger.LogInformation("Database {Target} reachable after {Attempt} attempt(s).", options.DescribeTarget(), attempt);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt <= options.StartupRetries)
                {
                    // Komunikat Npgsql (host/port, SQLSTATE) — nigdy hasło ani pełny connection string.
                    var delay = options.StartupRetryDelay * Math.Min(attempt, 3);
                    logger.LogWarning("Database {Target} not reachable (attempt {Attempt}/{MaxAttempts}): {Reason}. Retrying in {DelaySeconds} s.",
                        options.DescribeTarget(), attempt, options.StartupRetries + 1, ex.GetBaseException().Message, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        // Ponawiamy tylko błędy przejściowe (baza jeszcze wstaje, sieć). Złe hasło czy brak bazy — od razu błąd startu.
        private static bool IsTransient(Exception ex) => ex switch
        {
            PostgresException pg => pg.SqlState is "57P03" /* cannot_connect_now */ or "53300" /* too_many_connections */,
            NpgsqlException npgsql => npgsql.IsTransient,
            TimeoutException => true,
            System.Net.Sockets.SocketException => true,
            InvalidOperationException { InnerException: { } inner } => IsTransient(inner),
            _ => false
        };
    }
}
