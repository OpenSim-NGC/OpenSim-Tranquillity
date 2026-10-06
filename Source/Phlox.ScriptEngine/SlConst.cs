/*
 * SL constants the implementation needs, by their SL names and with SL's values, so a
 * function body never carries a bare number (or a private copy) that can drift from the compiler's
 * table. LSLSystemAPI.cs reaches them through "using static". SlConstTests.SlConstMatchesTable pins
 * every field here against InWorldz.Phlox.Compiler.DefaultConstants.Constants, and
 * SlConstantsTests.SlConstantsMatchLL pins that table against SL's own (lsl-definitions @ 10741b9).
 */
namespace Phlox.ScriptEngine
{
    internal static class SlConst
    {
        // ── AGENT_* ───────────────────────────────────────────────────────
        public const int AGENT_ALWAYS_RUN = 4096;
        public const int AGENT_ATTACHMENTS = 2;
        public const int AGENT_AWAY = 64;
        public const int AGENT_FLYING = 1;
        public const int AGENT_IN_AIR = 256;
        public const int AGENT_LIST_PARCEL = 1;
        public const int AGENT_LIST_PARCEL_OWNER = 2;
        public const int AGENT_LIST_REGION = 4;
        public const int AGENT_MOUSELOOK = 8;
        public const int AGENT_ON_OBJECT = 32;
        public const int AGENT_SCRIPTED = 4;
        public const int AGENT_SITTING = 16;
        public const int AGENT_WALKING = 128;

        // ── ALL_* ─────────────────────────────────────────────────────────
        public const int ALL_SIDES = -1;

        // ── CHARACTER_* ───────────────────────────────────────────────────
        public const int CHARACTER_DESIRED_SPEED = 1;

        // ── CLICK_ACTION_* ────────────────────────────────────────────────
        public const int CLICK_ACTION_NONE = 0;
        public const int CLICK_ACTION_PAY = 3;

        // ── CONTENT_*─────────────────────────────────────────────────────
        public const int CONTENT_TYPE_ATOM = 4;
        public const int CONTENT_TYPE_FORM = 7;
        public const int CONTENT_TYPE_HTML = 1;
        public const int CONTENT_TYPE_JSON = 5;
        public const int CONTENT_TYPE_LLSD = 6;
        public const int CONTENT_TYPE_RSS = 8;
        public const int CONTENT_TYPE_XHTML = 3;
        public const int CONTENT_TYPE_XML = 2;

        // ── DAMAGE_TYPE_* ─────────────────────────────────────────────────
        public const int DAMAGE_TYPE_GENERIC = 0;

        // ── llSetPhysicsMaterial mask: DENSITY, FRICTION, RESTITUTION, GRAVITY_MULTIPLIER ──
        public const int DENSITY = 1;
        public const int FRICTION = 2;
        public const int GRAVITY_MULTIPLIER = 8;
        public const int RESTITUTION = 4;

        // ── DATA_* ────────────────────────────────────────────────────────
        public const int DATA_BORN = 3;
        public const int DATA_NAME = 2;
        public const int DATA_ONLINE = 1;
        public const int DATA_PAYINFO = 8;
        public const int DATA_RATING = 4;
        public const int DATA_SIM_POS = 5;
        public const int DATA_SIM_RATING = 7;
        public const int DATA_SIM_STATUS = 6;

        // ── DEBUG_* ───────────────────────────────────────────────────────
        public const int DEBUG_CHANNEL = 2147483647;

        // ── ENV_* ─────────────────────────────────────────────────────────
        public const int ENV_OK = 1;

        // ── ERR_* ─────────────────────────────────────────────────────────
        public const int ERR_GENERIC = -1;
        public const int ERR_PARCEL_PERMISSIONS = -2;
        public const int ERR_MALFORMED_PARAMS = -3;
        public const int ERR_RUNTIME_PERMISSIONS = -4;
        public const int ERR_THROTTLED = -5;

        // ── INVENTORY_* ───────────────────────────────────────────────────
        public const int INVENTORY_SCRIPT = 10;

        // ── IW_DELIVER_* (InWorldz; Halcyon LSL_Constants.cs) ─────────────
        public const int IW_DELIVER_BADKEY = 1;
        public const int IW_DELIVER_ITEM = 3;
        public const int IW_DELIVER_MUTED = 2;
        public const int IW_DELIVER_NONE = 7;
        public const int IW_DELIVER_OK = 0;
        public const int IW_DELIVER_PERM = 6;
        public const int IW_DELIVER_PRIM = 4;
        public const int IW_DELIVER_USER = 5;

