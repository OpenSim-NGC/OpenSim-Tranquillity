/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using System;

using OpenMetaverse;

namespace OpenSim.Services.HypergridService;

public enum HomeLaunchDecision
{
    Allow,
    RefuseNoSession,
    RefuseUserMismatch,
    RefuseWrongToken,
    RefuseAlreadyHome
}

/// <summary>
/// May this launch seat an agent on the home grid?
///
/// Lifted out of UserAgentService.LoginAgentToGrid unchanged, for one reason:
/// a rule that fails CLOSED has to be testable. This one refuses hypergrid
/// arrivals it cannot account for, so a mistake here does not show up as an
/// error — it shows up as residents who cannot get home, which is why the test
/// for "a password login with no travel row is ALLOWED" matters as much as any
/// of the refusals.
///
/// The order is the rule (DHPG plan hg_homeagent_session_bind, item 1):
///
///   a password login may launch from nothing;
///   anything else needs a travel session that already exists,
///   belonging to this user,
///   holding exactly the token we last issued,
///   and not already sitting on this grid.
///
/// Two comparisons are deliberate and differ from each other. The token is
/// compared ORDINAL — it is a secret, and 'A' is not 'a'. The grid name is
/// compared case-INSENSITIVELY, because it is a host name and the two sides of
/// a hypergrid hop disagree about capitalisation all the time.
/// </summary>
public static class HomeLaunchAuthorization
{
    public static HomeLaunchDecision Decide(
            bool fromLogin,
            bool travelSessionExists,
            UUID travelUserID,
            UUID agentID,
            string storedToken,
            string presentedToken,
            string travelGridExternalName,
            string homeGridName,
            string targetGridName)
    {
        // A password login reaches LoginAgentToGrid in-process and has no prior
        // trip by definition. Refusing here would stop every first login.
        if (fromLogin)
            return HomeLaunchDecision.Allow;

        if (!travelSessionExists)
            return HomeLaunchDecision.RefuseNoSession;

        if (travelUserID != agentID)
            return HomeLaunchDecision.RefuseUserMismatch;

        // An empty token is the signature of an origin region that has not been
        // patched to forward it. It is refused like any other wrong token, and
        // named separately in the log so the cause is readable.
        if (string.IsNullOrEmpty(presentedToken)
                || !string.Equals(storedToken, presentedToken, StringComparison.Ordinal))
        {
            return HomeLaunchDecision.RefuseWrongToken;
        }

        // A launch onto this grid for an agent whose travel row already says it is
        // here is not a hypergrid hop; local teleports do not go through /homeagent.
        if (homeGridName == targetGridName
                && string.Equals(travelGridExternalName, homeGridName, StringComparison.InvariantCultureIgnoreCase))
        {
            return HomeLaunchDecision.RefuseAlreadyHome;
        }

        return HomeLaunchDecision.Allow;
    }

    /// <summary>What the caller is told. Deliberately vaguer than the log line.</summary>
    public static string ReasonFor(HomeLaunchDecision decision)
    {
        switch (decision)
        {
            case HomeLaunchDecision.RefuseNoSession:
                return "No authorized travel session";
            case HomeLaunchDecision.RefuseAlreadyHome:
                return "Agent is already on the home grid";
            case HomeLaunchDecision.RefuseUserMismatch:
            case HomeLaunchDecision.RefuseWrongToken:
                return "Unauthorized";
            default:
                return string.Empty;
        }
    }

    /// <summary>The log wording for a refused token, which names the empty case.</summary>
    public static string TokenProblem(string presentedToken)
    {
        return string.IsNullOrEmpty(presentedToken)
                ? "an EMPTY token (origin region did not forward it)"
                : "a different token";
    }
}
