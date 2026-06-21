namespace RobloxMultiManager.Models;

/// <summary>
/// One public running server instance of a place, from
/// GET https://games.roblox.com/v1/games/{placeId}/servers/Public.
/// <see cref="JobId"/> is what the launcher passes as <c>gameId</c> to land an
/// account in this exact server (used to put several alts together for trading).
/// </summary>
public sealed record RobloxServer(string JobId, int Playing, int MaxPlayers)
{
    public int FreeSlots => Math.Max(0, MaxPlayers - Playing);
}
