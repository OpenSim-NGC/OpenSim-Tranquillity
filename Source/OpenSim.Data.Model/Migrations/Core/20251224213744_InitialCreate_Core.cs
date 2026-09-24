using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSim.Data.Migrations.Core
{
    /// <inheritdoc />
    public partial class InitialCreate_Core : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgentPrefs",
                columns: table => new
                {
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    AccessPrefs = table.Column<string>(type: "char(2)", fixedLength: true, maxLength: 2, nullable: false, defaultValueSql: "'M'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    HoverHeight = table.Column<double>(type: "double(30,27)", nullable: false),
                    Language = table.Column<string>(type: "char(5)", fixedLength: true, maxLength: 5, nullable: false, defaultValueSql: "'en-us'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    LanguageIsPublic = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValueSql: "'1'"),
                    PermEveryone = table.Column<int>(type: "int", nullable: false),
                    PermGroup = table.Column<int>(type: "int", nullable: false),
                    PermNextOwner = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'532480'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.PrincipalID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    assetType = table.Column<sbyte>(type: "tinyint", nullable: false),
                    local = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    temporary = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    data = table.Column<byte[]>(type: "longblob", nullable: false),
                    create_time = table.Column<int>(type: "int", nullable: true, defaultValueSql: "'0'"),
                    access_time = table.Column<int>(type: "int", nullable: true, defaultValueSql: "'0'"),
                    asset_flags = table.Column<int>(type: "int", nullable: false),
                    CreatorID = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "auth",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    passwordHash = table.Column<string>(type: "char(32)", fixedLength: true, maxLength: 32, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    passwordSalt = table.Column<string>(type: "char(32)", fixedLength: true, maxLength: 32, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    webLoginKey = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    accountType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValueSql: "'UserAccount'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "Avatars",
                columns: table => new
                {
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Value = table.Column<string>(type: "text", nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.PrincipalID, x.Name })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "classifieds",
                columns: table => new
                {
                    classifieduuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    creatoruuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    creationdate = table.Column<int>(type: "int", nullable: false),
                    expirationdate = table.Column<int>(type: "int", nullable: false),
                    category = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    description = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    parceluuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    parentestate = table.Column<int>(type: "int", nullable: false),
                    snapshotuuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    simname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    posglobal = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    parcelname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    classifiedflags = table.Column<int>(type: "int", nullable: false),
                    priceforlisting = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.classifieduuid);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "estate_allowed_experiences",
                columns: table => new
                {
                    uuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EstateID = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estate_allowed_experiences", x => new { x.uuid, x.EstateID });
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "estate_groups",
                columns: table => new
                {
                    EstateID = table.Column<uint>(type: "int unsigned", nullable: false),
                    uuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "estate_key_experiences",
                columns: table => new
                {
                    uuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EstateID = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estate_key_experiences", x => new { x.uuid, x.EstateID });
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "estate_managers",
                columns: table => new
                {
                    EstateID = table.Column<uint>(type: "int unsigned", nullable: false),
                    uuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "estate_map",
                columns: table => new
                {
                    RegionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    EstateID = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.RegionID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "estate_settings",
                columns: table => new
                {
                    EstateID = table.Column<uint>(type: "int unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EstateName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    AbuseEmailToEstateOwner = table.Column<sbyte>(type: "tinyint", nullable: false),
                    DenyAnonymous = table.Column<sbyte>(type: "tinyint", nullable: false),
                    ResetHomeOnTeleport = table.Column<sbyte>(type: "tinyint", nullable: false),
                    FixedSun = table.Column<sbyte>(type: "tinyint", nullable: false),
                    DenyTransacted = table.Column<sbyte>(type: "tinyint", nullable: false),
                    BlockDwell = table.Column<sbyte>(type: "tinyint", nullable: false),
                    DenyIdentified = table.Column<sbyte>(type: "tinyint", nullable: false),
                    AllowVoice = table.Column<sbyte>(type: "tinyint", nullable: false),
                    UseGlobalTime = table.Column<sbyte>(type: "tinyint", nullable: false),
                    PricePerMeter = table.Column<int>(type: "int", nullable: false),
                    TaxFree = table.Column<sbyte>(type: "tinyint", nullable: false),
                    AllowDirectTeleport = table.Column<sbyte>(type: "tinyint", nullable: false),
                    RedirectGridX = table.Column<int>(type: "int", nullable: false),
                    RedirectGridY = table.Column<int>(type: "int", nullable: false),
                    ParentEstateID = table.Column<uint>(type: "int unsigned", nullable: false),
                    SunPosition = table.Column<double>(type: "double", nullable: false),
                    EstateSkipScripts = table.Column<sbyte>(type: "tinyint", nullable: false),
                    BillableFactor = table.Column<float>(type: "float", nullable: false),
                    PublicAccess = table.Column<sbyte>(type: "tinyint", nullable: false),
                    AbuseEmail = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    EstateOwner = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    DenyMinors = table.Column<sbyte>(type: "tinyint", nullable: false),
                    AllowLandmark = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    AllowParcelChanges = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    AllowSetHome = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    AllowEnviromentOverride = table.Column<sbyte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.EstateID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "estate_users",
                columns: table => new
                {
                    EstateID = table.Column<uint>(type: "int unsigned", nullable: false),
                    uuid = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "estateban",
                columns: table => new
                {
                    EstateID = table.Column<uint>(type: "int unsigned", nullable: false),
                    bannedUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedIp = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedIpHostMask = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedNameMask = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    banningUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    banTime = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "experience_kv",
                columns: table => new
                {
                    experience = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    key = table.Column<string>(type: "varchar(1011)", maxLength: 1011, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    value = table.Column<string>(type: "varchar(4095)", maxLength: 4095, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experience_kv", x => new { x.experience, x.key });
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "experience_permissions",
                columns: table => new
                {
                    experience = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    avatar = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    allow = table.Column<ulong>(type: "bit(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experience_permissions", x => new { x.experience, x.avatar });
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "experiences",
                columns: table => new
                {
                    public_id = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_id = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(42)", maxLength: 42, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    group_id = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    logo = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    marketplace = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    slurl = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    maturity = table.Column<int>(type: "int", nullable: false),
                    properties = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experiences", x => x.public_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "Friends",
                columns: table => new
                {
                    PrincipalID = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Friend = table.Column<string>(type: "varchar(255)", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Flags = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValueSql: "'0'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Offered = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValueSql: "'0'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.PrincipalID, x.Friend })
                        .Annotation("MySql:IndexPrefixLength", new[] { 36, 36 });
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "fsassets",
                columns: table => new
                {
                    id = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    description = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    type = table.Column<int>(type: "int", nullable: false),
                    hash = table.Column<string>(type: "char(80)", fixedLength: true, maxLength: 80, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    create_time = table.Column<int>(type: "int", nullable: false),
                    access_time = table.Column<int>(type: "int", nullable: false),
                    asset_flags = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "GloebitSubscriptions",
                columns: table => new
                {
                    ObjectID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    AppKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    GlbApiUrl = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    SubscriptionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ObjectName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    cTime = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.ObjectID, x.AppKey, x.GlbApiUrl })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "GloebitTransactions",
                columns: table => new
                {
                    TransactionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PayerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PayerName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PayeeID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PayeeName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Amount = table.Column<int>(type: "int", nullable: false),
                    TransactionType = table.Column<int>(type: "int", nullable: false),
                    TransactionTypeString = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    IsSubscriptionDebit = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SubscriptionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PartID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PartName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PartDescription = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    CategoryID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    SaleType = table.Column<int>(type: "int", nullable: true),
                    Submitted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ResponseReceived = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ResponseSuccess = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ResponseStatus = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    ResponseReason = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PayerEndingBalance = table.Column<int>(type: "int", nullable: false),
                    enacted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    consumed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    canceled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    cTime = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn),
                    enactedTime = table.Column<DateTime>(type: "timestamp", nullable: true),
                    finishedTime = table.Column<DateTime>(type: "timestamp", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.TransactionID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "GloebitUsers",
                columns: table => new
                {
                    AppKey = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    GloebitID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    GloebitToken = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    LastSessionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.AppKey, x.PrincipalID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "GridUser",
                columns: table => new
                {
                    UserID = table.Column<string>(type: "varchar(255)", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    HomeRegionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    HomePosition = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, defaultValueSql: "'<0,0,0>'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    HomeLookAt = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, defaultValueSql: "'<0,0,0>'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastRegionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastPosition = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, defaultValueSql: "'<0,0,0>'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastLookAt = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false, defaultValueSql: "'<0,0,0>'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Online = table.Column<string>(type: "char(5)", fixedLength: true, maxLength: 5, nullable: false, defaultValueSql: "'false'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Login = table.Column<string>(type: "char(16)", fixedLength: true, maxLength: 16, nullable: false, defaultValueSql: "'0'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Logout = table.Column<string>(type: "char(16)", fixedLength: true, maxLength: 16, nullable: false, defaultValueSql: "'0'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UserID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "hg_traveling_data",
                columns: table => new
                {
                    SessionID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    UserID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    GridExternalName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ServiceToken = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ClientIPAddress = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    MyIPAddress = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    TMStamp = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.SessionID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "im_offline",
                columns: table => new
                {
                    ID = table.Column<int>(type: "mediumint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    FromID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Message = table.Column<string>(type: "text", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TMStamp = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.ID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "inventoryfolders",
                columns: table => new
                {
                    folderID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    folderName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    agentID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parentFolderID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.folderID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "inventoryitems",
                columns: table => new
                {
                    inventoryID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    assetID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    assetType = table.Column<int>(type: "int", nullable: true),
                    inventoryName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    inventoryDescription = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    inventoryNextPermissions = table.Column<uint>(type: "int unsigned", nullable: true),
                    inventoryCurrentPermissions = table.Column<uint>(type: "int unsigned", nullable: true),
                    invType = table.Column<int>(type: "int", nullable: true),
                    creatorID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    inventoryBasePermissions = table.Column<uint>(type: "int unsigned", nullable: false),
                    inventoryEveryOnePermissions = table.Column<uint>(type: "int unsigned", nullable: false),
                    salePrice = table.Column<int>(type: "int", nullable: false),
                    saleType = table.Column<sbyte>(type: "tinyint", nullable: false),
                    creationDate = table.Column<int>(type: "int", nullable: false),
                    groupID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    groupOwned = table.Column<sbyte>(type: "tinyint", nullable: false),
                    flags = table.Column<uint>(type: "int unsigned", nullable: false),
                    avatarID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parentFolderID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    inventoryGroupPermissions = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.inventoryID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "migrations",
                columns: table => new
                {
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    version = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "MuteList",
                columns: table => new
                {
                    AgentID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    MuteID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    MuteName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    MuteType = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'1'"),
                    MuteFlags = table.Column<int>(type: "int", nullable: false),
                    Stamp = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_groups",
                columns: table => new
                {
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Location = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Name = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Charter = table.Column<string>(type: "text", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    InsigniaID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    FounderID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    MembershipFee = table.Column<int>(type: "int", nullable: false),
                    OpenEnrollment = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ShowInList = table.Column<int>(type: "int", nullable: false),
                    AllowPublish = table.Column<int>(type: "int", nullable: false),
                    MaturePublish = table.Column<int>(type: "int", nullable: false),
                    OwnerRoleID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.GroupID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_invites",
                columns: table => new
                {
                    InviteID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    RoleID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PrincipalID = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    TMStamp = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.InviteID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_membership",
                columns: table => new
                {
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PrincipalID = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    SelectedRoleID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Contribution = table.Column<int>(type: "int", nullable: false),
                    ListInProfile = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'1'"),
                    AcceptNotices = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'1'"),
                    AccessToken = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.GroupID, x.PrincipalID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_notices",
                columns: table => new
                {
                    NoticeID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    TMStamp = table.Column<uint>(type: "int unsigned", nullable: false),
                    FromName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Subject = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Message = table.Column<string>(type: "text", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    HasAttachment = table.Column<int>(type: "int", nullable: false),
                    AttachmentType = table.Column<int>(type: "int", nullable: false),
                    AttachmentName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    AttachmentItemID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    AttachmentOwnerID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.NoticeID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_principals",
                columns: table => new
                {
                    PrincipalID = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ActiveGroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.PrincipalID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_rolemembership",
                columns: table => new
                {
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    RoleID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PrincipalID = table.Column<string>(type: "varchar(255)", nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.GroupID, x.RoleID, x.PrincipalID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0, 0 });
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "os_groups_roles",
                columns: table => new
                {
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    RoleID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Title = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Powers = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.GroupID, x.RoleID })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "Presence",
                columns: table => new
                {
                    UserID = table.Column<string>(type: "varchar(255)", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    RegionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    SessionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    SecureSessionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastSeen = table.Column<DateTime>(type: "timestamp", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "regions",
                columns: table => new
                {
                    uuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionHandle = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    regionName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionRecvKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionSendKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionSecret = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionDataURI = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    serverIP = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    serverPort = table.Column<uint>(type: "int unsigned", nullable: true),
                    serverURI = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    locX = table.Column<uint>(type: "int unsigned", nullable: true),
                    locY = table.Column<uint>(type: "int unsigned", nullable: true),
                    locZ = table.Column<uint>(type: "int unsigned", nullable: true),
                    eastOverrideHandle = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    westOverrideHandle = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    southOverrideHandle = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    northOverrideHandle = table.Column<ulong>(type: "bigint unsigned", nullable: true),
                    regionAssetURI = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionAssetRecvKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionAssetSendKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUserURI = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUserRecvKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionUserSendKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    regionMapTexture = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    serverHttpPort = table.Column<int>(type: "int", nullable: true),
                    serverRemotingPort = table.Column<int>(type: "int", nullable: true),
                    owner_uuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    originUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    access = table.Column<uint>(type: "int unsigned", nullable: true, defaultValueSql: "'1'"),
                    ScopeID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    sizeX = table.Column<int>(type: "int", nullable: false),
                    sizeY = table.Column<int>(type: "int", nullable: false),
                    flags = table.Column<int>(type: "int", nullable: false),
                    last_seen = table.Column<int>(type: "int", nullable: false),
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Token = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcelMapTexture = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.uuid);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "tokens",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    token = table.Column<string>(type: "varchar(255)", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    validity = table.Column<DateTime>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    PrincipalID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ScopeID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    FirstName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Email = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ServiceURLs = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Created = table.Column<int>(type: "int", nullable: true),
                    UserLevel = table.Column<int>(type: "int", nullable: false),
                    UserFlags = table.Column<int>(type: "int", nullable: false),
                    UserTitle = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    active = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'1'")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "UserAlias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    AliasID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    UserID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Description = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "userdata",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    TagId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    DataKey = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    DataVal = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.UserId, x.TagId })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "usernotes",
                columns: table => new
                {
                    useruuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    targetuuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    notes = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "userpicks",
                columns: table => new
                {
                    pickuuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    creatoruuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    toppick = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    parceluuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    description = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    snapshotuuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    user = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    originalname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    simname = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    posglobal = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    sortorder = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    gatekeeper = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.pickuuid);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "userprofile",
                columns: table => new
                {
                    useruuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profilePartner = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileAllowPublish = table.Column<byte[]>(type: "binary(1)", fixedLength: true, maxLength: 1, nullable: false),
                    profileMaturePublish = table.Column<byte[]>(type: "binary(1)", fixedLength: true, maxLength: 1, nullable: false),
                    profileURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileWantToMask = table.Column<int>(type: "int", nullable: false),
                    profileWantToText = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileSkillsMask = table.Column<int>(type: "int", nullable: false),
                    profileSkillsText = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileLanguages = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileImage = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileAboutText = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileFirstImage = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    profileFirstText = table.Column<string>(type: "text", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.useruuid);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "usersettings",
                columns: table => new
                {
                    useruuid = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    imviaemail = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    visible = table.Column<string>(type: "enum('true','false')", nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.useruuid);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateIndex(
                name: "PrincipalID",
                table: "AgentPrefs",
                column: "PrincipalID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "PrincipalID1",
                table: "Avatars",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "EstateID",
                table: "estate_groups",
                column: "EstateID");

            migrationBuilder.CreateIndex(
                name: "EstateID",
                table: "estate_managers",
                column: "EstateID");

            migrationBuilder.CreateIndex(
                name: "EstateID",
                table: "estate_map",
                column: "EstateID");

            migrationBuilder.CreateIndex(
                name: "EstateID",
                table: "estate_users",
                column: "EstateID");

            migrationBuilder.CreateIndex(
                name: "estateban_EstateID",
                table: "estateban",
                column: "EstateID");

            migrationBuilder.CreateIndex(
                name: "PrincipalID2",
                table: "Friends",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "idx_fsassets_access_time",
                table: "fsassets",
                column: "access_time");

            migrationBuilder.CreateIndex(
                name: "ix_cts",
                table: "GloebitSubscriptions",
                column: "cTime");

            migrationBuilder.CreateIndex(
                name: "ix_oid",
                table: "GloebitSubscriptions",
                column: "ObjectID");

            migrationBuilder.CreateIndex(
                name: "ix_sid",
                table: "GloebitSubscriptions",
                column: "SubscriptionID");

            migrationBuilder.CreateIndex(
                name: "k_sub_api",
                table: "GloebitSubscriptions",
                columns: new[] { "SubscriptionID", "GlbApiUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cts1",
                table: "GloebitTransactions",
                column: "cTime");

            migrationBuilder.CreateIndex(
                name: "ix_payeeid",
                table: "GloebitTransactions",
                column: "PayeeID");

            migrationBuilder.CreateIndex(
                name: "ix_payerid",
                table: "GloebitTransactions",
                column: "PayerID");

            migrationBuilder.CreateIndex(
                name: "ix_pid",
                table: "GloebitTransactions",
                column: "PartID");

            migrationBuilder.CreateIndex(
                name: "ix_sid1",
                table: "GloebitTransactions",
                column: "SubscriptionID");

            migrationBuilder.CreateIndex(
                name: "ix_tt",
                table: "GloebitTransactions",
                column: "TransactionType");

            migrationBuilder.CreateIndex(
                name: "ix_gu_pid",
                table: "GloebitUsers",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "UserID",
                table: "hg_traveling_data",
                column: "UserID");

            migrationBuilder.CreateIndex(
                name: "FromID",
                table: "im_offline",
                column: "FromID");

            migrationBuilder.CreateIndex(
                name: "PrincipalID3",
                table: "im_offline",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "inventoryfolders_agentid",
                table: "inventoryfolders",
                column: "agentID");

            migrationBuilder.CreateIndex(
                name: "inventoryfolders_parentFolderid",
                table: "inventoryfolders",
                column: "parentFolderID");

            migrationBuilder.CreateIndex(
                name: "inventoryitems_avatarid",
                table: "inventoryitems",
                column: "avatarID");

            migrationBuilder.CreateIndex(
                name: "inventoryitems_parentFolderid",
                table: "inventoryitems",
                column: "parentFolderID");

            migrationBuilder.CreateIndex(
                name: "AgentID",
                table: "MuteList",
                column: "AgentID");

            migrationBuilder.CreateIndex(
                name: "AgentID_2",
                table: "MuteList",
                columns: new[] { "AgentID", "MuteID", "MuteName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Name",
                table: "os_groups_groups",
                column: "Name",
                unique: true)
                .Annotation("MySql:FullTextIndex", true);

            migrationBuilder.CreateIndex(
                name: "PrincipalGroup",
                table: "os_groups_invites",
                columns: new[] { "GroupID", "PrincipalID" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "PrincipalID4",
                table: "os_groups_membership",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "GroupID",
                table: "os_groups_notices",
                column: "GroupID");

            migrationBuilder.CreateIndex(
                name: "TMStamp",
                table: "os_groups_notices",
                column: "TMStamp");

            migrationBuilder.CreateIndex(
                name: "PrincipalID5",
                table: "os_groups_rolemembership",
                column: "PrincipalID");

            migrationBuilder.CreateIndex(
                name: "GroupID1",
                table: "os_groups_roles",
                column: "GroupID");

            migrationBuilder.CreateIndex(
                name: "RegionID",
                table: "Presence",
                column: "RegionID");

            migrationBuilder.CreateIndex(
                name: "SessionID",
                table: "Presence",
                column: "SessionID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserID",
                table: "Presence",
                column: "UserID");

            migrationBuilder.CreateIndex(
                name: "flags",
                table: "regions",
                column: "flags");

            migrationBuilder.CreateIndex(
                name: "overrideHandles",
                table: "regions",
                columns: new[] { "eastOverrideHandle", "westOverrideHandle", "southOverrideHandle", "northOverrideHandle" });

            migrationBuilder.CreateIndex(
                name: "regionHandle",
                table: "regions",
                column: "regionHandle");

            migrationBuilder.CreateIndex(
                name: "regionName",
                table: "regions",
                column: "regionName");

            migrationBuilder.CreateIndex(
                name: "ScopeID",
                table: "regions",
                column: "ScopeID");

            migrationBuilder.CreateIndex(
                name: "token",
                table: "tokens",
                column: "token");

            migrationBuilder.CreateIndex(
                name: "UUID",
                table: "tokens",
                column: "UUID");

            migrationBuilder.CreateIndex(
                name: "uuid_token",
                table: "tokens",
                columns: new[] { "UUID", "token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "validity",
                table: "tokens",
                column: "validity");

            migrationBuilder.CreateIndex(
                name: "Email",
                table: "UserAccounts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "FirstName",
                table: "UserAccounts",
                column: "FirstName");

            migrationBuilder.CreateIndex(
                name: "LastName",
                table: "UserAccounts",
                column: "LastName");

            migrationBuilder.CreateIndex(
                name: "Name",
                table: "UserAccounts",
                columns: new[] { "FirstName", "LastName" });

            migrationBuilder.CreateIndex(
                name: "PrincipalID",
                table: "UserAccounts",
                column: "PrincipalID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "AliasID",
                table: "UserAlias",
                column: "AliasID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Id",
                table: "UserAlias",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserID",
                table: "UserAlias",
                column: "UserID");

            migrationBuilder.CreateIndex(
                name: "useruuid",
                table: "usernotes",
                columns: new[] { "useruuid", "targetuuid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPrefs");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "auth");

            migrationBuilder.DropTable(
                name: "Avatars");

            migrationBuilder.DropTable(
                name: "classifieds");

            migrationBuilder.DropTable(
                name: "estate_allowed_experiences");

            migrationBuilder.DropTable(
                name: "estate_groups");

            migrationBuilder.DropTable(
                name: "estate_key_experiences");

            migrationBuilder.DropTable(
                name: "estate_managers");

            migrationBuilder.DropTable(
                name: "estate_map");

            migrationBuilder.DropTable(
                name: "estate_settings");

            migrationBuilder.DropTable(
                name: "estate_users");

            migrationBuilder.DropTable(
                name: "estateban");

            migrationBuilder.DropTable(
                name: "experience_kv");

            migrationBuilder.DropTable(
                name: "experience_permissions");

            migrationBuilder.DropTable(
                name: "experiences");

            migrationBuilder.DropTable(
                name: "Friends");

            migrationBuilder.DropTable(
                name: "fsassets");

            migrationBuilder.DropTable(
                name: "GloebitSubscriptions");

            migrationBuilder.DropTable(
                name: "GloebitTransactions");

            migrationBuilder.DropTable(
                name: "GloebitUsers");

            migrationBuilder.DropTable(
                name: "GridUser");

            migrationBuilder.DropTable(
                name: "hg_traveling_data");

            migrationBuilder.DropTable(
                name: "im_offline");

            migrationBuilder.DropTable(
                name: "inventoryfolders");

            migrationBuilder.DropTable(
                name: "inventoryitems");

            migrationBuilder.DropTable(
                name: "migrations");

            migrationBuilder.DropTable(
                name: "MuteList");

            migrationBuilder.DropTable(
                name: "os_groups_groups");

            migrationBuilder.DropTable(
                name: "os_groups_invites");

            migrationBuilder.DropTable(
                name: "os_groups_membership");

            migrationBuilder.DropTable(
                name: "os_groups_notices");

            migrationBuilder.DropTable(
                name: "os_groups_principals");

            migrationBuilder.DropTable(
                name: "os_groups_rolemembership");

            migrationBuilder.DropTable(
                name: "os_groups_roles");

            migrationBuilder.DropTable(
                name: "Presence");

            migrationBuilder.DropTable(
                name: "regions");

            migrationBuilder.DropTable(
                name: "tokens");

            migrationBuilder.DropTable(
                name: "UserAccounts");

            migrationBuilder.DropTable(
                name: "UserAlias");

            migrationBuilder.DropTable(
                name: "userdata");

            migrationBuilder.DropTable(
                name: "usernotes");

            migrationBuilder.DropTable(
                name: "userpicks");

            migrationBuilder.DropTable(
                name: "userprofile");

            migrationBuilder.DropTable(
                name: "usersettings");
        }
    }
}
