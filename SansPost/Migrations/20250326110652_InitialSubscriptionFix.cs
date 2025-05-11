using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SansPost.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubscriptionFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscriptions_userid",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "subscriptiontype",
                table: "users");

            migrationBuilder.Sql(
            "ALTER TABLE subscriptions ALTER COLUMN type TYPE integer USING type::integer;");


            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_userid",
                table: "subscriptions",
                column: "userid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscriptions_userid",
                table: "subscriptions");

            migrationBuilder.AddColumn<int>(
                name: "subscriptiontype",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "subscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_userid",
                table: "subscriptions",
                column: "userid");
        }
    }
}
