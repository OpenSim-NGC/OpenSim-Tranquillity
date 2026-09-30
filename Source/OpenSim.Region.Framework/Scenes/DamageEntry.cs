using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// PHLOX-10. One pending damage against a presence, as the SL damage pipeline sees it: who did it,
/// how much they asked for, what it was adjusted to, and what kind it was. A batch of these is what
/// <see cref="ScenePresence.ApplyDamage(System.Collections.Generic.List{DamageEntry}, bool)"/> applies
/// in one go - the physics frame's collisions, or a single scripted call - and what on_damage /
/// final_damage see through llDetectedDamage(n). <see cref="Amount"/> is the mutable one: llAdjustDamage
/// inside on_damage rewrites it; <see cref="OriginalDamage"/> keeps what was asked.
/// </summary>
public sealed class DamageEntry
{
    /// <summary>DAMAGE_TYPE_IMPACT: collisions, ground falls, llSetDamage prims.</summary>
    public const int TYPE_IMPACT = -1;
    /// <summary>DAMAGE_TYPE_GENERIC: the default for scripted damage.</summary>
    public const int TYPE_GENERIC = 0;

    /// <summary>The object that dealt it; UUID.Zero for the ground.</summary>
    public UUID SourceObject;
    /// <summary>Its owner; UUID.Zero for the ground.</summary>
    public UUID SourceOwner;
    /// <summary>Local id of the source, for TriggerAvatarKill's killer argument; 0 for the ground.</summary>
    public uint SourceLocalId;
    /// <summary>What was asked for. Never changes.</summary>
    public float OriginalDamage;
    /// <summary>What will be (on_damage) or was (final_damage) applied. llAdjustDamage writes it.</summary>
    public float Amount;
    /// <summary>A DAMAGE_TYPE_* value.</summary>
    public int DamageType;

    public DamageEntry(UUID sourceObject, UUID sourceOwner, uint sourceLocalId, float amount, int damageType)
    {
        SourceObject = sourceObject;
        SourceOwner = sourceOwner;
        SourceLocalId = sourceLocalId;
        OriginalDamage = amount;
        Amount = amount;
        DamageType = damageType;
    }

    public override string ToString() => $"{Amount}/{OriginalDamage} type {DamageType} from {SourceObject}";
}
