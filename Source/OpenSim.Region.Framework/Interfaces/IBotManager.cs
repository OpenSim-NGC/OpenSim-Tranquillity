/*
 * Copyright (c) Legion Grid Contributors
 * IBotManager.cs — Bot/NPC management interface for Phlox script engine
 * Bridges InWorldz-style bot* LSL functions to OpenSim's INPCModule infrastructure.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Result codes for bot movement operations.
    /// </summary>
    public enum BotMovementResult
    {
        Success = 0,
        BotNotFound = -1,
        UserNotFound = -2,
        Error = -3
    }

    /// <summary>
    /// Travel modes for bot navigation waypoints.
    /// Values match BOT_TRAVELMODE_* LSL constants.
    /// </summary>
    public enum TravelMode
    {
        Walk = 1,
        Run = 2,
        Fly = 3,
        Teleport = 4,
        Wait = 5
    }

    /// <summary>
    /// Interface for managing scripted bots (NPCs controlled via LSL bot* functions).
    /// Implemented by BotManager which wraps INPCModule with additional tracking
    /// for tags, speed, profiles, outfits, navigation, and event registration.
    /// </summary>
    public interface IBotManager
    {
        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>Create a bot with the given name, position, and outfit.</summary>
        /// <returns>The bot's UUID, or UUID.Zero on failure.</returns>
        UUID CreateBot(string firstName, string lastName, Vector3 startPos,
            string outfitName, UUID scriptItemID, UUID ownerID, out string reason);

        /// <summary>Remove a bot from the region.</summary>
        void RemoveBot(UUID botID, UUID ownerID);

        /// <summary>Check whether the given UUID is a bot managed by this module.</summary>
        bool IsBot(UUID userID);

        /// <summary>Get the display name of a bot.</summary>
        string GetBotName(UUID botID);

        /// <summary>Get the owner of a bot.</summary>
        UUID GetBotOwner(UUID botID);

        /// <summary>Check if callerID has permission to control botID.</summary>
        bool CheckPermission(UUID botID, UUID callerID);

        /// <summary>Get all bot UUIDs in the region.</summary>
        List<UUID> GetAllBots();

        /// <summary>Get all bot UUIDs owned by a specific owner.</summary>
        List<UUID> GetAllOwnedBots(UUID ownerID);

        // ── Movement & Navigation ────────────────────────────────────────

        /// <summary>Set waypoint navigation for a bot.</summary>
        void SetBotNavigationPoints(UUID botID, List<Vector3> positions,
            List<TravelMode> travelModes, Dictionary<int, object> options, UUID ownerID);

        /// <summary>Start following an avatar.</summary>
        BotMovementResult StartFollowingAvatar(UUID botID, UUID targetID,
            Dictionary<int, object> options, UUID ownerID);

        /// <summary>Stop all movement.</summary>
        void StopMovement(UUID botID, UUID ownerID);

        /// <summary>Pause current movement.</summary>
        void PauseBotMovement(UUID botID, UUID ownerID);

        /// <summary>Resume paused movement.</summary>
        void ResumeBotMovement(UUID botID, UUID ownerID);

        /// <summary>Set bot movement speed multiplier.</summary>
        void SetBotSpeed(UUID botID, float speed, UUID ownerID);

        /// <summary>Wander randomly within an area.</summary>
        void WanderWithin(UUID botID, Vector3 origin, Vector3 distances,
            Dictionary<int, object> options, UUID ownerID);

        /// <summary>Get the bot's current position.</summary>
        Vector3 GetBotPosition(UUID botID, UUID ownerID);

        /// <summary>Teleport bot to a position.</summary>
        void SetBotPosition(UUID botID, Vector3 position, UUID ownerID);

        /// <summary>Set the bot's facing rotation.</summary>
        void SetBotRotation(UUID botID, Quaternion rotation, UUID ownerID);

        // ── Chat ─────────────────────────────────────────────────────────

        /// <summary>Make the bot chat (whisper/say/shout/typing).</summary>
        void BotChat(UUID botID, int channel, string message,
            ChatTypeEnum chatType, UUID ownerID);

        /// <summary>Send an IM from the bot to a user.</summary>
        void SendInstantMessageForBot(UUID botID, UUID targetID,
            string message, UUID ownerID);

        // ── Interaction ──────────────────────────────────────────────────

        /// <summary>Make the bot sit on an object.</summary>
        void SitBotOnObject(UUID botID, UUID objectID, UUID ownerID);

        /// <summary>Make the bot stand up.</summary>
        void StandBotUp(UUID botID, UUID ownerID);

        /// <summary>Make the bot touch an object.</summary>
        void BotTouchObject(UUID botID, UUID objectID, UUID ownerID);

        /// <summary>Give an inventory item from the bot's host prim.</summary>
        void GiveInventoryObject(UUID botID, SceneObjectPart host,
            string objName, UUID objID, byte assetType, UUID destID, UUID ownerID);

        // ── Animations ───────────────────────────────────────────────────

        /// <summary>Start playing an animation on the bot.</summary>
        void StartBotAnimation(UUID botID, UUID animID, string animName, UUID hostID, UUID ownerID);

        /// <summary>Stop playing an animation on the bot.</summary>
        void StopBotAnimation(UUID botID, UUID animID, string animName, UUID ownerID);

        // ── Profile ──────────────────────────────────────────────────────

        /// <summary>Set bot profile fields.</summary>
        void SetBotProfile(UUID botID, string aboutText, string email,
            UUID? imageID, string profileURL, UUID ownerID);

        // ── Outfits ──────────────────────────────────────────────────────

        /// <summary>Save current avatar appearance as a named outfit.</summary>
        void SaveOutfitToDatabase(UUID ownerID, string outfitName, out string reason);

        /// <summary>Remove a saved outfit.</summary>
        void RemoveOutfitFromDatabase(UUID ownerID, string outfitName);

        /// <summary>Change a bot's appearance to a saved outfit.</summary>
        void ChangeBotOutfit(UUID botID, string outfitName, UUID ownerID, out string reason);

        /// <summary>Get all outfit names owned by an owner.</summary>
        List<string> GetBotOutfitsByOwner(UUID ownerID);

        // ── Tagging ──────────────────────────────────────────────────────

        /// <summary>Add a tag to a bot.</summary>
        void AddTagToBot(UUID botID, string tag, UUID ownerID);

        /// <summary>Remove a tag from a bot.</summary>
        void RemoveTagFromBot(UUID botID, string tag, UUID ownerID);

        /// <summary>Check if a bot has a specific tag.</summary>
        bool BotHasTag(UUID botID, string tag);

        /// <summary>Get all tags on a bot.</summary>
        List<string> GetBotTags(UUID botID);

        /// <summary>Get all bots that have a specific tag.</summary>
        List<UUID> GetBotsWithTag(string tag);

        /// <summary>Remove all bots with a specific tag (owned by caller).</summary>
        void RemoveBotsWithTag(string tag, UUID ownerID);

        // ── Event Registration ───────────────────────────────────────────

        /// <summary>Register for path update events from a bot.</summary>
        void BotRegisterForPathUpdateEvents(UUID botID, UUID scriptItemID, UUID ownerID);

        /// <summary>Deregister from path update events.</summary>
        void BotDeregisterFromPathUpdateEvents(UUID botID, UUID scriptItemID, UUID ownerID);

        /// <summary>Register for collision events from a bot.</summary>
        void BotRegisterForCollisionEvents(UUID botID, SceneObjectGroup hostGroup, UUID ownerID);

        /// <summary>Deregister from collision events.</summary>
        void BotDeregisterFromCollisionEvents(UUID botID, SceneObjectGroup hostGroup, UUID ownerID);
    }
}
