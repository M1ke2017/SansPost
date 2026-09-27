using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace SansPost.Tests.Postgres
{
    // Rejestruje polecenia SQL wysłane przez EF — do liczenia zapytań (N+1) i EXPLAIN.
    internal sealed class CommandCapture : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = new();

        // Także polecenia synchroniczne — np. podzapytanie, które EF wykonałby po cichu przy budowaniu zapytania.
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // Mała tabela → planner i tak wybrałby seq scan; wyłączamy go, żeby sprawdzić czy indeks PASUJE do zapytania.
        public static async Task<List<string>> ExplainAsync(string connectionString, string sql, NpgsqlParameter[] parameters)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
                await off.ExecuteNonQueryAsync();

            await using var explain = new NpgsqlCommand("EXPLAIN " + sql, connection);
            explain.Parameters.AddRange(parameters.Select(p => p.Clone()).ToArray());

            var plan = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
            return plan;
        }
    }
}
