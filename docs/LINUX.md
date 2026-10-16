# Running Ostraplan on Linux

Ostraplan is a Windows app, but it runs on Linux under [Wine](https://www.winehq.org/). Linux support
comes from the community: Windows is still the platform every release is built and tested on, and Linux
gets checked when someone reports a problem. Bug reports from Linux are welcome. Please say you are on
Linux and include the details listed under [Reporting a problem](#reporting-a-problem).

These steps were tested on Ubuntu 24.04 with Wine 9.0 and Ostraplan 1.31.1. The Fedora and Arch commands
are the usual equivalents and have not been tested.

## What you need

- **Ostranauts installed through Steam.** The game has no native Linux build, so Steam runs it through
  Proton. Ostraplan reads the game's data from that install.
- **Wine 9.0 or newer, with 32-bit support.** Ostraplan itself is 64-bit, but the small launcher and
  updater that come with it are 32-bit.
- **winetricks**, to install the fonts Ostraplan needs.

## 1. Install Wine and winetricks

Debian and Ubuntu need the 32-bit architecture turned on before 32-bit Wine can be installed:

```bash
sudo dpkg --add-architecture i386
sudo apt update
sudo apt install wine wine32:i386 winetricks
```

Fedora:

```bash
sudo dnf install wine winetricks
```

Arch:

```bash
sudo pacman -S wine winetricks
```

Check the version with `wine --version`.

## 2. Make a Wine prefix for Ostraplan

A prefix is a self-contained Windows environment. Giving Ostraplan its own keeps it apart from anything else
you run under Wine.

```bash
export WINEPREFIX="$HOME/.local/share/wineprefixes/ostraplan"
export WINEARCH=win64
wineboot --init
```

If Wine offers to install Mono or Gecko, you can cancel both: Ostraplan uses neither. The prefix already
reports itself as Windows 10, so there is nothing to change in `winecfg`.

## 3. Install the fonts

```bash
export WINEPREFIX="$HOME/.local/share/wineprefixes/ostraplan"
winetricks corefonts
```

This step is required. Without these fonts Ostraplan closes as soon as its main window opens.

## 4. Download and unzip Ostraplan

Download `Ostraplan-win-Portable.zip` from the [Releases](https://github.com/Valtora/Ostraplan/releases)
page, then unzip it:

```bash
mkdir -p ~/apps/ostraplan
unzip ~/Downloads/Ostraplan-win-Portable.zip -d ~/apps/ostraplan
```

## 5. Run it

```bash
export WINEPREFIX="$HOME/.local/share/wineprefixes/ostraplan"
wine ~/apps/ostraplan/Ostraplan.exe
```

The terminal shows some log lines as it starts. Lines about `ntlm_auth` and "no NTLM support, expect problems"
are about a Windows network login feature Ostraplan does not use, and are safe to ignore. Installing your
distribution's `winbind` package makes them go away.

To launch it without typing that each time, save those two lines in a script, for example
`~/apps/ostraplan/ostraplan.sh`, and make it executable with `chmod +x`.

## Finding the game and your saves

Ostraplan finds a Steam install of the game by itself. It looks in `~/.steam/steam`, `~/.local/share/Steam`,
the Flatpak Steam folder and the Snap one, and in every Steam library those list, so a game on another drive
is found too.

It reads your saves from the folder Proton keeps for Ostranauts, under
`steamapps/compatdata/1022980/pfx/drive_c/users/steamuser/AppData/LocalLow/Blue Bottle Games/Ostranauts`
in the library the game is installed in.

If the game is somewhere else, Ostraplan asks for the folder. Wine shows your Linux files as the `Z:` drive,
so the usual location is:

```
Z:\home\<your user name>\.steam\steam\steamapps\common\Ostranauts
```

and a saves folder picked by hand in Settings is usually:

```
Z:\home\<your user name>\.steam\steam\steamapps\compatdata\1022980\pfx\drive_c\users\steamuser\AppData\LocalLow\Blue Bottle Games\Ostranauts\Saves
```

## Updates

Updates work the same as on Windows. Ostraplan downloads a new version in the background and shows a
**Restart to update** button. They need the 32-bit Wine support from step 1.

## Where Ostraplan keeps its files

Settings and the activity log are inside the prefix, at
`~/.local/share/wineprefixes/ostraplan/drive_c/users/<your user name>/AppData/Roaming/Ostraplan`.

## Known differences

- Text uses substitute fonts, because Windows' Segoe UI is not available under Wine, so it looks slightly
  different from the screenshots.
- **Show in folder** opens Wine's own file browser rather than your desktop's.
- Not yet tried: the Steam Deck, Steam installed through Flatpak or Snap, and Workshop mods. Reports either
  way are useful.

## Reporting a problem

Open an issue on [GitHub](https://github.com/Valtora/Ostraplan/issues) and include:

- your distribution and its version,
- the output of `wine --version`,
- the activity log, `audit.log` in the folder above.
