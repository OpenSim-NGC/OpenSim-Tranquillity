/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions of the
 * BSD licence in the project root are met.
 */

using OpenMetaverse;
using Xunit;
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

    [Fact]
    public void NoTravelSessionIsRefused()
    {
        Assert.Equal(HomeLaunchDecision.RefuseNoSession, Hop(travelSessionExists: false));
    }

    [Fact]
    public void ASessionBelongingToAnotherUserIsRefused()
    {
        Assert.Equal(HomeLaunchDecision.RefuseUserMismatch, Hop(travelUser: SomeoneElse));
    }

    /// <summary>
    /// The live case. An origin region that has not been patched to forward the
    /// token sends nothing at all, so this is what every unpatched hop looks like,
    /// and it must refuse for that stated reason rather than by accident.
    /// </summary>
    [Fact]
    public void AnEmptyTokenIsRefusedAndNamedAsEmpty()
    {
        Assert.Equal(HomeLaunchDecision.RefuseWrongToken, Hop(presentedToken: ""));
        Assert.Equal(HomeLaunchDecision.RefuseWrongToken, Hop(presentedToken: null));
        Assert.Contains("EMPTY", HomeLaunchAuthorization.TokenProblem(""));
    }

    [Fact]
    public void AStaleTokenIsRefusedAndNamedAsDifferent()
    {
        Assert.Equal(HomeLaunchDecision.RefuseWrongToken,
                Hop(presentedToken: "http://playsim.net:8002;00000000-0000-4000-8000-0000000000ff"));
        Assert.Equal("a different token", HomeLaunchAuthorization.TokenProblem("something"));
    }

    /// <summary>
    /// Someone else's CURRENT token: well formed, unexpired, and genuinely issued
    /// by this grid — just not for this session. It is refused for the same reason
    /// as a stale one, because the only question asked is whether it matches the
    /// token this session was issued.
    /// </summary>
    [Fact]
    public void AValidTokenBelongingToAnotherSessionIsRefused()
    {
        const string AnotherSessionsToken = "http://playsim.net:8002;9c4e1a77-0000-4000-8000-000000000002";

        Assert.Equal(HomeLaunchDecision.RefuseWrongToken, Hop(presentedToken: AnotherSessionsToken));
    }

    [Fact]
    public void TheExactIssuedTokenIsAllowed()
    {
        Assert.Equal(HomeLaunchDecision.Allow, Hop());
    }

    [Fact]
    public void AnAgentWhoseRowSaysItIsAlreadyHereIsRefused()
    {
        Assert.Equal(HomeLaunchDecision.RefuseAlreadyHome, Hop(travelGrid: HomeGrid, targetGrid: HomeGrid));
    }

    /// <summary>
    /// The dangerous direction. A password login reaches the service in-process
    /// and has no prior trip by definition; if this ever refuses, first logins
    /// stop for everyone.
    /// </summary>
        [Fact]
    public void PasswordLoginWithNoTravelRowIsAllowed()
    {
        Assert.Equal(
            HomeLaunchDecision.Allow,
            HomeLaunchAuthorization.Decide(
                        fromLogin: true, travelSessionExists: false,
                        travelUserID: UUID.Zero, agentID: Traveller,
                        storedToken: null, presentedToken: null,
                        travelGridExternalName: null,
                homeGridName: HomeGrid, targetGridName: HomeGrid));
    }

        [Fact]
    public void PasswordLoginIsExemptFromEveryOtherCheck()
    {
        Assert.Equal(
            HomeLaunchDecision.Allow,
            HomeLaunchAuthorization.Decide(
                        fromLogin: true, travelSessionExists: true,
                        travelUserID: SomeoneElse, agentID: Traveller,
                        storedToken: IssuedToken, presentedToken: "",
                        travelGridExternalName: HomeGrid,
                homeGridName: HomeGrid, targetGridName: HomeGrid));
    }

    /// <summary>A token is a secret, so it is compared exactly.</summary>
        [Fact]
    public void TheTokenComparisonIsCaseSensitive()
    {
        Assert.Equal(
            HomeLaunchDecision.RefuseWrongToken,
            HomeLaunchAuthorization.Decide(
                        fromLogin: false, travelSessionExists: true,
                        travelUserID: Traveller, agentID: Traveller,
                        storedToken: "AbC", presentedToken: "abc",
                        travelGridExternalName: ForeignGrid,
                homeGridName: HomeGrid, targetGridName: ForeignGrid));
    }

    /// <summary>A grid name is a host name, and the two sides of a hop disagree about case.</summary>
    [Fact]
    public void TheGridNameComparisonIsCaseInsensitive()
    {
        Assert.Equal(HomeLaunchDecision.RefuseAlreadyHome, Hop(travelGrid: "PlaySim.NET", targetGrid: HomeGrid));
    }

    [Fact]
    public void AHopOutToAnotherGridIsAllowedEvenWhenTheRowNamesThisGrid()
    {
        Assert.Equal(HomeLaunchDecision.Allow, Hop(travelGrid: HomeGrid, targetGrid: ForeignGrid));
    }

    [Theory]
    [InlineData("http://home.example", "http://HOME.example:80/", "http://home.example/")]
    [InlineData("https://home.example/", "https://home.example:443", "https://HOME.example")]
    [InlineData("http://home.example:8002/", "http://HOME.example:8002", "http://home.example:8002")]
    public void EquivalentHomeGridUrisCannotBypassAlreadyHomeCheck(string homeGrid, string travelGrid, string targetGrid)
    {
        Assert.Equal(HomeLaunchDecision.RefuseAlreadyHome, HomeLaunchAuthorization.Decide(
                fromLogin: false, travelSessionExists: true,
                travelUserID: Traveller, agentID: Traveller,
                storedToken: IssuedToken, presentedToken: IssuedToken,
                travelGridExternalName: travelGrid, homeGridName: homeGrid, targetGridName: targetGrid));
    }

    [Fact]
    public void AnAuthorizedReturnFromAForeignGridIsAllowed()
    {
        Assert.Equal(HomeLaunchDecision.Allow, Hop(targetGrid: HomeGrid));
    }

        [Fact]
    public void EachRefusalCarriesItsReasonAndAnAllowedLaunchCarriesNone()
    {
        Assert.Equal("No authorized travel session",
            HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseNoSession));
        Assert.Equal("Agent is already on the home grid",
            HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseAlreadyHome));
        Assert.Empty(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.Allow));
    }

    /// <summary>
    /// What a caller is told must not say which check failed: naming it tells a
    /// determined caller exactly what to change next.
    /// </summary>
    [Fact]
    public void TheReasonGivenToACallerDoesNotNameWhichCheckFailed()
    {
        Assert.Equal(HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseWrongToken),
                HomeLaunchAuthorization.ReasonFor(HomeLaunchDecision.RefuseUserMismatch));
    }
}
