namespace RobloxMultiManager.Models;

// ---- game model ----
public sealed record RobloxGame(long UniverseId, long PlaceId, string Name, int Playing, int UpVotes, int DownVotes);
