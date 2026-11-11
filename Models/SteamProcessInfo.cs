namespace Steamalt;

public sealed class SteamProcessInfo
{
    public int ProcessId { get; init; }
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = "Unknown profile";
    public string Status { get; init; } = "Running";
}
