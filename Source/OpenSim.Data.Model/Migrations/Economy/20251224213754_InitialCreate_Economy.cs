using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSim.Data.Migrations.Economy
{
    /// <inheritdoc />
    public partial class InitialCreate_Economy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "balances",
                columns: table => new
                {
                    user = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    balance = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<sbyte>(type: "tinyint", nullable: true),
                    type = table.Column<sbyte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.user);
                },
                comment: "Rev.4")
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "totalsales",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    user = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    objectUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    type = table.Column<int>(type: "int", nullable: false),
                    TotalCount = table.Column<int>(type: "int", nullable: false),
                    TotalAmount = table.Column<int>(type: "int", nullable: false),
                    time = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                },
                comment: "Rev.3")
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    sender = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    receiver = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    amount = table.Column<int>(type: "int", nullable: false),
                    senderBalance = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'-1'"),
                    receiverBalance = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'-1'"),
                    objectUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    objectName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionHandle = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    type = table.Column<int>(type: "int", nullable: false),
                    time = table.Column<int>(type: "int", nullable: false),
                    secure = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    status = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    commonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                },
                comment: "Rev.12")
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "userinfo",
                columns: table => new
                {
                    user = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    simip = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    avatar = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    pass = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    type = table.Column<sbyte>(type: "tinyint", nullable: false),
                    @class = table.Column<sbyte>(name: "class", type: "tinyint", nullable: false),
                    serverurl = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.user);
                },
                comment: "Rev.3")
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "balances");

            migrationBuilder.DropTable(
                name: "totalsales");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "userinfo");
        }
    }
}
