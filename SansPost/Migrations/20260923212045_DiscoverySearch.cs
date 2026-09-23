using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Sprint 5: PostgreSQL full-text search (generowana kolumna tsvector 'simple' z wagami A/B + GIN)
    /// oraz indeks text_pattern_ops dla prefiksowego wyszukiwania autorów. Istniejące posty są indeksowane
    /// automatycznie — ADD COLUMN ... GENERATED STORED wylicza wartość dla wszystkich wierszy.
    /// </summary>
    public partial class DiscoverySearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "searchvector",
                table: "posts",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', coalesce(title, '')), 'A') || setweight(to_tsvector('simple', coalesce(content, '')), 'B')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username_prefix",
                table: "users",
                column: "normalizedusername")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_posts_search",
                table: "posts",
                column: "searchvector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_username_prefix",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_posts_search",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "searchvector",
                table: "posts");
        }
    }
}
