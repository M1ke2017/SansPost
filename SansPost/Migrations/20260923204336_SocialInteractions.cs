using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Sprint 4: komentarze z limitem treści, UpdatedAt i Version (optimistic concurrency);
    /// polubienia z CreatedAt i UNIQUE(postid, userid); indeksy wątku komentarzy i aktywności autora.
    /// </summary>
    public partial class SocialInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Komentarze dłuższe niż nowy limit nie są obcinane po cichu — migracja przerywa się z instrukcją.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM comments WHERE char_length(content) > 2000) THEN
                        RAISE EXCEPTION 'SansPost: comments exceed new length limit (2000). Resolve manually: SELECT id FROM comments WHERE char_length(content) > 2000;';
                    END IF;
                END $$;
                """);

            // Duplikaty polubień (stary toggle bez constraintu) blokowałyby UNIQUE. Duplikat nie niesie informacji —
            // zostaje najstarszy rekord każdej pary (postid, userid).
            migrationBuilder.Sql(
                """
                DELETE FROM likes l
                USING likes older
                WHERE l.postid = older.postid
                  AND l.userid = older.userid
                  AND l.id > older.id;
                """);

            migrationBuilder.DropIndex(
                name: "IX_likes_postid",
                table: "likes");

            migrationBuilder.DropIndex(
                name: "IX_comments_postid",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_comments_userid",
                table: "comments");

            migrationBuilder.AddColumn<DateTime>(
                name: "createdat",
                table: "likes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()"); // istniejące polubienia: czas migracji (dokładny czas nieznany)

            migrationBuilder.AlterColumn<string>(
                name: "content",
                table: "comments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTime>(
                name: "updatedat",
                table: "comments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "comments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "UX_likes_post_user",
                table: "likes",
                columns: new[] { "postid", "userid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comments_author",
                table: "comments",
                columns: new[] { "userid", "createdat", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_comments_post_thread",
                table: "comments",
                columns: new[] { "postid", "createdat", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_likes_post_user",
                table: "likes");

            migrationBuilder.DropIndex(
                name: "IX_comments_author",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_comments_post_thread",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "createdat",
                table: "likes");

            migrationBuilder.DropColumn(
                name: "updatedat",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "version",
                table: "comments");

            migrationBuilder.AlterColumn<string>(
                name: "content",
                table: "comments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.CreateIndex(
                name: "IX_likes_postid",
                table: "likes",
                column: "postid");

            migrationBuilder.CreateIndex(
                name: "IX_comments_postid",
                table: "comments",
                column: "postid");

            migrationBuilder.CreateIndex(
                name: "IX_comments_userid",
                table: "comments",
                column: "userid");
        }
    }
}
