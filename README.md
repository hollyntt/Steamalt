# Steamalt

Steamalt is a Linux-only launcher for starting Steam and Steam games through Firejail. Each named launch profile has a persistent sandbox home, while using the host Steam installation and game libraries.

## Requirements

- Linux (x64 or ARM64)
- .NET 10
- Steam and Firejail installed and available in `PATH`
- Steam installed on the host, with games installed in its libraries

Steamalt does not install or update Steam or games. It reuses the host Steam client, its login, and its installed game libraries; the profiles do not provide separate Steam accounts.

## Usage

Add a launch profile, enter the game's Steam App ID, and select **Launch game**. Select **Start Steam** to open the host Steam client inside Firejail. Profiles use stable homes under `$XDG_DATA_HOME/steamalt/profiles` (or `~/.local/share/steamalt/profiles` when `XDG_DATA_HOME` is unset). They keep per-profile sandbox files, but Steam account state and game libraries are shared with the host Steam installation. Removing a profile does not delete its profile home.

To use a non-Steam game, select **Start Steam**, then add it in that Steam window using **Games > Add a Non-Steam Game to My Library**. Launch it from Steam while that Firejail session is running. Steamalt does not add game entries itself. The host home is visible in Steam's file picker; it is read-only except for the Steam installation and registered game libraries, which Steam may update.

Launching a game starts:

```text
firejail --ignore=seccomp --ignore=whitelist --ignore=blacklist \
  --profile=steam --allow-bwrap \
  --env=HOME=<profile-home> \
  --read-only=<host-home> --read-write=<profile-home> \
  --read-write=<steam-install> --read-write=<steam-library> \
  -- steam -applaunch <app-id>
```

Steamalt disables Firejail's seccomp filter so Proton's pressure-vessel `bwrap` can use `mount_setattr`, and enables nested bwrap. It ignores the Steam profile's whitelist and blacklist rules, which otherwise make host game paths such as `~/Applications` inaccessible to Steam. The host home is still mounted read-only, except for the host Steam installation and registered Steam libraries, which are writable.

**Start Steam** runs the same command without `-applaunch <app-id>`. Steamalt uses the `firejail` and `steam` executables from `PATH`. Profiles are saved to `$XDG_CONFIG_HOME/steamalt/config.json` (or `~/.config/steamalt/config.json` when `XDG_CONFIG_HOME` is unset).

## Build

```sh
dotnet publish Steamalt.csproj -c Release -r linux-x64 --self-contained false
```

Use `-r linux-arm64` to publish for ARM64 Linux.
