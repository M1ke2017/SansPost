using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Sprint 3: limity kolumn posts, zamknięty zestaw kategorii (CHECK), UpdatedAt, Version (optimistic concurrency)
    /// oraz indeksy pod keyset pagination. Wymaga AddAuthenticationFoundation.
    /// </summary>
    public partial class PostsCoreConcurrencyAndFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dane dłuższe niż nowe limity nie są obcinane po cichu — migracja przerywa się z instrukcją.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM posts
                               WHERE char_length(title) > 150
                                  OR char_length(content) > 10000
                                  OR char_length(imageurl) > 2048) THEN
                        RAISE EXCEPTION 'SansPost: posts exceed new length limits (title 150, content 10000, imageurl 2048). Resolve manually: SELECT id FROM posts WHERE char_length(title) > 150 OR char_length(content) > 10000 OR char_length(imageurl) > 2048;';
                    END IF;
                END $$;
                """);

            // Stare kategorie (polskie nazwy, dowolny tekst, NULL) → nowy zamknięty zestaw.
            migrationBuilder.Sql(
                """
                UPDATE posts
                SET category = CASE category
                    WHEN 'Technologia' THEN 'Technology'
                    WHEN 'Podróże' THEN 'Travel'
                    WHEN 'General' THEN 'General'
                    WHEN 'Technology' THEN 'Technology'
                    WHEN 'Games' THEN 'Games'
                    WHEN 'Travel' THEN 'Travel'
                    WHEN 'Ideas' THEN 'Ideas'
                    WHEN 'Projects' THEN 'Projects'
                    WHEN 'Feedback' THEN 'Feedback'
                    ELSE 'General'
                END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_posts_userid",
                table: "posts");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "posts",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "imageurl",
                table: "posts",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "content",
                table: "posts",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "category",
                table: "posts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "updatedat",
                table: "posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "posts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_posts_author_feed",
                table: "posts",
                columns: new[] { "userid", "createdat", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_posts_category_feed",
                table: "posts",
                columns: new[] { "category", "createdat", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_posts_feed",
                table: "posts",
                columns: new[] { "createdat", "id" },
                descending: new bool[0]);

            migrationBuilder.AddCheckConstraint(
                name: "CK_posts_category",
                table: "posts",
                sql: "category IN ('General', 'Technology', 'Games', 'Travel', 'Ideas', 'Projects', 'Feedback')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stratne: kategorie pozostają w nowym (angielskim) zestawie; stary model przyjmował dowolny tekst.
            migrationBuilder.DropIndex(
                name: "IX_posts_author_feed",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_posts_category_feed",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_posts_feed",
                table: "posts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_posts_category",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "updatedat",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "version",
                table: "posts");

            migrationBuilder.AlterColumn<string>(
                name: "title",
                table: "posts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(150)",
                oldMaxLength: 150);

            migrationBuilder.AlterColumn<string>(
                name: "imageurl",
                table: "posts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "content",
                table: "posts",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000);

            migrationBuilder.AlterColumn<string>(
                name: "category",
                table: "posts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_posts_userid",
                table: "posts",
                column: "userid");
        }
    }
}
