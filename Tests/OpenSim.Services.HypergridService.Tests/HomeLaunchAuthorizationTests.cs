/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using OpenMetaverse;

using OpenSim.Services.HypergridService;

namespace OpenSim.Services.HypergridService.Tests;

/// <summary>
/// Item 6 of DHPG's plan hg_homeagent_session_bind: the rule that decides
/// whether a hypergrid launch may seat an agent on the home grid.
///
/// The rule FAILS CLOSED, which is what makes these worth writing. A mistake in
/// the refusing direction does not raise an error anywhere — it appears as
/// residents who cannot get home, or, in the worst case
/// (<see cref="PasswordLoginWithNoTravelRowIsAllowed"/>), as nobody being able
/// to log in at all.
///
/// These were written against the extraction, then checked by breaking it:
/// removing the token comparison fails the three token tests, treating a missing
/// travel row as acceptable fails <see cref="NoTravelSessionIsRefused"/>, and
/// dropping the password-login exemption fails both login tests.
/// </summary>
[TestFixture]
public class HomeLaunchAuthorizationTests
{
    private static readonly UUID Traveller = new("00000000-0000-4000-8000-00000000000a");
    private static readonly UUID SomeoneElse = new("00000000-0000-4000-8000-00000000000b");

    private const string HomeGrid = "playsim.net";
    private const string ForeignGrid = "playground.darkheartsos.com:8002";
    private const string IssuedToken = "http://playsim.net:8002;2f1d4c9e-0000-4000-8000-000000000001";

    /// <summary>A launch arriving over /homeagent — anything that is not a password login.</summary>
    private static HomeLaunchDecision Hop(
            bool travelSessionExists = true,
            UUID? travelUser = null,
            string storedToken = IssuedToken,
            string presentedToken = IssuedToken,
            string travelGrid = ForeignGrid,
            string targetGrid = ForeignGrid)
    {
        return HomeLaunchAuthorization.Decide(
                fromLogin: false,
                travelSessionExists: travelSessionExists,
                travelUserID: travelUser ?? Traveller,
                agentID: Traveller,
                storedToken: storedToken,
                presentedToken: presentedToken,
                travelGridExternalName: travelGrid,
                homeGridName: HomeGrid,
                targetGridName: targetGrid);
    }

    [Test]
    public void NoTravelSessionIsRefused()
    {
        Assert.That(Hop(travelSessionExists: false), Is.EqualTo(HomeLaunchDecision.RefuseNoSession));
    }

    [Test]
    public void ASessionBelongingToAnotherUserIsRefused()
    {
        Assert.That(Hop(travelUser: SomeoneElse), Is.EqualTo(HomeLaunchDecision.RefuseUserMismatch));
    }

