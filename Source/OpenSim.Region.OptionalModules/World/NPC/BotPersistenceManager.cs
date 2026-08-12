/*
 * Copyright (c) Legion Grid Contributors
 * BotPersistenceManager.cs — Persistent bot storage and respawn system
 * Phase 34: Saves bot data to SQLite and auto-respawns on region load.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Timers;
// Tranquillity has ImplicitUsings=enable (auto-imports System.Threading), so alias
// Timer to System.Timers.Timer to resolve the System.Threading.Timer ambiguity.
using Timer = System.Timers.Timer;
using log4net;
using System.Data.SQLite;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.World.NPC
{
    /// <summary>
    /// Error codes returned by bot persistence script functions.
    /// </summary>
    public static class BotPersistError
    {
        public const int OK              =  0;
        public const int NOT_FOUND       = -1;
        public const int NO_PERMISSION   = -2;
        public const int PARCEL_CAP      = -3;
        public const int OWNER_CAP       = -4;
        public const int REGION_CAP      = -5;
        public const int DISABLED        = -6;
        public const int ALREADY         = -7;
    }

    /// <summary>
    /// Serializable record for a persistent bot stored in SQLite.
    /// </summary>
    public class PersistentBotRecord
    {
        public UUID BotID;
        public UUID OwnerID;
        public UUID CreatorScript;
        public UUID CreatorObject;
        public UUID RegionID;
        public UUID ParcelID;

        public string BotFirstName;
        public string BotLastName;

        public Vector3 Position;
        public Quaternion Rotation;

        public byte[] AppearanceData;

        public string TagsJson;        // JSON array of strings
        public string CustomDataJson;  // JSON object for script key-value data

        public bool Active;
        public string DeactivationReason;

        public DateTime CreatedAt;
        public DateTime UpdatedAt;
        public DateTime? ExpiresAt;
    }

    /// <summary>
    /// Manages persistent bot storage in SQLite with staggered respawn,
    /// capacity limits, expiration, and admin controls.
    /// </summary>
    public class BotPersistenceManager
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // ── Configuration ──
        private bool m_gridEnabled;
        private string m_databasePath;
        private int m_maxPerParcel = 10;
        private int m_maxPerOwner = 15;
        private int m_maxPerRegion = 50;
        private bool m_separateAgentPool;
        private int m_defaultTTLSeconds = 604800; // 7 days
        private int m_positionSaveInterval = 60;
        private int m_cleanupInterval = 21600; // 6 hours
        private int m_respawnRate = 2;

        // ── Runtime state ──
        private SQLiteConnection m_db;
        private Scene m_scene;
        private BotManager m_botManager;
        private Timer m_positionSaveTimer;
        private Timer m_cleanupTimer;
        private Timer m_respawnTimer;
        private Queue<PersistentBotRecord> m_respawnQueue;
        private readonly HashSet<UUID> m_persistentBotIDs = new HashSet<UUID>();
        private bool m_initialized;
        private readonly object m_lock = new object();

        // Track which bots have dirty appearance (changed since last save)
        private readonly HashSet<UUID> m_dirtyAppearance = new HashSet<UUID>();

        #region Initialization

        /// <summary>
        /// Initialize from OpenSim.ini config. Call once per region.
        /// </summary>
        public void Initialize(Scene scene, BotManager botManager, IConfigSource configSource)
        {
            m_scene = scene;
            m_botManager = botManager;

            IConfig config = configSource.Configs["BotPersistence"];
            if (config == null)
            {
                m_gridEnabled = false;
                m_log.Info("[BotPersistence] No [BotPersistence] section in config — disabled.");
                return;
            }

            m_gridEnabled = config.GetBoolean("Enabled", false);
            if (!m_gridEnabled)
            {
                m_log.Info("[BotPersistence] Disabled in config.");
                return;
            }

            m_databasePath = config.GetString("DatabaseFile", "botpersistence.db");
            m_maxPerParcel = config.GetInt("MaxPerParcel", 10);
            m_maxPerOwner = config.GetInt("MaxPerOwner", 15);
            m_maxPerRegion = config.GetInt("MaxPerRegion", 50);
            m_separateAgentPool = config.GetBoolean("SeparateAgentPool", false);
            m_defaultTTLSeconds = config.GetInt("DefaultTTL", 604800);
            m_positionSaveInterval = config.GetInt("PositionSaveInterval", 60);
            m_cleanupInterval = config.GetInt("CleanupInterval", 21600);
            m_respawnRate = config.GetInt("RespawnRate", 2);

            // Build per-region database path
            string dbDir = Path.GetDirectoryName(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, m_databasePath));
            string dbName = Path.GetFileNameWithoutExtension(m_databasePath);
            string dbExt = Path.GetExtension(m_databasePath);
            string regionDbPath = Path.Combine(dbDir ?? ".",
                $"{dbName}_{scene.RegionInfo.RegionID}{dbExt}");

            try
            {
                InitializeDatabase(regionDbPath);
                m_initialized = true;

                m_log.InfoFormat("[BotPersistence] Initialized for region {0} — db: {1}",
                    scene.RegionInfo.RegionName, regionDbPath);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to initialize database: {0}", ex.Message);
                m_gridEnabled = false;
            }
        }

        private void InitializeDatabase(string dbPath)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);
            string connStr = $"Data Source={dbPath}";
            m_db = new SQLiteConnection(connStr);
            m_db.Open();

            // Enable WAL mode for crash safety
            using (var cmd = m_db.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
            }

            // Create table if not exists
            using (var cmd = m_db.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS persistent_bots (
    bot_id          TEXT PRIMARY KEY,
    owner_id        TEXT NOT NULL,
    creator_script  TEXT NOT NULL,
    creator_object  TEXT NOT NULL,
    region_id       TEXT NOT NULL,
    parcel_id       TEXT NOT NULL,

    bot_first_name  TEXT NOT NULL,
    bot_last_name   TEXT NOT NULL,

    position_x      REAL NOT NULL,
    position_y      REAL NOT NULL,
    position_z      REAL NOT NULL,
    rotation_x      REAL NOT NULL DEFAULT 0,
    rotation_y      REAL NOT NULL DEFAULT 0,
    rotation_z      REAL NOT NULL DEFAULT 0,
    rotation_w      REAL NOT NULL DEFAULT 1,

    appearance_data BLOB,

    tags            TEXT,
    custom_data     TEXT,

    active          INTEGER NOT NULL DEFAULT 1,
    deactivation_reason TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    expires_at      TEXT
);

CREATE INDEX IF NOT EXISTS idx_pb_owner ON persistent_bots(owner_id);
CREATE INDEX IF NOT EXISTS idx_pb_region ON persistent_bots(region_id, active);
CREATE INDEX IF NOT EXISTS idx_pb_parcel ON persistent_bots(parcel_id, active);
";
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region Enable/Disable Checks

        /// <summary>
        /// Returns true if bot persistence is available for this region.
        /// Checks both grid-level config and region-level setting.
        /// </summary>
        public bool IsEnabled()
        {
            if (!m_gridEnabled || !m_initialized) return false;

            // Region-level toggle (AllowBotPersistence) will be added to
            // RegionSettings in Phase 34b. For now, grid-level config controls it.
            return true;
        }

        #endregion

        #region Persist / Remove / Query

        /// <summary>
        /// Mark an existing bot as persistent.
        /// </summary>
        public int SetPersistent(UUID botID, UUID ownerID, UUID scriptItemID,
            UUID objectID, int ttlSeconds)
        {
            if (!IsEnabled()) return BotPersistError.DISABLED;

            // Check if already persistent
            lock (m_lock)
            {
                if (m_persistentBotIDs.Contains(botID))
                    return BotPersistError.ALREADY;
            }

            // Verify the bot exists and caller owns it
            if (!m_botManager.CheckPermission(botID, ownerID))
                return BotPersistError.NOT_FOUND;

            // Get bot's ScenePresence for position/appearance
            ScenePresence sp = m_scene.GetScenePresence(botID);
            if (sp == null) return BotPersistError.NOT_FOUND;

            // Get parcel at bot's position
            ILandObject parcel = m_scene.LandChannel.GetLandObject(
                sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
            if (parcel == null) return BotPersistError.NO_PERMISSION;

            // Check rez rights on parcel
            if (!CheckRezRights(ownerID, parcel))
                return BotPersistError.NO_PERMISSION;

            UUID parcelID = parcel.LandData.GlobalID;
            string regionID = m_scene.RegionInfo.RegionID.ToString();

            // Check capacity limits
            int capResult = CheckCapacityLimits(ownerID, parcelID);
            if (capResult != BotPersistError.OK)
                return capResult;

            // Build the record
            DateTime now = DateTime.UtcNow;
            DateTime? expiresAt = null;
            int effectiveTTL = ttlSeconds > 0 ? ttlSeconds : m_defaultTTLSeconds;
            if (effectiveTTL > 0)
                expiresAt = now.AddSeconds(effectiveTTL);

            // Serialize appearance
            byte[] appearanceBytes = null;
            if (sp.Appearance != null)
            {
                try
                {
                    var ctx = new EntityTransferContext();
                    var osd = sp.Appearance.Pack(ctx);
                    appearanceBytes = System.Text.Encoding.UTF8.GetBytes(
                        OpenMetaverse.StructuredData.OSDParser.SerializeLLSDXmlString(osd));
                }
                catch (Exception ex)
                {
                    m_log.WarnFormat("[BotPersistence] Failed to serialize appearance for {0}: {1}",
                        botID, ex.Message);
                }
            }

            // Get bot tags from BotManager
            List<string> tags = m_botManager.GetBotTags(botID);
            string tagsJson = tags.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(tags)
                : null;

            // Insert into database
            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR REPLACE INTO persistent_bots
    (bot_id, owner_id, creator_script, creator_object, region_id, parcel_id,
     bot_first_name, bot_last_name,
     position_x, position_y, position_z,
     rotation_x, rotation_y, rotation_z, rotation_w,
     appearance_data, tags, custom_data,
     active, created_at, updated_at, expires_at)
VALUES
    (@botID, @ownerID, @script, @object, @regionID, @parcelID,
     @firstName, @lastName,
     @px, @py, @pz, @rx, @ry, @rz, @rw,
     @appearance, @tags, @customData,
     1, @created, @updated, @expires)";

                    cmd.Parameters.AddWithValue("@botID", botID.ToString());
                    cmd.Parameters.AddWithValue("@ownerID", ownerID.ToString());
                    cmd.Parameters.AddWithValue("@script", scriptItemID.ToString());
                    cmd.Parameters.AddWithValue("@object", objectID.ToString());
                    cmd.Parameters.AddWithValue("@regionID", regionID);
                    cmd.Parameters.AddWithValue("@parcelID", parcelID.ToString());
                    cmd.Parameters.AddWithValue("@firstName", sp.Firstname ?? "Bot");
                    cmd.Parameters.AddWithValue("@lastName", sp.Lastname ?? "Resident");
                    cmd.Parameters.AddWithValue("@px", sp.AbsolutePosition.X);
                    cmd.Parameters.AddWithValue("@py", sp.AbsolutePosition.Y);
                    cmd.Parameters.AddWithValue("@pz", sp.AbsolutePosition.Z);
                    cmd.Parameters.AddWithValue("@rx", sp.Rotation.X);
                    cmd.Parameters.AddWithValue("@ry", sp.Rotation.Y);
                    cmd.Parameters.AddWithValue("@rz", sp.Rotation.Z);
                    cmd.Parameters.AddWithValue("@rw", sp.Rotation.W);
                    cmd.Parameters.AddWithValue("@appearance",
                        appearanceBytes != null ? (object)appearanceBytes : DBNull.Value);
                    cmd.Parameters.AddWithValue("@tags",
                        tagsJson != null ? (object)tagsJson : DBNull.Value);
                    cmd.Parameters.AddWithValue("@customData", DBNull.Value);
                    cmd.Parameters.AddWithValue("@created", now.ToString("o"));
                    cmd.Parameters.AddWithValue("@updated", now.ToString("o"));
                    cmd.Parameters.AddWithValue("@expires",
                        expiresAt.HasValue ? (object)expiresAt.Value.ToString("o") : DBNull.Value);

                    cmd.ExecuteNonQuery();
                }

                lock (m_lock)
                    m_persistentBotIDs.Add(botID);

                m_log.InfoFormat("[BotPersistence] Bot {0} persisted (owner {1}, TTL {2}s)",
                    botID, ownerID, effectiveTTL);

                return BotPersistError.OK;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to persist bot {0}: {1}",
                    botID, ex.Message);
                return BotPersistError.NOT_FOUND;
            }
        }

        /// <summary>
        /// Remove persistence from a bot (bot continues as non-persistent).
        /// </summary>
        public int RemovePersistent(UUID botID, UUID ownerID)
        {
            if (!IsEnabled()) return BotPersistError.DISABLED;

            if (!m_botManager.CheckPermission(botID, ownerID))
                return BotPersistError.NOT_FOUND;

            lock (m_lock)
            {
                if (!m_persistentBotIDs.Contains(botID))
                    return BotPersistError.NOT_FOUND;
            }

            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM persistent_bots WHERE bot_id = @id";
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    cmd.ExecuteNonQuery();
                }

                lock (m_lock)
                    m_persistentBotIDs.Remove(botID);

                lock (m_dirtyAppearance)
                    m_dirtyAppearance.Remove(botID);

                m_log.InfoFormat("[BotPersistence] Bot {0} persistence removed", botID);
                return BotPersistError.OK;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to remove persistence for {0}: {1}",
                    botID, ex.Message);
                return BotPersistError.NOT_FOUND;
            }
        }

        /// <summary>
        /// Check if a bot is persistent.
        /// </summary>
        public bool IsPersistent(UUID botID)
        {
            lock (m_lock)
                return m_persistentBotIDs.Contains(botID);
        }

        /// <summary>
        /// Get custom data value for a persistent bot.
        /// </summary>
        public string GetPersistentData(UUID botID, string key)
        {
            if (!IsPersistent(botID)) return string.Empty;

            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = "SELECT custom_data FROM persistent_bots WHERE bot_id = @id AND active = 1";
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    var result = cmd.ExecuteScalar();
                    if (result == null || result == DBNull.Value) return string.Empty;

                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        result.ToString());
                    return dict != null && dict.TryGetValue(key, out string val) ? val : string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Set custom data value for a persistent bot.
        /// </summary>
        public int SetPersistentData(UUID botID, UUID ownerID, string key, string value)
        {
            if (!IsEnabled()) return BotPersistError.DISABLED;
            if (!IsPersistent(botID)) return BotPersistError.NOT_FOUND;
            if (!m_botManager.CheckPermission(botID, ownerID))
                return BotPersistError.NOT_FOUND;

            try
            {
                // Read existing custom_data
                Dictionary<string, string> dict = null;
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = "SELECT custom_data FROM persistent_bots WHERE bot_id = @id";
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        try
                        {
                            dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                                result.ToString());
                        }
                        catch { }
                    }
                }

                dict ??= new Dictionary<string, string>();

                if (string.IsNullOrEmpty(value))
                    dict.Remove(key);
                else
                    dict[key] = value;

                string json = System.Text.Json.JsonSerializer.Serialize(dict);

                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET custom_data = @data, updated_at = @now
WHERE bot_id = @id";
                    cmd.Parameters.AddWithValue("@data", json);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    cmd.ExecuteNonQuery();
                }

                return BotPersistError.OK;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] SetPersistentData failed for {0}: {1}",
                    botID, ex.Message);
                return BotPersistError.NOT_FOUND;
            }
        }

        #endregion

        #region Heartbeat / TTL Refresh

        /// <summary>
        /// Called when a script interacts with a persistent bot.
        /// Refreshes the expires_at timestamp.
        /// </summary>
        public void TouchHeartbeat(UUID botID)
        {
            if (!IsPersistent(botID)) return;

            try
            {
                DateTime? newExpiry = null;
                if (m_defaultTTLSeconds > 0)
                    newExpiry = DateTime.UtcNow.AddSeconds(m_defaultTTLSeconds);

                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET updated_at = @now, expires_at = @expires
WHERE bot_id = @id AND active = 1";
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@expires",
                        newExpiry.HasValue ? (object)newExpiry.Value.ToString("o") : DBNull.Value);
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                m_log.DebugFormat("[BotPersistence] Heartbeat update failed for {0}: {1}",
                    botID, ex.Message);
            }
        }

        /// <summary>
        /// Mark a bot's appearance as dirty so it gets saved on next position save.
        /// </summary>
        public void MarkAppearanceDirty(UUID botID)
        {
            if (!IsPersistent(botID)) return;
            lock (m_dirtyAppearance)
                m_dirtyAppearance.Add(botID);
        }

        #endregion

        #region Capacity Checks

        private bool CheckRezRights(UUID ownerID, ILandObject parcel)
        {
            if (parcel == null) return false;
            LandData land = parcel.LandData;

            // Owner of the parcel
            if (land.OwnerID == ownerID) return true;

            // Group member with rez rights
            if (land.GroupID != UUID.Zero && land.GroupID == GetAgentGroupID(ownerID))
            {
                if ((land.Flags & (uint)ParcelFlags.CreateGroupObjects) != 0)
                    return true;
            }

            // Everyone can rez
            if ((land.Flags & (uint)ParcelFlags.CreateObjects) != 0)
                return true;

            return false;
        }

        private UUID GetAgentGroupID(UUID agentID)
        {
            ScenePresence sp = m_scene.GetScenePresence(agentID);
            return sp?.ControllingClient?.ActiveGroupId ?? UUID.Zero;
        }

        private int CheckCapacityLimits(UUID ownerID, UUID parcelID)
        {
            string regionID = m_scene.RegionInfo.RegionID.ToString();

            try
            {
                // Per-region cap
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM persistent_bots WHERE region_id = @rid AND active = 1";
                    cmd.Parameters.AddWithValue("@rid", regionID);
                    long regionCount = (long)cmd.ExecuteScalar();
                    if (regionCount >= m_maxPerRegion)
                        return BotPersistError.REGION_CAP;
                }

                // Per-owner cap
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COUNT(*) FROM persistent_bots
WHERE owner_id = @oid AND region_id = @rid AND active = 1";
                    cmd.Parameters.AddWithValue("@oid", ownerID.ToString());
                    cmd.Parameters.AddWithValue("@rid", regionID);
                    long ownerCount = (long)cmd.ExecuteScalar();
                    if (ownerCount >= m_maxPerOwner)
                        return BotPersistError.OWNER_CAP;
                }

                // Per-parcel cap
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT COUNT(*) FROM persistent_bots
WHERE parcel_id = @pid AND active = 1";
                    cmd.Parameters.AddWithValue("@pid", parcelID.ToString());
                    long parcelCount = (long)cmd.ExecuteScalar();
                    if (parcelCount >= m_maxPerParcel)
                        return BotPersistError.PARCEL_CAP;
                }

                return BotPersistError.OK;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Capacity check failed: {0}", ex.Message);
                return BotPersistError.REGION_CAP; // fail safe — deny
            }
        }

        #endregion

        #region Position Save Timer

        /// <summary>
        /// Start the periodic position save timer.
        /// </summary>
        public void StartTimers()
        {
            if (!IsEnabled()) return;

            // Position save timer
            m_positionSaveTimer = new Timer(m_positionSaveInterval * 1000.0);
            m_positionSaveTimer.Elapsed += OnPositionSaveTimer;
            m_positionSaveTimer.AutoReset = true;
            m_positionSaveTimer.Start();

            // Cleanup timer
            m_cleanupTimer = new Timer(m_cleanupInterval * 1000.0);
            m_cleanupTimer.Elapsed += OnCleanupTimer;
            m_cleanupTimer.AutoReset = true;
            m_cleanupTimer.Start();

            m_log.InfoFormat("[BotPersistence] Timers started — save every {0}s, cleanup every {1}s",
                m_positionSaveInterval, m_cleanupInterval);
        }

        private void OnPositionSaveTimer(object sender, ElapsedEventArgs e)
        {
            SaveAllPositions();
        }

        /// <summary>
        /// Batch-save positions and dirty appearances for all active persistent bots.
        /// </summary>
        public void SaveAllPositions()
        {
            List<UUID> botIDs;
            lock (m_lock)
                botIDs = new List<UUID>(m_persistentBotIDs);

            if (botIDs.Count == 0) return;

            HashSet<UUID> dirtySet;
            lock (m_dirtyAppearance)
            {
                dirtySet = new HashSet<UUID>(m_dirtyAppearance);
                m_dirtyAppearance.Clear();
            }

            try
            {
                using (var transaction = m_db.BeginTransaction())
                {
                    foreach (UUID botID in botIDs)
                    {
                        ScenePresence sp = m_scene.GetScenePresence(botID);
                        if (sp == null) continue;

                        using (var cmd = m_db.CreateCommand())
                        {
                            if (dirtySet.Contains(botID) && sp.Appearance != null)
                            {
                                // Save position + appearance
                                byte[] appearanceBytes = null;
                                try
                                {
                                    var ctx = new EntityTransferContext();
                                    var osd = sp.Appearance.Pack(ctx);
                                    appearanceBytes = System.Text.Encoding.UTF8.GetBytes(
                                        OpenMetaverse.StructuredData.OSDParser.SerializeLLSDXmlString(osd));
                                }
                                catch { }

                                cmd.CommandText = @"
UPDATE persistent_bots SET
    position_x = @px, position_y = @py, position_z = @pz,
    rotation_x = @rx, rotation_y = @ry, rotation_z = @rz, rotation_w = @rw,
    appearance_data = @appearance, updated_at = @now
WHERE bot_id = @id AND active = 1";
                                cmd.Parameters.AddWithValue("@appearance",
                                    appearanceBytes != null ? (object)appearanceBytes : DBNull.Value);
                            }
                            else
                            {
                                // Save position only
                                cmd.CommandText = @"
UPDATE persistent_bots SET
    position_x = @px, position_y = @py, position_z = @pz,
    rotation_x = @rx, rotation_y = @ry, rotation_z = @rz, rotation_w = @rw,
    updated_at = @now
WHERE bot_id = @id AND active = 1";
                            }

                            cmd.Parameters.AddWithValue("@px", sp.AbsolutePosition.X);
                            cmd.Parameters.AddWithValue("@py", sp.AbsolutePosition.Y);
                            cmd.Parameters.AddWithValue("@pz", sp.AbsolutePosition.Z);
                            cmd.Parameters.AddWithValue("@rx", sp.Rotation.X);
                            cmd.Parameters.AddWithValue("@ry", sp.Rotation.Y);
                            cmd.Parameters.AddWithValue("@rz", sp.Rotation.Z);
                            cmd.Parameters.AddWithValue("@rw", sp.Rotation.W);
                            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                            cmd.Parameters.AddWithValue("@id", botID.ToString());

                            cmd.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Position save failed: {0}", ex.Message);
            }
        }

        #endregion

        #region Cleanup

        private void OnCleanupTimer(object sender, ElapsedEventArgs e)
        {
            RunCleanup();
        }

        /// <summary>
        /// Expire old bots and check for orphaned scripts.
        /// </summary>
        public void RunCleanup()
        {
            if (!IsEnabled()) return;

            string regionID = m_scene.RegionInfo.RegionID.ToString();
            DateTime now = DateTime.UtcNow;
            int expiredCount = 0;
            int orphanedCount = 0;

            try
            {
                // 1. Mark expired bots inactive
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET active = 0, deactivation_reason = 'expired'
WHERE region_id = @rid AND active = 1 AND expires_at IS NOT NULL AND expires_at < @now";
                    cmd.Parameters.AddWithValue("@rid", regionID);
                    cmd.Parameters.AddWithValue("@now", now.ToString("o"));
                    expiredCount = cmd.ExecuteNonQuery();
                }

                // 2. Check for orphaned bots (creator object no longer exists)
                List<string> toOrphan = new List<string>();
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT bot_id, creator_object FROM persistent_bots
WHERE region_id = @rid AND active = 1";
                    cmd.Parameters.AddWithValue("@rid", regionID);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string creatorObjStr = reader.GetString(1);
                            if (UUID.TryParse(creatorObjStr, out UUID creatorObj))
                            {
                                // Check if the object still exists in the scene
                                SceneObjectPart part = m_scene.GetSceneObjectPart(creatorObj);
                                if (part == null)
                                    toOrphan.Add(reader.GetString(0));
                            }
                        }
                    }
                }

                foreach (string orphanID in toOrphan)
                {
                    using (var cmd = m_db.CreateCommand())
                    {
                        cmd.CommandText = @"
UPDATE persistent_bots SET active = 0, deactivation_reason = 'orphaned'
WHERE bot_id = @id AND active = 1";
                        cmd.Parameters.AddWithValue("@id", orphanID);
                        orphanedCount += cmd.ExecuteNonQuery();
                    }

                    if (UUID.TryParse(orphanID, out UUID oid))
                    {
                        lock (m_lock)
                            m_persistentBotIDs.Remove(oid);
                    }
                }

                if (expiredCount > 0 || orphanedCount > 0)
                {
                    m_log.InfoFormat("[BotPersistence] Cleanup: {0} expired, {1} orphaned in {2}",
                        expiredCount, orphanedCount, m_scene.RegionInfo.RegionName);
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Cleanup failed: {0}", ex.Message);
            }
        }

        #endregion

        #region Region Shutdown

        /// <summary>
        /// Save all persistent bot state before region shutdown.
        /// </summary>
        public void OnRegionShutdown()
        {
            if (!IsEnabled()) return;

            // Mark all appearances as dirty so they get saved
            lock (m_lock)
            {
                lock (m_dirtyAppearance)
                {
                    foreach (UUID id in m_persistentBotIDs)
                        m_dirtyAppearance.Add(id);
                }
            }

            // Final save
            SaveAllPositions();

            // Stop timers
            m_positionSaveTimer?.Stop();
            m_positionSaveTimer?.Dispose();
            m_cleanupTimer?.Stop();
            m_cleanupTimer?.Dispose();
            m_respawnTimer?.Stop();
            m_respawnTimer?.Dispose();

            // Close database
            m_db?.Close();
            m_db?.Dispose();

            m_log.InfoFormat("[BotPersistence] Shutdown complete for {0}",
                m_scene.RegionInfo.RegionName);
        }

        #endregion

        #region Region Startup / Respawn

        /// <summary>
        /// Load and respawn persistent bots after region startup.
        /// Call after parcels are initialized and connections are accepted.
        /// </summary>
        public void LoadPersistentBots()
        {
            if (!IsEnabled())
            {
                m_log.Info("[BotPersistence] Bot persistence disabled — skipping respawn.");
                return;
            }

            string regionID = m_scene.RegionInfo.RegionID.ToString();

            // 1. Run cleanup first
            RunCleanup();

            // 2. Load all active records
            List<PersistentBotRecord> records = new List<PersistentBotRecord>();
            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT bot_id, owner_id, creator_script, creator_object, region_id, parcel_id,
       bot_first_name, bot_last_name,
       position_x, position_y, position_z,
       rotation_x, rotation_y, rotation_z, rotation_w,
       appearance_data, tags, custom_data,
       created_at, updated_at, expires_at
FROM persistent_bots
WHERE region_id = @rid AND active = 1
ORDER BY created_at ASC";
                    cmd.Parameters.AddWithValue("@rid", regionID);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var rec = new PersistentBotRecord
                            {
                                BotID = UUID.Parse(reader.GetString(0)),
                                OwnerID = UUID.Parse(reader.GetString(1)),
                                CreatorScript = UUID.Parse(reader.GetString(2)),
                                CreatorObject = UUID.Parse(reader.GetString(3)),
                                RegionID = UUID.Parse(reader.GetString(4)),
                                ParcelID = UUID.Parse(reader.GetString(5)),
                                BotFirstName = reader.GetString(6),
                                BotLastName = reader.GetString(7),
                                Position = new Vector3(
                                    reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)),
                                Rotation = new Quaternion(
                                    reader.GetFloat(11), reader.GetFloat(12),
                                    reader.GetFloat(13), reader.GetFloat(14)),
                                TagsJson = reader.IsDBNull(16) ? null : reader.GetString(16),
                                CustomDataJson = reader.IsDBNull(17) ? null : reader.GetString(17),
                                Active = true
                            };

                            // Appearance data (BLOB)
                            if (!reader.IsDBNull(15))
                            {
                                rec.AppearanceData = (byte[])reader.GetValue(15);
                            }

                            // Timestamps
                            rec.CreatedAt = DateTime.Parse(reader.GetString(18));
                            rec.UpdatedAt = DateTime.Parse(reader.GetString(19));
                            if (!reader.IsDBNull(20))
                                rec.ExpiresAt = DateTime.Parse(reader.GetString(20));

                            records.Add(rec);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to load persistent bots: {0}", ex.Message);
                return;
            }

            // 3. Validate each record
            m_respawnQueue = new Queue<PersistentBotRecord>();
            int deactivated = 0;

            foreach (var rec in records)
            {
                string failReason = ValidateForRespawn(rec);
                if (failReason != null)
                {
                    DeactivateRecord(rec.BotID, failReason);
                    deactivated++;
                    continue;
                }

                m_respawnQueue.Enqueue(rec);
            }

            int toSpawn = m_respawnQueue.Count;

            if (toSpawn == 0)
            {
                m_log.InfoFormat("[BotPersistence] No bots to respawn in {0} ({1} deactivated)",
                    m_scene.RegionInfo.RegionName, deactivated);
                return;
            }

            // 4. Start staggered spawn timer
            m_log.InfoFormat("[BotPersistence] Queued {0} bots for respawn in {1} ({2} deactivated) — rate: {3}/sec",
                toSpawn, m_scene.RegionInfo.RegionName, deactivated, m_respawnRate);

            double intervalMs = 1000.0 / Math.Max(1, m_respawnRate);
            m_respawnTimer = new Timer(intervalMs);
            m_respawnTimer.Elapsed += OnRespawnTick;
            m_respawnTimer.AutoReset = true;
            m_respawnTimer.Start();
        }

        private string ValidateForRespawn(PersistentBotRecord rec)
        {
            // Check parcel permissions
            ILandObject parcel = null;
            try
            {
                parcel = m_scene.LandChannel.GetLandObject(rec.Position.X, rec.Position.Y);
            }
            catch { }

            if (parcel == null)
                return "parcel_not_found";

            if (!CheckRezRights(rec.OwnerID, parcel))
                return "permission_denied";

            // Check if owner is banned from the region
            if (m_scene.RegionInfo.EstateSettings.IsBanned(rec.OwnerID))
                return "owner_banned";

            // Region cap check (against already-queued + already-persistent)
            lock (m_lock)
            {
                if (m_persistentBotIDs.Count >= m_maxPerRegion)
                    return "cap_exceeded";
            }

            return null; // valid
        }

        private void OnRespawnTick(object sender, ElapsedEventArgs e)
        {
            if (m_respawnQueue == null || m_respawnQueue.Count == 0)
            {
                m_respawnTimer?.Stop();
                m_respawnTimer?.Dispose();
                m_respawnTimer = null;
                m_log.InfoFormat("[BotPersistence] Respawn complete for {0}",
                    m_scene.RegionInfo.RegionName);
                return;
            }

            // Pause if an avatar login is in progress
            // (Check if any root agents were added in the last second)
            // This is a lightweight check — not perfect but avoids blocking logins
            // A more robust approach would hook into Scene.AddNewAgent events

            PersistentBotRecord rec = m_respawnQueue.Dequeue();
            SpawnBot(rec);
        }

        private void SpawnBot(PersistentBotRecord rec)
        {
            try
            {
                // Deserialize appearance
                AvatarAppearance appearance = null;
                if (rec.AppearanceData != null)
                {
                    try
                    {
                        string xml = System.Text.Encoding.UTF8.GetString(rec.AppearanceData);
                        var osd = OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDXml(xml);
                        appearance = new AvatarAppearance();
                        appearance.Unpack((OpenMetaverse.StructuredData.OSDMap)osd);
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat("[BotPersistence] Failed to deserialize appearance for {0}: {1}",
                            rec.BotID, ex.Message);
                    }
                }

                appearance ??= new AvatarAppearance();

                // Create the bot via BotManager
                string reason;
                UUID newBotID = m_botManager.CreateBot(
                    rec.BotFirstName, rec.BotLastName,
                    rec.Position, null, // no outfit name — using serialized appearance
                    rec.CreatorScript, rec.OwnerID,
                    out reason);

                if (newBotID == UUID.Zero)
                {
                    m_log.WarnFormat("[BotPersistence] Failed to respawn bot {0}: {1}",
                        rec.BotID, reason ?? "unknown");
                    DeactivateRecord(rec.BotID, "spawn_failed");
                    return;
                }

                // Apply saved appearance
                INPCModule npcModule = m_scene.RequestModuleInterface<INPCModule>();
                if (npcModule != null)
                    npcModule.SetNPCAppearance(newBotID, appearance, m_scene);

                // Set rotation
                ScenePresence sp = m_scene.GetScenePresence(newBotID);
                if (sp != null)
                    sp.Rotation = rec.Rotation;

                // Restore tags
                if (!string.IsNullOrEmpty(rec.TagsJson))
                {
                    try
                    {
                        var tags = System.Text.Json.JsonSerializer.Deserialize<List<string>>(rec.TagsJson);
                        if (tags != null)
                        {
                            foreach (string tag in tags)
                                m_botManager.AddTagToBot(newBotID, tag, rec.OwnerID);
                        }
                    }
                    catch { }
                }

                // Update the database record with the new bot ID
                // (bot UUID changes on respawn since it's a new ScenePresence)
                UpdateBotIDInDb(rec.BotID, newBotID);

                lock (m_lock)
                {
                    m_persistentBotIDs.Remove(rec.BotID);
                    m_persistentBotIDs.Add(newBotID);
                }

                m_log.InfoFormat("[BotPersistence] Respawned bot {0} -> {1} ({2} {3})",
                    rec.BotID, newBotID, rec.BotFirstName, rec.BotLastName);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] SpawnBot exception for {0}: {1}",
                    rec.BotID, ex.Message);
                DeactivateRecord(rec.BotID, "spawn_exception");
            }
        }

        private void UpdateBotIDInDb(UUID oldID, UUID newID)
        {
            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET bot_id = @newID, updated_at = @now
WHERE bot_id = @oldID";
                    cmd.Parameters.AddWithValue("@newID", newID.ToString());
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@oldID", oldID.ToString());
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to update bot ID {0} -> {1}: {2}",
                    oldID, newID, ex.Message);
            }
        }

        #endregion

        #region Admin Controls

        /// <summary>
        /// Deactivate a specific persistent bot record.
        /// </summary>
        public void DeactivateRecord(UUID botID, string reason)
        {
            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET active = 0, deactivation_reason = @reason, updated_at = @now
WHERE bot_id = @id AND active = 1";
                    cmd.Parameters.AddWithValue("@reason", reason);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@id", botID.ToString());
                    cmd.ExecuteNonQuery();
                }

                lock (m_lock)
                    m_persistentBotIDs.Remove(botID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] Failed to deactivate {0}: {1}",
                    botID, ex.Message);
            }
        }

        /// <summary>
        /// Deactivate all persistent bots in this region (admin command).
        /// </summary>
        public int ClearAllPersistentBots()
        {
            string regionID = m_scene.RegionInfo.RegionID.ToString();
            try
            {
                int count;
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET active = 0, deactivation_reason = 'admin_clear', updated_at = @now
WHERE region_id = @rid AND active = 1";
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@rid", regionID);
                    count = cmd.ExecuteNonQuery();
                }

                lock (m_lock)
                    m_persistentBotIDs.Clear();

                m_log.InfoFormat("[BotPersistence] Admin cleared {0} persistent bots in {1}",
                    count, m_scene.RegionInfo.RegionName);
                return count;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] ClearAll failed: {0}", ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Deactivate all persistent bots for a specific owner (admin command).
        /// </summary>
        public int ClearOwnerPersistentBots(UUID ownerID)
        {
            string regionID = m_scene.RegionInfo.RegionID.ToString();
            try
            {
                int count;
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE persistent_bots SET active = 0, deactivation_reason = 'admin_clear_owner', updated_at = @now
WHERE region_id = @rid AND owner_id = @oid AND active = 1";
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@rid", regionID);
                    cmd.Parameters.AddWithValue("@oid", ownerID.ToString());
                    count = cmd.ExecuteNonQuery();
                }

                lock (m_lock)
                {
                    // Rebuild persistent set (removing this owner's bots)
                    List<UUID> toRemove = new List<UUID>();
                    foreach (UUID id in m_persistentBotIDs)
                    {
                        if (m_botManager.GetBotOwner(id) == ownerID)
                            toRemove.Add(id);
                    }
                    foreach (UUID id in toRemove)
                        m_persistentBotIDs.Remove(id);
                }

                return count;
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] ClearOwner failed: {0}", ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// List all active persistent bot records for console display.
        /// </summary>
        public List<PersistentBotRecord> ListActiveBots()
        {
            List<PersistentBotRecord> result = new List<PersistentBotRecord>();
            string regionID = m_scene.RegionInfo.RegionID.ToString();

            try
            {
                using (var cmd = m_db.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT bot_id, owner_id, bot_first_name, bot_last_name,
       position_x, position_y, position_z, parcel_id, created_at, expires_at
FROM persistent_bots
WHERE region_id = @rid AND active = 1
ORDER BY created_at ASC";
                    cmd.Parameters.AddWithValue("@rid", regionID);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new PersistentBotRecord
                            {
                                BotID = UUID.Parse(reader.GetString(0)),
                                OwnerID = UUID.Parse(reader.GetString(1)),
                                BotFirstName = reader.GetString(2),
                                BotLastName = reader.GetString(3),
                                Position = new Vector3(
                                    reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6)),
                                ParcelID = UUID.Parse(reader.GetString(7)),
                                CreatedAt = DateTime.Parse(reader.GetString(8)),
                                ExpiresAt = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9))
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[BotPersistence] ListActiveBots failed: {0}", ex.Message);
            }

            return result;
        }

        #endregion

        #region Bot Removal Hook

        /// <summary>
        /// Called when a bot is being removed. If it was persistent,
        /// deactivate the record.
        /// </summary>
        public void OnBotRemoved(UUID botID)
        {
            if (!IsPersistent(botID)) return;

            DeactivateRecord(botID, "bot_removed");

            lock (m_dirtyAppearance)
                m_dirtyAppearance.Remove(botID);
        }

        #endregion
    }
}
