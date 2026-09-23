using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Rozdziela role od poziomu subskrypcji. Migracja wyłącznie danych — schemat bez zmian.
    ///
    /// users.role przed: 0 = User, 1 = Premium, 2 = Admin, 3 = Regular
    /// users.role po:    0 = User, 1 = Admin
    ///
    /// Użytkownicy z dawną rolą Premium dostają subskrypcję Premium (bez daty wygaśnięcia),
    /// żeby nie utracić statusu przy usunięciu roli.
    /// </summary>
    public partial class SeparateRolesFromSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Dawna rola Premium (1) -> subskrypcja Premium (1). Musi się wykonać przed przemapowaniem ról.
            migrationBuilder.Sql(
                """
                UPDATE subscriptions
                SET type = 1, expiresat = NULL
                WHERE type <> 1
                  AND userid IN (SELECT id FROM users WHERE role = 1);
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO subscriptions (userid, type, createdat, expiresat)
                SELECT u.id, 1, now(), NULL
                FROM users u
                WHERE u.role = 1
                  AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.userid = u.id);
                """);

            // 2. Admin (2) -> 1; User (0), Premium (1), Regular (3) -> User (0).
            migrationBuilder.Sql(
                """
                UPDATE users
                SET role = CASE WHEN role = 2 THEN 1 ELSE 0 END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stratne: nie da się odtworzyć ról Premium/Regular. Admin (1) -> 2, reszta -> User (0).
            // Subskrypcje utworzone w Up pozostają (nie są szkodliwe dla starego modelu).
            migrationBuilder.Sql(
                """
                UPDATE users
                SET role = CASE WHEN role = 1 THEN 2 ELSE 0 END;
                """);
        }
    }
}
