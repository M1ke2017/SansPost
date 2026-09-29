using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SansPost.Migrations
{
    /// <inheritdoc />
    public partial class DuelResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duelresults",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    duelid = table.Column<Guid>(type: "uuid", nullable: false),
                    playeroneid = table.Column<int>(type: "integer", nullable: false),
                    playertwoid = table.Column<int>(type: "integer", nullable: false),
                    winnerid = table.Column<int>(type: "integer", nullable: true),
                    resulttype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    roundcount = table.Column<int>(type: "integer", nullable: false),
                    finishedat = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finishreason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duelresults", x => x.id);
                    table.CheckConstraint("CK_duelresults_finishreason", "finishreason IN ('Knockout', 'RoundLimit', 'Surrender', 'Disconnect')");
                    table.CheckConstraint("CK_duelresults_players", "playeroneid <> playertwoid");
                    table.CheckConstraint("CK_duelresults_resulttype", "resulttype IN ('Win', 'Draw')");
                    table.CheckConstraint("CK_duelresults_rounds", "roundcount >= 0");
                    table.CheckConstraint("CK_duelresults_winner", "(resulttype = 'Draw' AND winnerid IS NULL) OR (resulttype = 'Win' AND winnerid IN (playeroneid, playertwoid))");
                    table.ForeignKey(
                        name: "FK_duelresults_users_playeroneid",
                        column: x => x.playeroneid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_duelresults_users_playertwoid",
                        column: x => x.playertwoid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_duelresults_users_winnerid",
                        column: x => x.winnerid,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duelresults_playerone",
                table: "duelresults",
                column: "playeroneid");

            migrationBuilder.CreateIndex(
                name: "IX_duelresults_playertwo",
                table: "duelresults",
                column: "playertwoid");

            migrationBuilder.CreateIndex(
                name: "IX_duelresults_winner",
                table: "duelresults",
                column: "winnerid");

            migrationBuilder.CreateIndex(
                name: "UX_duelresults_duel",
                table: "duelresults",
                column: "duelid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duelresults");
        }
    }
}
