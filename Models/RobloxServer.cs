namespace RobloxMultiManager.Models;

// ---- server model ----
public sealed record RobloxServer(string JobId, int Playing, int MaxPlayers)
{
    public int FreeSlots => Math.Max(0, MaxPlayers - Playing);
}
