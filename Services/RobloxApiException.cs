namespace RobloxMultiManager.Services;

/// <summary>
/// Raised when a Roblox web request fails in a way the user should see
/// (invalid/expired cookie, rate-limit, ticket generation failure, etc.).
/// The <see cref="System.Exception.Message"/> is safe to show in the UI.
/// </summary>
public sealed class RobloxApiException : Exception
{
    public RobloxApiException(string message, Exception? inner = null)
        : base(message, inner) { }
}
