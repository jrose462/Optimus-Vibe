using System.Net;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

/// <summary>Issue #85: a saved sign-in that still works must not be replaced by a new device-flow sign-in (each one mints a token; GitHub keeps 10).</summary>
public sealed class SignInDecisionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NoSavedToken_StartsDeviceFlow_WithoutCallingGitHub(string? token)
    {
        var called = false;
        var check = await SignInDecision.CheckAsync(token, _ => { called = true; return Task.FromResult("me"); });
        Assert.False(check.AlreadySignedIn);
        Assert.False(called);
    }

    [Fact]
    public async Task WorkingToken_IsKept()
    {
        string? seen = null;
        var check = await SignInDecision.CheckAsync("saved-token", t => { seen = t; return Task.FromResult("octocat"); });
        Assert.True(check.AlreadySignedIn);
        Assert.Equal("octocat", check.Login);
        Assert.Equal("saved-token", seen);
    }

    [Fact]
    public async Task RejectedToken_StartsDeviceFlow()
    {
        var check = await SignInDecision.CheckAsync("dead", _ => throw new GitHubApiException(HttpStatusCode.Unauthorized, "Bad credentials"));
        Assert.False(check.AlreadySignedIn);
        Assert.Null(check.Login);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(null)]
    public async Task OtherGitHubFailures_AreSurfaced_NotAnExcuseToMintAToken(HttpStatusCode? status)
    {
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            SignInDecision.CheckAsync("saved", _ => throw new GitHubApiException(status, "GitHub is unhappy")));
    }

    [Fact]
    public async Task NetworkError_IsSurfaced()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            SignInDecision.CheckAsync("saved", _ => throw new HttpRequestException("offline")));
    }

    [Fact]
    public async Task RealApiClient_AcceptedToken_IsKept()
    {
        var gh = new FakeGitHub();
        var ok = await SignInDecision.CheckAsync("test-token", t => gh.Api().GetLoginAsync());
        Assert.True(ok.AlreadySignedIn);
        Assert.Equal("tester", ok.Login);
    }
}
