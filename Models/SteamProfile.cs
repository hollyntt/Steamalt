namespace Steamalt;

public sealed class SteamProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profile";
}

public sealed class LauncherConfig
{
    public List<SteamProfile> Profiles { get; set; } = new();
}
