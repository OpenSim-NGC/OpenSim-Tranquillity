using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// An in-memory IAvatarService over the same key/value shape as the Avatars table: one string map per principal.
/// Only the three calls the bake index uses do anything — GetAvatar, SetItems, RemoveItems — which is the point:
/// if a test passes against this, the production path needs no service change either.
///
/// <para>
/// <see cref="SetAppearance"/> reproduces the real service's behaviour faithfully, delete-everything-first
/// included (AvatarService.cs:93), because that is the hazard the bake index lives with.
/// </para>
/// </summary>
public sealed class FakeAvatarService : IAvatarService
{
    public readonly Dictionary<UUID, Dictionary<string, string>> Records = new();
    public int SetItemsCalls;

    private Dictionary<string, string> Record(UUID id)
    {
        if (!Records.TryGetValue(id, out var d)) Records[id] = d = new Dictionary<string, string>();
        return d;
    }

    public AvatarData GetAvatar(UUID userID)
        => new() { AvatarType = 1, Data = new Dictionary<string, string>(Record(userID)) };

    public bool SetAvatar(UUID userID, AvatarData avatar)
    {
        Records[userID] = new Dictionary<string, string>(avatar.Data);   // AvatarService.SetAvatar deletes every row first
        return true;
    }

    public AvatarAppearance GetAppearance(UUID userID) => GetAvatar(userID).ToAvatarAppearance();
    public bool SetAppearance(UUID userID, AvatarAppearance appearance) => SetAvatar(userID, new AvatarData(appearance));

    public bool ResetAvatar(UUID userID) => Records.Remove(userID);

    public bool SetItems(UUID userID, string[] names, string[] values)
    {
        if (names.Length != values.Length) return false;
        SetItemsCalls++;
        var d = Record(userID);
        for (var i = 0; i < names.Length; i++) d[names[i]] = values[i];
        return true;
    }

    public bool RemoveItems(UUID userID, string[] names)
    {
        var d = Record(userID);
        foreach (var n in names) d.Remove(n);
        return true;
    }
}
