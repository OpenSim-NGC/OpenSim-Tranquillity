using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSim.Data.Migrations.Region
{
    /// <inheritdoc />
    public partial class InitialCreate_Region : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bakedterrain",
                columns: table => new
                {
                    RegionUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Revision = table.Column<int>(type: "int", nullable: true),
                    Heightfield = table.Column<byte[]>(type: "longblob", nullable: true)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "land",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "varchar(255)", nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    RegionUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    LocalLandID = table.Column<int>(type: "int", nullable: true),
                    Bitmap = table.Column<byte[]>(type: "longblob", nullable: true),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    OwnerUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    IsGroupOwned = table.Column<int>(type: "int", nullable: true),
                    Area = table.Column<int>(type: "int", nullable: true),
                    AuctionID = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<int>(type: "int", nullable: true),
                    ClaimDate = table.Column<int>(type: "int", nullable: true),
                    ClaimPrice = table.Column<int>(type: "int", nullable: true),
                    GroupUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    SalePrice = table.Column<int>(type: "int", nullable: true),
                    LandStatus = table.Column<int>(type: "int", nullable: true),
                    LandFlags = table.Column<uint>(type: "int unsigned", nullable: true),
                    LandingType = table.Column<int>(type: "int", nullable: true),
                    MediaAutoScale = table.Column<int>(type: "int", nullable: true),
                    MediaTextureUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    MediaURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    MusicURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    PassHours = table.Column<float>(type: "float", nullable: true),
                    PassPrice = table.Column<int>(type: "int", nullable: true),
                    SnapshotUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    UserLocationX = table.Column<float>(type: "float", nullable: true),
                    UserLocationY = table.Column<float>(type: "float", nullable: true),
                    UserLocationZ = table.Column<float>(type: "float", nullable: true),
                    UserLookAtX = table.Column<float>(type: "float", nullable: true),
                    UserLookAtY = table.Column<float>(type: "float", nullable: true),
                    UserLookAtZ = table.Column<float>(type: "float", nullable: true),
                    AuthbuyerID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    OtherCleanTime = table.Column<int>(type: "int", nullable: false),
                    Dwell = table.Column<int>(type: "int", nullable: false),
                    MediaType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValueSql: "'none/none'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    MediaDescription = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    MediaSize = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValueSql: "'0,0'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    MediaLoop = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ObscureMusic = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ObscureMedia = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SeeAVs = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    AnyAVSounds = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    GroupAVSounds = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'1'"),
                    environment = table.Column<string>(type: "mediumtext", nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "landaccesslist",
                columns: table => new
                {
                    LandUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    AccessUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Flags = table.Column<int>(type: "int", nullable: true),
                    Expires = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

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
                name: "primitems",
                columns: table => new
                {
                    itemID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    invType = table.Column<int>(type: "int", nullable: true),
                    assetType = table.Column<int>(type: "int", nullable: true),
                    name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    creationDate = table.Column<long>(type: "bigint", nullable: true),
                    nextPermissions = table.Column<int>(type: "int", nullable: true),
                    currentPermissions = table.Column<int>(type: "int", nullable: true),
                    basePermissions = table.Column<int>(type: "int", nullable: true),
                    everyonePermissions = table.Column<int>(type: "int", nullable: true),
                    groupPermissions = table.Column<int>(type: "int", nullable: true),
                    flags = table.Column<int>(type: "int", nullable: false),
                    primID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    assetID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    parentFolderID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    CreatorID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    ownerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    groupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    lastOwnerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.itemID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "prims",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    CreationDate = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Text = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    SitName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TouchName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    ObjectFlags = table.Column<int>(type: "int", nullable: true),
                    OwnerMask = table.Column<int>(type: "int", nullable: true),
                    NextOwnerMask = table.Column<int>(type: "int", nullable: true),
                    GroupMask = table.Column<int>(type: "int", nullable: true),
                    EveryoneMask = table.Column<int>(type: "int", nullable: true),
                    BaseMask = table.Column<int>(type: "int", nullable: true),
                    PositionX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    PositionY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    PositionZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    GroupPositionX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    GroupPositionY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    GroupPositionZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    VelocityX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    VelocityY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    VelocityZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AngularVelocityX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AngularVelocityY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AngularVelocityZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AccelerationX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AccelerationY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AccelerationZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    RotationX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    RotationY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    RotationZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    RotationW = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOffsetX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOffsetY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOffsetZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOrientW = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOrientX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOrientY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    SitTargetOrientZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    RegionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    CreatorID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    OwnerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    GroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastOwnerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    SceneGroupID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PayPrice = table.Column<int>(type: "int", nullable: false),
                    PayButton1 = table.Column<int>(type: "int", nullable: false),
                    PayButton2 = table.Column<int>(type: "int", nullable: false),
                    PayButton3 = table.Column<int>(type: "int", nullable: false),
                    PayButton4 = table.Column<int>(type: "int", nullable: false),
                    LoopedSound = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LoopedSoundGain = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    TextureAnimation = table.Column<byte[]>(type: "blob", nullable: true),
                    OmegaX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    OmegaY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    OmegaZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraEyeOffsetX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraEyeOffsetY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraEyeOffsetZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraAtOffsetX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraAtOffsetY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    CameraAtOffsetZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    ForceMouselook = table.Column<sbyte>(type: "tinyint", nullable: false),
                    ScriptAccessPin = table.Column<int>(type: "int", nullable: false),
                    AllowedDrop = table.Column<sbyte>(type: "tinyint", nullable: false),
                    DieAtEdge = table.Column<sbyte>(type: "tinyint", nullable: false),
                    SalePrice = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'10'"),
                    SaleType = table.Column<sbyte>(type: "tinyint", nullable: false),
                    ColorR = table.Column<int>(type: "int", nullable: false),
                    ColorG = table.Column<int>(type: "int", nullable: false),
                    ColorB = table.Column<int>(type: "int", nullable: false),
                    ColorA = table.Column<int>(type: "int", nullable: false),
                    ParticleSystem = table.Column<byte[]>(type: "blob", nullable: true),
                    ClickAction = table.Column<sbyte>(type: "tinyint", nullable: false),
                    Material = table.Column<sbyte>(type: "tinyint", nullable: false, defaultValueSql: "'3'"),
                    CollisionSound = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    CollisionSoundVolume = table.Column<double>(type: "double", nullable: false),
                    LinkNumber = table.Column<int>(type: "int", nullable: false),
                    PassTouches = table.Column<sbyte>(type: "tinyint", nullable: false),
                    MediaURL = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    DynAttrs = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PhysicsShapeType = table.Column<sbyte>(type: "tinyint", nullable: false),
                    Density = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'1000'"),
                    GravityModifier = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'1'"),
                    Friction = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0.6'"),
                    Restitution = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0.5'"),
                    KeyframeMotion = table.Column<byte[]>(type: "blob", nullable: true),
                    AttachedPosX = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AttachedPosY = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    AttachedPosZ = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    PassCollisions = table.Column<sbyte>(type: "tinyint", nullable: false),
                    Vehicle = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    RotationAxisLocks = table.Column<sbyte>(type: "tinyint", nullable: false),
                    RezzerID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    PhysInertia = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    sopanims = table.Column<byte[]>(type: "blob", nullable: true),
                    standtargetx = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    standtargety = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    standtargetz = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    sitactrange = table.Column<float>(type: "float", nullable: false, defaultValueSql: "'0'"),
                    pseudocrc = table.Column<int>(type: "int", nullable: false, defaultValueSql: "'0'"),
                    linksetdata = table.Column<string>(type: "mediumtext", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    allowunsit = table.Column<sbyte>(type: "tinyint", nullable: false),
                    scriptedsitonly = table.Column<sbyte>(type: "tinyint", nullable: false),
                    startstr = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "primshapes",
                columns: table => new
                {
                    UUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "''", collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Shape = table.Column<int>(type: "int", nullable: true),
                    ScaleX = table.Column<double>(type: "double", nullable: false),
                    ScaleY = table.Column<double>(type: "double", nullable: false),
                    ScaleZ = table.Column<double>(type: "double", nullable: false),
                    PCode = table.Column<int>(type: "int", nullable: true),
                    PathBegin = table.Column<int>(type: "int", nullable: true),
                    PathEnd = table.Column<int>(type: "int", nullable: true),
                    PathScaleX = table.Column<int>(type: "int", nullable: true),
                    PathScaleY = table.Column<int>(type: "int", nullable: true),
                    PathShearX = table.Column<int>(type: "int", nullable: true),
                    PathShearY = table.Column<int>(type: "int", nullable: true),
                    PathSkew = table.Column<int>(type: "int", nullable: true),
                    PathCurve = table.Column<int>(type: "int", nullable: true),
                    PathRadiusOffset = table.Column<int>(type: "int", nullable: true),
                    PathRevolutions = table.Column<int>(type: "int", nullable: true),
                    PathTaperX = table.Column<int>(type: "int", nullable: true),
                    PathTaperY = table.Column<int>(type: "int", nullable: true),
                    PathTwist = table.Column<int>(type: "int", nullable: true),
                    PathTwistBegin = table.Column<int>(type: "int", nullable: true),
                    ProfileBegin = table.Column<int>(type: "int", nullable: true),
                    ProfileEnd = table.Column<int>(type: "int", nullable: true),
                    ProfileCurve = table.Column<int>(type: "int", nullable: true),
                    ProfileHollow = table.Column<int>(type: "int", nullable: true),
                    State = table.Column<int>(type: "int", nullable: true),
                    Texture = table.Column<byte[]>(type: "longblob", nullable: true),
                    ExtraParams = table.Column<byte[]>(type: "longblob", nullable: true),
                    Media = table.Column<string>(type: "text", nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    LastAttachPoint = table.Column<int>(type: "int", nullable: false),
                    MatOvrd = table.Column<byte[]>(type: "blob", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.UUID);
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateTable(
                name: "regionban",
                columns: table => new
                {
                    regionUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedUUID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedIp = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    bannedIpHostMask = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "regionenvironment",
                columns: table => new
                {
                    region_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    llsd_settings = table.Column<string>(type: "mediumtext", nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.region_id);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "regionextra",
                columns: table => new
                {
                    RegionID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Name = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    value = table.Column<string>(type: "text", nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => new { x.RegionID, x.Name })
                        .Annotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "regionsettings",
                columns: table => new
                {
                    regionUUID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    block_terraform = table.Column<int>(type: "int", nullable: false),
                    block_fly = table.Column<int>(type: "int", nullable: false),
                    allow_damage = table.Column<int>(type: "int", nullable: false),
                    restrict_pushing = table.Column<int>(type: "int", nullable: false),
                    allow_land_resell = table.Column<int>(type: "int", nullable: false),
                    allow_land_join_divide = table.Column<int>(type: "int", nullable: false),
                    block_show_in_search = table.Column<int>(type: "int", nullable: false),
                    agent_limit = table.Column<int>(type: "int", nullable: false),
                    object_bonus = table.Column<double>(type: "double", nullable: false),
                    maturity = table.Column<int>(type: "int", nullable: false),
                    disable_scripts = table.Column<int>(type: "int", nullable: false),
                    disable_collisions = table.Column<int>(type: "int", nullable: false),
                    disable_physics = table.Column<int>(type: "int", nullable: false),
                    terrain_texture_1 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    terrain_texture_2 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    terrain_texture_3 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    terrain_texture_4 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    elevation_1_nw = table.Column<double>(type: "double", nullable: false),
                    elevation_2_nw = table.Column<double>(type: "double", nullable: false),
                    elevation_1_ne = table.Column<double>(type: "double", nullable: false),
                    elevation_2_ne = table.Column<double>(type: "double", nullable: false),
                    elevation_1_se = table.Column<double>(type: "double", nullable: false),
                    elevation_2_se = table.Column<double>(type: "double", nullable: false),
                    elevation_1_sw = table.Column<double>(type: "double", nullable: false),
                    elevation_2_sw = table.Column<double>(type: "double", nullable: false),
                    water_height = table.Column<double>(type: "double", nullable: false),
                    terrain_raise_limit = table.Column<double>(type: "double", nullable: false),
                    terrain_lower_limit = table.Column<double>(type: "double", nullable: false),
                    use_estate_sun = table.Column<int>(type: "int", nullable: false),
                    fixed_sun = table.Column<int>(type: "int", nullable: false),
                    sun_position = table.Column<double>(type: "double", nullable: false),
                    covenant = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Sandbox = table.Column<sbyte>(type: "tinyint", nullable: false),
                    sunvectorx = table.Column<double>(type: "double", nullable: false),
                    sunvectory = table.Column<double>(type: "double", nullable: false),
                    sunvectorz = table.Column<double>(type: "double", nullable: false),
                    loaded_creation_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    loaded_creation_datetime = table.Column<uint>(type: "int unsigned", nullable: false),
                    map_tile_ID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TelehubObject = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    parcel_tile_ID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    covenant_datetime = table.Column<uint>(type: "int unsigned", nullable: false),
                    block_search = table.Column<sbyte>(type: "tinyint", nullable: false),
                    casino = table.Column<sbyte>(type: "tinyint", nullable: false),
                    cacheID = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: true, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TerrainPBR1 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TerrainPBR2 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TerrainPBR3 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    TerrainPBR4 = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, defaultValueSql: "'00000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.regionUUID);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "regionwindlight",
                columns: table => new
                {
                    region_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'000000-0000-0000-0000-000000000000'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    water_color_r = table.Column<float>(type: "float(9,6) unsigned", nullable: false, defaultValueSql: "'4.000000'"),
                    water_color_g = table.Column<float>(type: "float(9,6) unsigned", nullable: false, defaultValueSql: "'38.000000'"),
                    water_color_b = table.Column<float>(type: "float(9,6) unsigned", nullable: false, defaultValueSql: "'64.000000'"),
                    water_fog_density_exponent = table.Column<float>(type: "float(9,7) unsigned", nullable: false, defaultValueSql: "'4.0000000'"),
                    underwater_fog_modifier = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.25000000'"),
                    reflection_wavelet_scale_1 = table.Column<float>(type: "float(9,7) unsigned", nullable: false, defaultValueSql: "'2.0000000'"),
                    reflection_wavelet_scale_2 = table.Column<float>(type: "float(9,7) unsigned", nullable: false, defaultValueSql: "'2.0000000'"),
                    reflection_wavelet_scale_3 = table.Column<float>(type: "float(9,7) unsigned", nullable: false, defaultValueSql: "'2.0000000'"),
                    fresnel_scale = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.40000001'"),
                    fresnel_offset = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.50000000'"),
                    refract_scale_above = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.03000000'"),
                    refract_scale_below = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.20000000'"),
                    blur_multiplier = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.04000000'"),
                    big_wave_direction_x = table.Column<float>(type: "float(9,8)", nullable: false, defaultValueSql: "'1.04999995'"),
                    big_wave_direction_y = table.Column<float>(type: "float(9,8)", nullable: false, defaultValueSql: "'-0.41999999'"),
                    little_wave_direction_x = table.Column<float>(type: "float(9,8)", nullable: false, defaultValueSql: "'1.11000001'"),
                    little_wave_direction_y = table.Column<float>(type: "float(9,8)", nullable: false, defaultValueSql: "'-1.15999997'"),
                    normal_map_texture = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, defaultValueSql: "'822ded49-9a6c-f61c-cb89-6df54f42cdf4'", collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    horizon_r = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.25000000'"),
                    horizon_g = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.25000000'"),
                    horizon_b = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.31999999'"),
                    horizon_i = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.31999999'"),
                    haze_horizon = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.19000000'"),
                    blue_density_r = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.12000000'"),
                    blue_density_g = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.22000000'"),
                    blue_density_b = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.38000000'"),
                    blue_density_i = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.38000000'"),
                    haze_density = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.69999999'"),
                    density_multiplier = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.18000001'"),
                    distance_multiplier = table.Column<float>(type: "float(9,6) unsigned", nullable: false, defaultValueSql: "'0.800000'"),
                    max_altitude = table.Column<uint>(type: "int unsigned", nullable: false, defaultValueSql: "'1605'"),
                    sun_moon_color_r = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.23999999'"),
                    sun_moon_color_g = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.25999999'"),
                    sun_moon_color_b = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.30000001'"),
                    sun_moon_color_i = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.30000001'"),
                    sun_moon_position = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.31700000'"),
                    ambient_r = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.34999999'"),
                    ambient_g = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.34999999'"),
                    ambient_b = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.34999999'"),
                    ambient_i = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.34999999'"),
                    east_angle = table.Column<float>(type: "float(9,8) unsigned", nullable: false),
                    sun_glow_focus = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.10000000'"),
                    sun_glow_size = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'1.75000000'"),
                    scene_gamma = table.Column<float>(type: "float(9,7) unsigned", nullable: false, defaultValueSql: "'1.0000000'"),
                    star_brightness = table.Column<float>(type: "float(9,8) unsigned", nullable: false),
                    cloud_color_r = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.41000000'"),
                    cloud_color_g = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.41000000'"),
                    cloud_color_b = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.41000000'"),
                    cloud_color_i = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.41000000'"),
                    cloud_x = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'1.00000000'"),
                    cloud_y = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.52999997'"),
                    cloud_density = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'1.00000000'"),
                    cloud_coverage = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.27000001'"),
                    cloud_scale = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.41999999'"),
                    cloud_detail_x = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'1.00000000'"),
                    cloud_detail_y = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.52999997'"),
                    cloud_detail_density = table.Column<float>(type: "float(9,8) unsigned", nullable: false, defaultValueSql: "'0.12000000'"),
                    cloud_scroll_x = table.Column<float>(type: "float(9,7)", nullable: false, defaultValueSql: "'0.2000000'"),
                    cloud_scroll_x_lock = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    cloud_scroll_y = table.Column<float>(type: "float(9,7)", nullable: false, defaultValueSql: "'0.0100000'"),
                    cloud_scroll_y_lock = table.Column<byte>(type: "tinyint unsigned", nullable: false),
                    draw_classic_clouds = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValueSql: "'1'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PRIMARY", x => x.region_id);
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "spawn_points",
                columns: table => new
                {
                    RegionID = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "utf8mb3_general_ci")
                        .Annotation("MySql:CharSet", "utf8mb3"),
                    Yaw = table.Column<float>(type: "float", nullable: false),
                    Pitch = table.Column<float>(type: "float", nullable: false),
                    Distance = table.Column<float>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "utf8mb3")
                .Annotation("Relational:Collation", "utf8mb3_general_ci");

            migrationBuilder.CreateTable(
                name: "terrain",
                columns: table => new
                {
                    RegionUUID = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "latin1_swedish_ci")
                        .Annotation("MySql:CharSet", "latin1"),
                    Revision = table.Column<int>(type: "int", nullable: true),
                    Heightfield = table.Column<byte[]>(type: "longblob", nullable: true)
                },
                constraints: table =>
                {
                })
                .Annotation("MySql:CharSet", "latin1")
                .Annotation("Relational:Collation", "latin1_swedish_ci");

            migrationBuilder.CreateIndex(
                name: "primitems_primid",
                table: "primitems",
                column: "primID");

            migrationBuilder.CreateIndex(
                name: "prims_regionuuid",
                table: "prims",
                column: "RegionUUID");

            migrationBuilder.CreateIndex(
                name: "prims_scenegroupid",
                table: "prims",
                column: "SceneGroupID");

            migrationBuilder.CreateIndex(
                name: "RegionID",
                table: "spawn_points",
                column: "RegionID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bakedterrain");

            migrationBuilder.DropTable(
                name: "land");

            migrationBuilder.DropTable(
                name: "landaccesslist");

            migrationBuilder.DropTable(
                name: "migrations");

            migrationBuilder.DropTable(
                name: "primitems");

            migrationBuilder.DropTable(
                name: "prims");

            migrationBuilder.DropTable(
                name: "primshapes");

            migrationBuilder.DropTable(
                name: "regionban");

            migrationBuilder.DropTable(
                name: "regionenvironment");

            migrationBuilder.DropTable(
                name: "regionextra");

            migrationBuilder.DropTable(
                name: "regionsettings");

            migrationBuilder.DropTable(
                name: "regionwindlight");

            migrationBuilder.DropTable(
                name: "spawn_points");

            migrationBuilder.DropTable(
                name: "terrain");
        }
    }
}
