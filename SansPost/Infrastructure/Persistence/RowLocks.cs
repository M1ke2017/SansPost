using Microsoft.EntityFrameworkCore;

namespace SansPost.Infrastructure.Persistence
{
    public static class RowLocks
    {
        // Blokuje wiersz użytkownika do końca bieżącej transakcji (PostgreSQL: SELECT ... FOR NO KEY UPDATE).
        // Serializuje sekcje krytyczne jednego użytkownika (np. limit postów) między wszystkimi instancjami aplikacji,
        // nie blokując innych użytkowników ani sprawdzeń FK (FOR KEY SHARE).
        // Wymaga aktywnej transakcji. Zwraca false, gdy użytkownik nie istnieje.
        public static async Task<bool> LockUserRowAsync(this ApplicationDbContext context, int userId, CancellationToken cancellationToken)
        {
            if (context.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Blokada wiersza wymaga aktywnej transakcji.");

            if (!context.Database.IsNpgsql())
            {
                // SQLite (wyłącznie szybkie testy) nie ma blokad wierszy; zapisujący i tak są serializowani na poziomie pliku.
                return await context.Users.AnyAsync(u => u.Id == userId, cancellationToken);
            }

            var locked = await context.Database
                .SqlQuery<int>($"SELECT id AS \"Value\" FROM users WHERE id = {userId} FOR NO KEY UPDATE")
                .ToListAsync(cancellationToken);

            return locked.Count > 0;
        }
    }
}