        // ── IW_PRIM_* (InWorldz; Halcyon LSL_Constants.cs) ───────────────
        public const int IW_PRIM_ALPHA = 11001;
        public const int IW_PRIM_PROJECTOR = 11100;
        public const int IW_PRIM_PROJECTOR_AMBIENCE = 11105;
        public const int IW_PRIM_PROJECTOR_ENABLED = 11101;
        public const int IW_PRIM_PROJECTOR_FOCUS = 11104;
        public const int IW_PRIM_PROJECTOR_FOV = 11103;
        public const int IW_PRIM_PROJECTOR_TEXTURE = 11102;

        // ── JSON_* ────────────────────────────────────────────────────────
        public const int JSON_APPEND = -1;

        // ── KFM_* ─────────────────────────────────────────────────────────
        public const int KFM_CMD_PAUSE = 2;
        public const int KFM_CMD_PLAY = 0;
        public const int KFM_CMD_STOP = 1;
        public const int KFM_COMMAND = 0;
        public const int KFM_DATA = 2;
        public const int KFM_FORWARD = 0;
        public const int KFM_LOOP = 1;
        public const int KFM_MODE = 1;
        public const int KFM_PING_PONG = 2;
        public const int KFM_REVERSE = 3;
        public const int KFM_ROTATION = 1;
        public const int KFM_TRANSLATION = 2;

        // ── LINK_* ────────────────────────────────────────────────────────
        public const int LINK_ALL_CHILDREN = -3;
        public const int LINK_ALL_OTHERS = -2;
        public const int LINK_ROOT = 1;
        public const int LINK_SET = -1;
        public const int LINK_THIS = -4;

        // ── LINKSETDATA_* ─────────────────────────────────────────────────
        public const int LINKSETDATA_DELETE = 2;
        public const int LINKSETDATA_EMEMORY = 1;
        public const int LINKSETDATA_ENOKEY = 2;
        public const int LINKSETDATA_MULTIDELETE = 3;
        public const int LINKSETDATA_NOTFOUND = 4;
        public const int LINKSETDATA_RESET = 0;
        public const int LINKSETDATA_UPDATE = 1;

        // ── MASK_* ────────────────────────────────────────────────────────
        public const int MASK_BASE = 0;
        public const int MASK_EVERYONE = 3;
        public const int MASK_GROUP = 2;
        public const int MASK_NEXT = 4;
        public const int MASK_OWNER = 1;

        // ── OBJECT_* ──────────────────────────────────────────────────────
        public const int OBJECT_ATTACHED_POINT = 19;
        public const int OBJECT_CREATOR = 8;
        public const int OBJECT_DESC = 2;
        public const int OBJECT_GROUP = 7;
        public const int OBJECT_NAME = 1;
        public const int OBJECT_OWNER = 6;
        public const int OBJECT_PHANTOM = 22;
        public const int OBJECT_PHYSICS = 21;
        public const int OBJECT_PHYSICS_COST = 16;
        public const int OBJECT_POS = 3;
        public const int OBJECT_PRIM_EQUIVALENCE = 13;
        public const int OBJECT_RETURN_PARCEL = 1;
        public const int OBJECT_RETURN_PARCEL_OWNER = 2;
        public const int OBJECT_RETURN_REGION = 4;
        public const int OBJECT_ROOT = 18;
        public const int OBJECT_ROT = 4;
        public const int OBJECT_RUNNING_SCRIPT_COUNT = 9;
        public const int OBJECT_SCRIPT_MEMORY = 11;
        public const int OBJECT_SCRIPT_TIME = 12;
        public const int OBJECT_SERVER_COST = 14;
        public const int OBJECT_STREAMING_COST = 15;
        public const int OBJECT_TEMP_ON_REZ = 23;
        public const int OBJECT_TOTAL_SCRIPT_COUNT = 10;
        public const int OBJECT_VELOCITY = 5;

        // ── PARCEL_* ──────────────────────────────────────────────────────
        public const int PARCEL_DETAILS_DESC = 1;
        public const int PARCEL_DETAILS_GROUP = 3;
        public const int PARCEL_DETAILS_NAME = 0;
        public const int PARCEL_DETAILS_OWNER = 2;
        public const int PARCEL_DETAILS_SEE_AVATARS = 6;

