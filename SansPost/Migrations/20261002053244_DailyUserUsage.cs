using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SansPost.Migrations
{
    /// <inheritdoc />
    public partial class DailyUserUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dailyuserusages",
                columns: table => new
                {
                    userid = table.Column<int>(type: "integer", nullable: false),
                    dateutc = table.Column<DateOnly>(type: "date", nullable: false),
                    postscreated = table.Column<int>(type: "integer", nullable: false),
                    commentscreated = table.Column<int>(type: "integer", nullable: false),
                    gamesstarted = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dailyuserusages", x => new { x.userid, x.dateutc });
                    table.CheckConstraint("CK_dailyuserusages_counts", "postscreated >= 0 AND commentscreated >= 0 AND gamesstarted >= 0");
                    table.ForeignKey(
                        name: "FK_dailyuserusages_users_userid",
                        column: x => x.userid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dailyuserusages");
        }
    }
}
