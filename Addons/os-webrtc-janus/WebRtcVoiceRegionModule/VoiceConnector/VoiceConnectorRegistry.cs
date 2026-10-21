/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;

namespace osWebRtcVoice;

/// <summary>What one LoadFrom produced: the loaded (enabled, valid) records, the refusals with
/// their reasons, and the disabled sections that were skipped (not refused, so an
/// operator can park a record with Enabled=false without it being an error).</summary>
public sealed class VoiceConnectorLoadResult
{
    public VoiceConnectorRegistry Registry { get; }
    public IReadOnlyList<(string SectionName, string Reason)> Refusals { get; }
    public IReadOnlyList<string> SkippedDisabled { get; }
    /// <summary>Records that loaded but lost a setting, with the reason (a CapabilitySecret too short
    /// to enforce with). The reason never contains the secret.</summary>
    public IReadOnlyList<(string SectionName, string Reason)> Warnings { get; }

    internal VoiceConnectorLoadResult(VoiceConnectorRegistry pRegistry,
        List<(string, string)> pRefusals, List<string> pSkippedDisabled, List<(string, string)> pWarnings = null)
    {
        Warnings = (IReadOnlyList<(string, string)>)pWarnings ?? Array.Empty<(string, string)>();
        Registry = pRegistry;
        Refusals = pRefusals;
        SkippedDisabled = pSkippedDisabled;
    }
}

/// <summary>
/// The voice-connector policy registry. Loaded once
/// from [VoiceConnector.&lt;name&gt;] ini sections; the records ARE the authorisation
/// (operator-only by construction). Thread-safe: reads and the
/// mutable-state writes all take the one internal lock, the A2ASessionRegistry discipline.
/// </summary>
public sealed class VoiceConnectorRegistry : IVoiceConnectorRegistry
{
    public const string SectionPrefix = "VoiceConnector.";

    private readonly object m_lock = new object();
    // Keyed by record Name (the section suffix). Insertion-ordered enumeration is not promised.
    private readonly Dictionary<string, VoiceConnectorRecord> m_records = new Dictionary<string, VoiceConnectorRecord>();

    private VoiceConnectorRegistry() { }