        // ── PAYMENT_* ─────────────────────────────────────────────────────
        public const int PAYMENT_INFO_ON_FILE = 1;
        public const int PAYMENT_INFO_USED = 2;

        // ── PERM_* ────────────────────────────────────────────────────────
        public const int PERM_ALL = 2147483647;
        public const int PERM_COPY = 32768;
        public const int PERM_MODIFY = 16384;
        public const int PERM_MOVE = 524288;
        public const int PERM_TRANSFER = 8192;

        // ── PERMISSION_* ──────────────────────────────────────────────────
        public const int PERMISSION_ATTACH = 32;
        public const int PERMISSION_CHANGE_LINKS = 128;
        public const int PERMISSION_CONTROL_CAMERA = 2048;
        public const int PERMISSION_DEBIT = 2;
        public const int PERMISSION_OVERRIDE_ANIMATIONS = 32768;
        public const int PERMISSION_RETURN_OBJECTS = 65536;
        public const int PERMISSION_SILENT_ESTATE_MANAGEMENT = 16384;
        public const int PERMISSION_TAKE_CONTROLS = 4;
        public const int PERMISSION_TELEPORT = 4096;
        public const int PERMISSION_TRACK_CAMERA = 1024;
        public const int PERMISSION_TRIGGER_ANIMATION = 16;

        // ── PRIM_* ────────────────────────────────────────────────────────
        public const int PRIM_ALLOW_UNSIT = 39;
        public const int PRIM_ALPHA_MODE = 38;
        public const int PRIM_ALPHA_MODE_BLEND = 1;
        public const int PRIM_ALPHA_MODE_EMISSIVE = 3;
        public const int PRIM_ALPHA_MODE_MASK = 2;
        public const int PRIM_ALPHA_MODE_NONE = 0;
        public const int PRIM_BUMP_SHINY = 19;
        public const int PRIM_CAST_SHADOWS = 24;
        public const int PRIM_CLICK_ACTION = 43;
        public const int PRIM_COLLISION_SOUND = 53;
        public const int PRIM_COLOR = 18;
        public const int PRIM_DAMAGE = 51;
        public const int PRIM_DESC = 28;
        public const int PRIM_FLEXIBLE = 21;
        public const int PRIM_FULLBRIGHT = 20;
        public const int PRIM_GLOW = 25;
        public const int PRIM_GLTF_BASE_COLOR = 48;
        public const int PRIM_GLTF_EMISSIVE = 46;
        public const int PRIM_GLTF_METALLIC_ROUGHNESS = 47;
        public const int PRIM_GLTF_NORMAL = 45;
        public const int PRIM_HEALTH = 52;
        public const int PRIM_HOLE_DEFAULT = 0;
        public const int PRIM_LINK_TARGET = 34;
        public const int PRIM_MATERIAL = 2;
        public const int PRIM_MATERIAL_FLESH = 4;
        public const int PRIM_MEDIA_ALT_IMAGE_ENABLE = 0;
        public const int PRIM_MEDIA_AUTO_LOOP = 4;
        public const int PRIM_MEDIA_AUTO_PLAY = 5;
        public const int PRIM_MEDIA_AUTO_SCALE = 6;
        public const int PRIM_MEDIA_AUTO_ZOOM = 7;
        public const int PRIM_MEDIA_CONTROLS = 1;
        public const int PRIM_MEDIA_CURRENT_URL = 2;
        public const int PRIM_MEDIA_FIRST_CLICK_INTERACT = 8;
        public const int PRIM_MEDIA_HEIGHT_PIXELS = 10;
        public const int PRIM_MEDIA_HOME_URL = 3;
        public const int PRIM_MEDIA_PERMS_CONTROL = 14;
        public const int PRIM_MEDIA_PERMS_INTERACT = 13;
        public const int PRIM_MEDIA_WHITELIST = 12;
        public const int PRIM_MEDIA_WHITELIST_ENABLE = 11;
        public const int PRIM_MEDIA_WIDTH_PIXELS = 9;
        public const int PRIM_NAME = 27;
        public const int PRIM_NORMAL = 37;
        public const int PRIM_OMEGA = 32;
        public const int PRIM_PHANTOM = 5;
        public const int PRIM_PHYSICS = 3;
        public const int PRIM_PHYSICS_MATERIAL = 31; // OpenSim's own rule, not SL's (LSL_Constants.cs)
        public const int PRIM_PHYSICS_SHAPE_CONVEX = 2;
        public const int PRIM_PHYSICS_SHAPE_NONE = 1;
        public const int PRIM_PHYSICS_SHAPE_PRIM = 0;
        public const int PRIM_PHYSICS_SHAPE_TYPE = 30;
        public const int PRIM_POINT_LIGHT = 23;
        public const int PRIM_POSITION = 6;
        public const int PRIM_POS_LOCAL = 33;
        public const int PRIM_PROJECTOR = 42;
        public const int PRIM_REFLECTION_PROBE = 44;
        public const int PRIM_RENDER_MATERIAL = 49;
        public const int PRIM_ROTATION = 8;
        public const int PRIM_ROT_LOCAL = 29;
        public const int PRIM_SCRIPTED_SIT_ONLY = 40;
        public const int PRIM_SIT_FLAGS = 50;
        public const int PRIM_SIT_TARGET = 41;
        public const int PRIM_SIZE = 7;
        public const int PRIM_SLICE = 35;
        public const int PRIM_SPECULAR = 36;
        public const int PRIM_TEMP_ON_REZ = 4;
        public const int PRIM_TEXGEN = 22;
        public const int PRIM_TEXGEN_DEFAULT = 0;
        public const int PRIM_TEXGEN_PLANAR = 1;
        public const int PRIM_TEXT = 26;
        public const int PRIM_TEXTURE = 17;
        public const int PRIM_TYPE = 9;
        public const int PRIM_TYPE_BOX = 0;
        public const int PRIM_TYPE_CYLINDER = 1;
        public const int PRIM_TYPE_PRISM = 2;
        public const int PRIM_TYPE_RING = 6;
        public const int PRIM_TYPE_SCULPT = 7;
        public const int PRIM_TYPE_SPHERE = 3;
        public const int PRIM_TYPE_TORUS = 4;
        public const int PRIM_TYPE_TUBE = 5;