    /// <summary>
    /// The live case. An origin region that has not been patched to forward the
    /// token sends nothing at all, so this is what every unpatched hop looks like,
    /// and it must refuse for that stated reason rather than by accident.
    /// </summary>
    [Test]
    public void AnEmptyTokenIsRefusedAndNamedAsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Hop(presentedToken: ""), Is.EqualTo(HomeLaunchDecision.RefuseWrongToken));
            Assert.That(Hop(presentedToken: null), Is.EqualTo(HomeLaunchDecision.RefuseWrongToken));
            Assert.That(HomeLaunchAuthorization.TokenProblem(""), Does.Contain("EMPTY"));
        });
    }

    [Test]
    public void AStaleTokenIsRefusedAndNamedAsDifferent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Hop(presentedToken: "http://playsim.net:8002;00000000-0000-4000-8000-0000000000ff"),
                    Is.EqualTo(HomeLaunchDecision.RefuseWrongToken));
            Assert.That(HomeLaunchAuthorization.TokenProblem("something"), Is.EqualTo("a different token"));
        });
    }

    /// <summary>
    /// Someone else's CURRENT token: well formed, unexpired, and genuinely issued
    /// by this grid — just not for this session. It is refused for the same reason
    /// as a stale one, because the only question asked is whether it matches the
    /// token this session was issued.
    /// </summary>
    [Test]
    public void AValidTokenBelongingToAnotherSessionIsRefused()
    {
        const string AnotherSessionsToken = "http://playsim.net:8002;9c4e1a77-0000-4000-8000-000000000002";

        Assert.That(Hop(presentedToken: AnotherSessionsToken),
                Is.EqualTo(HomeLaunchDecision.RefuseWrongToken));
    }

    [Test]
    public void TheExactIssuedTokenIsAllowed()
    {
        Assert.That(Hop(), Is.EqualTo(HomeLaunchDecision.Allow));
    }

    [Test]
    public void AnAgentWhoseRowSaysItIsAlreadyHereIsRefused()
    {
        Assert.That(Hop(travelGrid: HomeGrid, targetGrid: HomeGrid),
                Is.EqualTo(HomeLaunchDecision.RefuseAlreadyHome));
    }

    /// <summary>
    /// The dangerous direction. A password login reaches the service in-process
    /// and has no prior trip by definition; if this ever refuses, first logins
    /// stop for everyone.
    /// </summary>
    [Test]
    public void PasswordLoginWithNoTravelRowIsAllowed()
    {
        Assert.That(
                HomeLaunchAuthorization.Decide(
                        fromLogin: true, travelSessionExists: false,
                        travelUserID: UUID.Zero, agentID: Traveller,
                        storedToken: null, presentedToken: null,
                        travelGridExternalName: null,
                        homeGridName: HomeGrid, targetGridName: HomeGrid),
                Is.EqualTo(HomeLaunchDecision.Allow));
    }

    [Test]
    public void PasswordLoginIsExemptFromEveryOtherCheck()
    {
        Assert.That(
                HomeLaunchAuthorization.Decide(
                        fromLogin: true, travelSessionExists: true,
                        travelUserID: SomeoneElse, agentID: Traveller,
                        storedToken: IssuedToken, presentedToken: "",
                        travelGridExternalName: HomeGrid,
                        homeGridName: HomeGrid, targetGridName: HomeGrid),
                Is.EqualTo(HomeLaunchDecision.Allow));
    }

    /// <summary>A token is a secret, so it is compared exactly.</summary>
    [Test]
    public void TheTokenComparisonIsCaseSensitive()
    {
        Assert.That(
                HomeLaunchAuthorization.Decide(
                        fromLogin: false, travelSessionExists: true,
                        travelUserID: Traveller, agentID: Traveller,
                        storedToken: "AbC", presentedToken: "abc",
                        travelGridExternalName: ForeignGrid,
                        homeGridName: HomeGrid, targetGridName: ForeignGrid),
                Is.EqualTo(HomeLaunchDecision.RefuseWrongToken));
    }

    /// <summary>A grid name is a host name, and the two sides of a hop disagree about case.</summary>
    [Test]
    public void TheGridNameComparisonIsCaseInsensitive()
    {
        Assert.That(Hop(travelGrid: "PlaySim.NET", targetGrid: HomeGrid),
                Is.EqualTo(HomeLaunchDecision.RefuseAlreadyHome));
    }

    [Test]
    public void AHopOutToAnotherGridIsAllowedEvenWhenTheRowNamesThisGrid()
    {
        Assert.That(Hop(travelGrid: HomeGrid, targetGrid: ForeignGrid),
                Is.EqualTo(HomeLaunchDecision.Allow));
    }

    [Test]
    public void EachRefusalCarriesItsReasonAndAnAllowedLaunchCarriesNone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseNoSession),
                    Is.EqualTo("No authorized travel session"));
            Assert.That(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseAlreadyHome),
                    Is.EqualTo("Agent is already on the home grid"));
            Assert.That(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.Allow), Is.Empty);
        });
    }

    /// <summary>
    /// What a caller is told must not say which check failed: naming it tells a
    /// determined caller exactly what to change next.
    /// </summary>
    [Test]
    public void TheReasonGivenToACallerDoesNotNameWhichCheckFailed()
    {
        Assert.That(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseUserMismatch),
                Is.EqualTo(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseWrongToken)));
    }
}
