using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Sprint 2: znormalizowane pola tożsamości z UNIQUE index oraz tabela refresh tokenów (tylko hash SHA-256).
    /// Wymaga wcześniejszego zastosowania SeparateRolesFromSubscriptions (kolejność wg timestampów).
    /// </summary>
    public partial class AddAuthenticationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalizedemail",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "normalizedusername",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill: ta sama zasada co IdentityNormalizer (trim + wielkie litery).
            migrationBuilder.Sql(
                """
                UPDATE users
                SET normalizedemail = UPPER(TRIM(email)),
                    normalizedusername = UPPER(TRIM(username));
                """);

            // Duplikaty (np. Test@Example.com i test@example.com) blokują UNIQUE index.
            // Nie scalamy ani nie usuwamy kont automatycznie — migracja przerywa się z instrukcją.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM users GROUP BY normalizedemail HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'SansPost: duplicate e-mails (case-insensitive) in users. Resolve manually: SELECT normalizedemail, array_agg(id) FROM users GROUP BY 1 HAVING COUNT(*) > 1;';
                    END IF;
                    IF EXISTS (SELECT 1 FROM users GROUP BY normalizedusername HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'SansPost: duplicate usernames (case-insensitive) in users. Resolve manually: SELECT normalizedusername, array_agg(id) FROM users GROUP BY 1 HAVING COUNT(*) > 1;';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "refreshtokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userid = table.Column<int>(type: "integer", nullable: false),
                    tokenhash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    createdat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expiresat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revokedat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    replacedbytokenid = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refreshtokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refreshtokens_refreshtokens_replacedbytokenid",
                        column: x => x.replacedbytokenid,
                        principalTable: "refreshtokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_refreshtokens_users_userid",
                        column: x => x.userid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_normalizedemail",
                table: "users",
                column: "normalizedemail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_normalizedusername",
                table: "users",
                column: "normalizedusername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refreshtokens_replacedbytokenid",
                table: "refreshtokens",
                column: "replacedbytokenid");

            migrationBuilder.CreateIndex(
                name: "IX_refreshtokens_tokenhash",
                table: "refreshtokens",
                column: "tokenhash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refreshtokens_userid",
                table: "refreshtokens",
                column: "userid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refreshtokens");

            migrationBuilder.DropIndex(
                name: "IX_users_normalizedemail",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_normalizedusername",
                table: "users");

            migrationBuilder.DropColumn(
                name: "normalizedemail",
                table: "users");

            migrationBuilder.DropColumn(
                name: "normalizedusername",
                table: "users");
        }
    }
}
