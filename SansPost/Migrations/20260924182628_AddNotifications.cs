using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SansPost.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userid = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actoruserid = table.Column<int>(type: "integer", nullable: false),
                    postid = table.Column<int>(type: "integer", nullable: false),
                    commentid = table.Column<int>(type: "integer", nullable: true),
                    createdat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    readat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.CheckConstraint("CK_notifications_type", "type IN ('CommentOnPost')");
                    table.ForeignKey(
                        name: "FK_notifications_comments_commentid",
                        column: x => x.commentid,
                        principalTable: "comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notifications_posts_postid",
                        column: x => x.postid,
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notifications_users_actoruserid",
                        column: x => x.actoruserid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notifications_users_userid",
                        column: x => x.userid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_actoruserid",
                table: "notifications",
                column: "actoruserid");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_commentid",
                table: "notifications",
                column: "commentid");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_postid",
                table: "notifications",
                column: "postid");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_unread",
                table: "notifications",
                column: "userid",
                filter: "readat IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_user_feed",
                table: "notifications",
                columns: new[] { "userid", "createdat", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
