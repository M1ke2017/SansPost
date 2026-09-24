using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SansPost.Migrations
{
    /// <summary>
    /// Sprint 6: status konta + AuthVersion, soft state treści (Published/Hidden/Deleted), zgłoszenia z częściowym
    /// UNIQUE, append-only audyt moderacji (trigger), brama rejestracji (jeden wiersz) oraz częściowe indeksy feedu.
    /// Istniejące dane: users → Active (AuthVersion 1), posts/comments → Published.
    /// </summary>
    public partial class DemoSafetyAndModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_posts_category_feed",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_posts_feed",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_comments_author",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_comments_post_thread",
                table: "comments");

            migrationBuilder.AddColumn<int>(
                name: "authversion",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTime>(
                name: "deletedat",
                table: "posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "posts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Published");

            migrationBuilder.AddColumn<DateTime>(
                name: "deletedat",
                table: "comments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "comments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Published");

            migrationBuilder.CreateTable(
                name: "moderationactions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    adminuserid = table.Column<int>(type: "integer", nullable: false),
                    actiontype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    targettype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    targetid = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    createdat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderationactions", x => x.id);
                    table.CheckConstraint("CK_moderationactions_actiontype", "actiontype IN ('HidePost', 'RestorePost', 'HideComment', 'RestoreComment', 'SuspendUser', 'BanUser', 'ReactivateUser', 'DismissReport', 'ChangeRole', 'ChangeSubscription')");
                    table.CheckConstraint("CK_moderationactions_targettype", "targettype IN ('Post', 'Comment', 'User', 'Report')");
                    table.ForeignKey(
                        name: "FK_moderationactions_users_adminuserid",
                        column: x => x.adminuserid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "registrationgates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registrationgates", x => x.id);
                    table.CheckConstraint("CK_registrationgate_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reporteruserid = table.Column<int>(type: "integer", nullable: false),
                    targettype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    targetid = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    createdat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reviewedbyuserid = table.Column<int>(type: "integer", nullable: true),
                    reviewedat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.id);
                    table.CheckConstraint("CK_reports_reason", "reason IN ('Spam', 'Abuse', 'Harassment', 'InappropriateContent', 'Other')");
                    table.CheckConstraint("CK_reports_status", "status IN ('Pending', 'Resolved', 'Dismissed')");
                    table.CheckConstraint("CK_reports_targettype", "targettype IN ('Post', 'Comment')");
                    table.ForeignKey(
                        name: "FK_reports_users_reporteruserid",
                        column: x => x.reporteruserid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reports_users_reviewedbyuserid",
                        column: x => x.reviewedbyuserid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "registrationgates",
                column: "id",
                value: 1);

            migrationBuilder.AddCheckConstraint(
                name: "CK_users_status",
                table: "users",
                sql: "status IN ('Active', 'Suspended', 'Banned')");

            migrationBuilder.CreateIndex(
                name: "IX_posts_category_feed",
                table: "posts",
                columns: new[] { "category", "createdat", "id" },
                descending: new[] { false, true, true },
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_posts_feed",
                table: "posts",
                columns: new[] { "createdat", "id" },
                descending: new bool[0],
                filter: "status = 'Published'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_posts_status",
                table: "posts",
                sql: "status IN ('Published', 'Hidden', 'Deleted')");

            migrationBuilder.CreateIndex(
                name: "IX_comments_author",
                table: "comments",
                columns: new[] { "userid", "createdat", "id" },
                descending: new[] { false, true, true },
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_comments_post_thread",
                table: "comments",
                columns: new[] { "postid", "createdat", "id" },
                filter: "status = 'Published'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_comments_status",
                table: "comments",
                sql: "status IN ('Published', 'Hidden', 'Deleted')");

            migrationBuilder.CreateIndex(
                name: "IX_moderationactions_adminuserid",
                table: "moderationactions",
                column: "adminuserid");

            migrationBuilder.CreateIndex(
                name: "IX_moderationactions_history",
                table: "moderationactions",
                columns: new[] { "createdat", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_reports_pending_queue",
                table: "reports",
                columns: new[] { "createdat", "id" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_reports_pending_target",
                table: "reports",
                columns: new[] { "targettype", "targetid" },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_reports_reviewedbyuserid",
                table: "reports",
                column: "reviewedbyuserid");

            migrationBuilder.CreateIndex(
                name: "UX_reports_pending_per_reporter",
                table: "reports",
                columns: new[] { "reporteruserid", "targettype", "targetid" },
                unique: true,
                filter: "status = 'Pending'");

            // Audyt moderacji jest append-only także na poziomie bazy — UPDATE/DELETE odrzucane niezależnie od aplikacji.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION sanspost_moderationactions_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'moderationactions is append-only (% not allowed)', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER moderationactions_append_only
                BEFORE UPDATE OR DELETE ON moderationactions
                FOR EACH ROW EXECUTE FUNCTION sanspost_moderationactions_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stratne: soft state treści i statusy kont znikają (ukryte/usunięte posty wróciłyby do publicznych odczytów
            // starego kodu), zgłoszenia i audyt są usuwane.
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS moderationactions_append_only ON moderationactions;
                DROP FUNCTION IF EXISTS sanspost_moderationactions_append_only();
                """);

            migrationBuilder.DropTable(
                name: "moderationactions");

            migrationBuilder.DropTable(
                name: "registrationgates");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_users_status",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_posts_category_feed",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_posts_feed",
                table: "posts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_posts_status",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "IX_comments_author",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_comments_post_thread",
                table: "comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_comments_status",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "authversion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "status",
                table: "users");

            migrationBuilder.DropColumn(
                name: "deletedat",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "status",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "deletedat",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "status",
                table: "comments");

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
    }
}
