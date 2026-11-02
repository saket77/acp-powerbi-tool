using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Acp.Portal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Existing rows would otherwise all share the empty default, which is a stamp that
            // can never differ from anyone else's. There are no rows yet, but a migration that
            // is only correct on an empty table is a trap for whoever runs it next.
            migrationBuilder.Sql(
                "UPDATE [Users] SET [SecurityStamp] = LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '')) "
                + "WHERE [SecurityStamp] = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");
        }
    }
}
