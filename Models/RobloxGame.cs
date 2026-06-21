namespace RobloxMultiManager.Models;

/// <summary>
/// One game/experience from Roblox search or discovery. <see cref="PlaceId"/> is the
/// rootPlaceId you launch into; <see cref="UniverseId"/> is used for icons/details.
/// </summary>
public sealed record RobloxGame(long UniverseId, long PlaceId, string Name, int Playing, int UpVotes, int DownVotes);
