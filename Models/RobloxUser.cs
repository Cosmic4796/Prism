namespace RobloxMultiManager.Models;

/// <summary>
/// Minimal authenticated-user payload from
/// GET https://users.roblox.com/v1/users/authenticated.
/// <see cref="Name"/> is the @username; <see cref="DisplayName"/> is the display name.
/// </summary>
public sealed record RobloxUser(long Id, string Name, string DisplayName);
