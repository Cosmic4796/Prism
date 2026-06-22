namespace RobloxMultiManager.Services;

// ---- exception ----
public sealed class RobloxApiException : Exception
{
    public RobloxApiException(string message, Exception? inner = null)
        : base(message, inner) { }
}
