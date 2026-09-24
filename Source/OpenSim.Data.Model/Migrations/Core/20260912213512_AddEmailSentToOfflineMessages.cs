using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSim.Data.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddEmailSentToOfflineMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailSent",
                table: "im_offline",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailSent",
                table: "im_offline");
        }
    }
}