        // ── PROFILE_* ─────────────────────────────────────────────────────
        public const int PROFILE_SCRIPT_MEMORY = 1;

        // ── PSYS_* ────────────────────────────────────────────────────────
        public const int PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA = 9;
        public const int PSYS_PART_BF_SOURCE_ALPHA = 7;
        public const int PSYS_PART_BLEND_FUNC_DEST = 25;
        public const int PSYS_PART_BLEND_FUNC_SOURCE = 24;
        public const int PSYS_PART_END_ALPHA = 4;
        public const int PSYS_PART_END_COLOR = 3;
        public const int PSYS_PART_END_GLOW = 27;
        public const int PSYS_PART_END_SCALE = 6;
        public const int PSYS_PART_FLAGS = 0;
        public const int PSYS_PART_MAX_AGE = 7;
        public const int PSYS_PART_START_ALPHA = 2;
        public const int PSYS_PART_START_COLOR = 1;
        public const int PSYS_PART_START_GLOW = 26;
        public const int PSYS_PART_START_SCALE = 5;
        public const int PSYS_SRC_ACCEL = 8;
        public const int PSYS_SRC_ANGLE_BEGIN = 22;
        public const int PSYS_SRC_ANGLE_END = 23;
        public const int PSYS_SRC_BURST_PART_COUNT = 15;
        public const int PSYS_SRC_BURST_RADIUS = 16;
        public const int PSYS_SRC_BURST_RATE = 13;
        public const int PSYS_SRC_BURST_SPEED_MAX = 18;
        public const int PSYS_SRC_BURST_SPEED_MIN = 17;
        public const int PSYS_SRC_INNERANGLE = 10;
        public const int PSYS_SRC_MAX_AGE = 19;
        public const int PSYS_SRC_OMEGA = 21;
        public const int PSYS_SRC_OUTERANGLE = 11;
        public const int PSYS_SRC_PATTERN = 9;
        public const int PSYS_SRC_TARGET_KEY = 20;
        public const int PSYS_SRC_TEXTURE = 12;

        // ── RC_* ──────────────────────────────────────────────────────────
        public const int RC_DATA_FLAGS = 2;
        public const int RC_GET_LINK_NUM = 4;
        public const int RC_GET_NORMAL = 1;
        public const int RC_GET_ROOT_KEY = 2;
        public const int RC_MAX_HITS = 3;
        public const int RC_REJECT_AGENTS = 1;
        public const int RC_REJECT_LAND = 8;
        public const int RC_REJECT_NONPHYSICAL = 4;
        public const int RC_REJECT_PHYSICAL = 2;
        public const int RC_REJECT_TYPES = 0;

