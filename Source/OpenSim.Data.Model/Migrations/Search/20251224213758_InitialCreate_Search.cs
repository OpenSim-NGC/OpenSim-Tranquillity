using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSim.Data.Migrations.Search
{
    /// <inheritdoc />
    public partial class InitialCreate_Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "allparcels",
                columns: table => new
                {
                    parcelUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    ownerUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    groupUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    landingpoint = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    infoUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelarea = table.Column<int>(type: "int", nullable: false),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.parcelUUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "classifieds",
                columns: table => new
                {
                    classifieduuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    creatoruuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    creationdate = table.Column<int>(type: "int", nullable: false),
                    expirationdate = table.Column<int>(type: "int", nullable: false),
                    category = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "text", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parceluuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parentestate = table.Column<int>(type: "int", nullable: false),
                    snapshotuuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    simname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    posglobal = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    classifiedflags = table.Column<int>(type: "int", nullable: false),
                    priceforlisting = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.classifieduuid);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    eventid = table.Column<uint>(type: "int unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    owneruuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    creatoruuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    category = table.Column<int>(type: "int", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    dateUTC = table.Column<int>(type: "int", nullable: false),
                    duration = table.Column<int>(type: "int", nullable: false),
                    covercharge = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    coveramount = table.Column<int>(type: "int", nullable: false),
                    simname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    globalPos = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    eventflags = table.Column<int>(type: "int", nullable: false),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.eventid);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "hostsregister",
                columns: table => new
                {
                    host = table.Column<string>(type: "varchar(255)", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    port = table.Column<int>(type: "int", nullable: false),
                    register = table.Column<int>(type: "int", nullable: false),
                    nextcheck = table.Column<int>(type: "int", nullable: false),
                    @checked = table.Column<bool>(name: "checked", type: "tinyint(1)", nullable: false),
                    failcounter = table.Column<int>(type: "int", nullable: false),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.host, x.port })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "objects",
                columns: table => new
                {
                    objectuuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parceluuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    location = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionuuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.objectuuid, x.parceluuid })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "parcels",
                columns: table => new
                {
                    parcelUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelname = table.Column<string>(type: "varchar(255)", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    landingpoint = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "varchar(255)", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    searchcategory = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    build = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    script = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    @public = table.Column<string>(name: "public", type: "enum('true','false')", nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    dwell = table.Column<float>(type: "float", nullable: false),
                    infouuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "''", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    mature = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false, defaultValueSql: "'PG'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    imageUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.regionUUID, x.parcelUUID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "parcelsales",
                columns: table => new
                {
                    regionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    area = table.Column<int>(type: "int", nullable: false),
                    saleprice = table.Column<int>(type: "int", nullable: false),
                    landingpoint = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    infoUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    dwell = table.Column<int>(type: "int", nullable: false),
                    parentestate = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'1'"),
                    mature = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false, defaultValueSql: "'PG'", collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.regionUUID, x.parcelUUID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "popularplaces",
                columns: table => new
                {
                    parcelUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    dwell = table.Column<float>(type: "float", nullable: false),
                    infoUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    has_picture = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    mature = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.parcelUUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    regionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionhandle = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    url = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    owner = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    owneruuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    gatekeeperURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.regionUUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "regionUUID",
                table: "allparcels",
                column: "regionUUID");

            migrationBuilder.CreateIndex(
                name: "description",
                table: "parcels",
                column: "description");

            migrationBuilder.CreateIndex(
                name: "dwell",
                table: "parcels",
                column: "dwell");

            migrationBuilder.CreateIndex(
                name: "name",
                table: "parcels",
                column: "parcelname");

            migrationBuilder.CreateIndex(
                name: "searchcategory",
                table: "parcels",
                column: "searchcategory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allparcels");

            migrationBuilder.DropTable(
                name: "classifieds");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "hostsregister");

            migrationBuilder.DropTable(
                name: "objects");

            migrationBuilder.DropTable(
                name: "parcels");

            migrationBuilder.DropTable(
                name: "parcelsales");

            migrationBuilder.DropTable(
                name: "popularplaces");

            migrationBuilder.DropTable(
                name: "regions");
        }
    }
}