    /// <summary>
    /// Load every [VoiceConnector.&lt;name&gt;] section from the config source. Refusal reasons
    /// (disclosure layer i): NPC name not carrying pNpcNameToken as a whole word
    /// (first or last name); Scope other than estate; unparsable Position; empty first or last
    /// name; duplicate names. Disabled sections are skipped, not refused. A refused record never
    /// enters the registry — there is no partially-loaded state.
    /// </summary>
    public static VoiceConnectorLoadResult LoadFrom(IConfigSource pConfig, string pNpcNameToken)
    {
        VoiceConnectorRegistry registry = new VoiceConnectorRegistry();
        List<(string, string)> refusals = new List<(string, string)>();
        List<string> skipped = new List<string>();
        List<(string, string)> warnings = new List<(string, string)>();
        // Duplicate detection across sections: NPC full names must be unique (two connectors
        // sharing one in-world identity would be indistinguishable in every disclosure surface).
        Dictionary<string, string> fullNameOwners = new Dictionary<string, string>();

        if (pConfig is null)
            return new VoiceConnectorLoadResult(registry, refusals, skipped);

        foreach (IConfig section in pConfig.Configs)
        {
            if (section?.Name is null || !section.Name.StartsWith(SectionPrefix, StringComparison.Ordinal))
                continue;

            string name = section.Name.Substring(SectionPrefix.Length);
            if (string.IsNullOrWhiteSpace(name))
            {
                refusals.Add((section.Name, "section has no connector name after 'VoiceConnector.'"));
                continue;
            }

            if (!section.GetBoolean("Enabled", false))
            {
                skipped.Add(section.Name);
                continue;
            }

            string first = section.GetString("NpcFirstName", string.Empty)?.Trim() ?? string.Empty;
            string last = section.GetString("NpcLastName", string.Empty)?.Trim() ?? string.Empty;
            if (first.Length == 0 || last.Length == 0)
            {
                refusals.Add((section.Name, "NpcFirstName and NpcLastName must both be non-empty"));
                continue;
            }

            // Disclosure layer (i): the token must appear as a WHOLE WORD of the NPC's
            // name — in practice the first or the last name equal to it (ordinal, case-sensitive:
            // the marker the operator chose is the marker people are told to look for).
            if (!NameCarriesToken(first, last, pNpcNameToken))
            {
                refusals.Add((section.Name,
                    $"NPC name \"{first} {last}\" does not carry the NpcNameToken \"{pNpcNameToken}\" as a whole word (required for disclosure)"));
                continue;
            }

            string scopeRaw = section.GetString("Scope", "estate")?.Trim() ?? "estate";
            if (!scopeRaw.Equals("estate", StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add((section.Name, $"Scope must be \"estate\" (the only supported value); got \"{scopeRaw}\""));
                continue;
            }

            string posRaw = section.GetString("Position", string.Empty) ?? string.Empty;
            if (!Vector3.TryParse(posRaw, out Vector3 position))
            {
                refusals.Add((section.Name, $"Position unparsable: \"{posRaw}\" (expected <x, y, z>)"));
                continue;
            }

            string fullName = $"{first} {last}";
            if (fullNameOwners.TryGetValue(fullName, out string owner))
            {
                refusals.Add((section.Name, $"duplicate NPC name \"{fullName}\" (already used by connector \"{owner}\")"));
                continue;
            }
            if (registry.m_records.ContainsKey(name))
            {
                refusals.Add((section.Name, $"duplicate connector name \"{name}\""));
                continue;
            }

            bool mayInject = section.GetBoolean("MayInject", false);
            string authorisedBy = section.GetString("AuthorisedBy", string.Empty)?.Trim() ?? string.Empty;
            string injectUrl = section.GetString("InjectSourceUrl", null);
            if (string.IsNullOrWhiteSpace(injectUrl))
                injectUrl = null;
            string region = section.GetString("Region", null);
            if (string.IsNullOrWhiteSpace(region))
                region = null;
            // A weak key is never enforced with. The record still loads; it just
            // gets no join-capability endpoint, and the operator is told why.
            string capSecret = section.GetString("CapabilitySecret", null);
            if (string.IsNullOrEmpty(capSecret))
                capSecret = null;
            else if (capSecret.Length < VoiceConnectorJoinCapEndpoint.MinSecretLength)
            {
                warnings.Add((section.Name, $"CapabilitySecret is shorter than {VoiceConnectorJoinCapEndpoint.MinSecretLength} " +
                    "characters: the join-capability endpoint is NOT served for this record"));
                capSecret = null;
            }

            VoiceConnectorRecord record = new VoiceConnectorRecord(name, true, first, last,
                position, VoiceConnectorScope.Estate, mayInject, authorisedBy, injectUrl, region, capSecret);
            registry.m_records[name] = record;
            fullNameOwners[fullName] = name;
        }

        return new VoiceConnectorLoadResult(registry, refusals, skipped, warnings);
    }

    private static bool NameCarriesToken(string pFirst, string pLast, string pToken)
    {
        if (string.IsNullOrEmpty(pToken))
            return false;   // no token configured -> nothing can carry it; the record is refused loudly
        foreach (string word in $"{pFirst} {pLast}".Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, pToken, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // IVoiceConnectorRegistry — the one question the AllowNpcVoice guard asks. NpcId slots are
    // UUID.Zero until registration, so this is false until a connector registers.
    public bool IsConnectorIdentity(UUID pAgentId)
    {
        if (pAgentId == UUID.Zero)
            return false;
        lock (m_lock)
        {
            foreach (VoiceConnectorRecord r in m_records.Values)
            {
                if (r.NpcId == pAgentId)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Detached snapshot of the loaded records, for module logging.</summary>
    public List<VoiceConnectorRecord> Snapshot()
    {
        lock (m_lock)
            return new List<VoiceConnectorRecord>(m_records.Values);
    }

    public int Count
    {
        get { lock (m_lock) return m_records.Count; }
    }
}