        // ── RCERR_* ───────────────────────────────────────────────────────
        public const int RCERR_CAST_TIME_EXCEEDED = -3;

        // ── REGION_* ──────────────────────────────────────────────────────
        public const int REGION_FLAG_ALLOW_DAMAGE = 1;
        public const int REGION_FLAG_BLOCK_FLY = 524288;
        public const int REGION_FLAG_DISABLE_COLLISIONS = 4096;
        public const int REGION_FLAG_DISABLE_PHYSICS = 16384;
        public const int REGION_FLAG_RESTRICT_PUSHOBJECT = 4194304;
        public const int REGION_FLAG_SANDBOX = 256;

        // ── REZ_* ─────────────────────────────────────────────────────────
        public const int REZ_DAMAGE = 8;
        public const int REZ_FLAGS = 1;
        public const int REZ_PARAM = 0;
        public const int REZ_PARAM_STRING = 13;
        public const int REZ_POS = 2;
        public const int REZ_ROT = 3;
        public const int REZ_VEL = 4;

        // ── SIT_* ─────────────────────────────────────────────────────────
        public const int SIT_FLAG_ALLOW_UNSIT = 2;
        public const int SIT_FLAG_NO_COLLIDE = 16;
        public const int SIT_FLAG_NO_DAMAGE = 32;
        public const int SIT_FLAG_SCRIPTED_ONLY = 4;
        public const int SIT_FLAG_SIT_TARGET = 1;

        // ── SKY_* ─────────────────────────────────────────────────────────
        public const int SKY_AMBIENT = 0;
        public const int SKY_CLOUDS = 2;
        public const int SKY_DOME = 4;
        public const int SKY_GAMMA = 5;
        public const int SKY_GLOW = 6;
        public const int SKY_MOON = 9;
        public const int SKY_STAR_BRIGHTNESS = 13;
        public const int SKY_SUN = 14;
        public const int SKY_TRACKS = 15;

        // ── STATUS_* ──────────────────────────────────────────────────────
        public const int STATUS_BLOCK_GRAB = 64;
        public const int STATUS_BLOCK_GRAB_OBJECT = 1024;
        public const int STATUS_CAST_SHADOWS = 512;
        public const int STATUS_DIE_AT_EDGE = 128;
        public const int STATUS_PHANTOM = 16;
        public const int STATUS_PHYSICS = 1;
        public const int STATUS_RETURN_AT_EDGE = 256;
        public const int STATUS_ROTATE_X = 2;
        public const int STATUS_ROTATE_Y = 4;
        public const int STATUS_ROTATE_Z = 8;
        public const int STATUS_SANDBOX = 32;

        // ── TYPE_* ────────────────────────────────────────────────────────
        public const int TYPE_FLOAT = 2;
        public const int TYPE_INTEGER = 1;
        public const int TYPE_INVALID = 0;
        public const int TYPE_KEY = 4;
        public const int TYPE_ROTATION = 6;
        public const int TYPE_STRING = 3;
        public const int TYPE_VECTOR = 5;

        // ── XP_* ──────────────────────────────────────────────────────────
        public const int XP_ERROR_EXPERIENCE_DISABLED = 8;
        public const int XP_ERROR_EXPERIENCE_SUSPENDED = 9;
        public const int XP_ERROR_INVALID_PARAMETERS = 3;
        public const int XP_ERROR_KEY_NOT_FOUND = 14;
        public const int XP_ERROR_MATURITY_EXCEEDED = 16;
        public const int XP_ERROR_NONE = 0;
        public const int XP_ERROR_NOT_FOUND = 6;
        public const int XP_ERROR_NOT_PERMITTED = 4;
        public const int XP_ERROR_NOT_PERMITTED_LAND = 17;
        public const int XP_ERROR_NO_EXPERIENCE = 5;
        public const int XP_ERROR_QUOTA_EXCEEDED = 11;
        public const int XP_ERROR_REQUEST_PERM_TIMEOUT = 18;
        public const int XP_ERROR_RETRY_UPDATE = 15;
        public const int XP_ERROR_STORAGE_EXCEPTION = 13;
        public const int XP_ERROR_STORE_DISABLED = 12;
    }
}
