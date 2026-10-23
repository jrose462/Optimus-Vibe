using System.Net;

namespace OptimusVibe.Sync;

/// <summary>Outcome of <see cref="SignInDecision.CheckAsync"/>.</summary>
public sealed record SignInCheck(bool AlreadySignedIn, string? Login);

/// <summary>
/// What to do when the user asks to sign in. GitHub keeps at most 10 OAuth tokens per user and app and revokes the oldest when an 11th is
/// minted, so a saved sign-in that still works is kept instead of minting another (issue #85).
/// </summary>
public static class SignInDecision
{
    /// <summary>
    /// No saved token, or GitHub answers 401: start the device flow. A token that works: nothing to do. Any other failure (offline,
    /// GitHub down) is rethrown so the caller reports it instead of minting a token it may not need.
    /// </summary>
    public static async Task<SignInCheck> CheckAsync(string? savedToken, Func<string, Task<string>> getLogin)
    {
        if (string.IsNullOrWhiteSpace(savedToken)) return new(false, null);
        try
        {
            return new(true, await getLogin(savedToken).ConfigureAwait(false));
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            return new(false, null);
        }
    }
}
