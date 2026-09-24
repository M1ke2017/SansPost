using Microsoft.EntityFrameworkCore;
using SansPost.Features.Identity;

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
            EnsureTransaction(context);

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

        // Blokuje jedyny wiersz bramy rejestracji (FOR UPDATE) do końca transakcji — globalna sekcja krytyczna
        // "policz konta → utwórz konto" we wszystkich instancjach. Wiersz zamiast advisory lock: blokada jest
        // widoczna w pg_locks jak każda inna, zwalniana automatycznie z transakcją i nie wymaga uzgadniania kluczy.
        public static async Task LockRegistrationGateAsync(this ApplicationDbContext context, CancellationToken cancellationToken)
        {
            EnsureTransaction(context);

            if (!context.Database.IsNpgsql())
                return;

            var locked = await context.Database
                .SqlQuery<int>($"SELECT id AS \"Value\" FROM registrationgates WHERE id = {RegistrationGate.SingletonId} FOR UPDATE")
                .ToListAsync(cancellationToken);

            if (locked.Count == 0)
                throw new InvalidOperationException("Brak wiersza bramy rejestracji — migracja DemoSafetyAndModeration nie została zastosowana.");
        }

        private static void EnsureTransaction(ApplicationDbContext context)
        {
            if (context.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Blokada wiersza wymaga aktywnej transakcji.");
        }
    }
}
