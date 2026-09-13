# Ap.Control

## How to use

You can download the latest release from the [releases page](https://github.com/Alias-Cynestal/ArchipelagoControlClient/releases/). There are two executables that are necessary for the Archipelago to be ran properly: the patcher and the client.

## Patcher
To use the patcher, open a Terminal window and run the following command:
```cmd
Ap.Control.Patcher.exe apply all
```
This will modify your game files to be compatible with the Archipelago client. You can always run the patcher again if you need to revert the changes with the following command:
```cmd
Ap.Control.Patcher.exe restore all
```
For more information on the patcher, you can run the following command:
```cmd
Ap.Control.Patcher.exe --help
```
### Why do I need to patch my game?
Great question! The patcher modifies your game files for two specifics reasons. The first is to allow a new way of controlling the sectors available, which means that they can be unlocked in the Archipelago.
The second reason is to lock the normal way of unlocking weapons (the Astral Constructs menu), which means you'll need to use the Archipelago to unlock them.

## macOS

The macOS release is one archive, `Ap.Control-osx-arm64.tar.gz` (Apple silicon; the game has no
Intel build). Extract it and keep the four files together.

**1. Patch the game.** Same commands as Windows, without the `.exe`:
```sh
./Ap.Control.Patcher apply all
```
The patcher finds `Control/Game.app` in your Steam library on its own. If it does not, pass
`--game` pointing at the `Control` folder, at `Game.app`, or at `Game.app/Contents/Resources` —
any of the three works.

**2. Install the launch wrapper.** Copy `libapcontrol.dylib` and `apcontrol-launch.sh` into
`~/Library/Application Support/Ap.Control/`, make the script executable, and set Control's Steam
**Launch Options** to:

    "/Users/YOUR-NAME/Library/Application Support/Ap.Control/apcontrol-launch.sh" %command%

From a source checkout, `make -C native/apshim install` does the copying and prints the exact line
to paste.

The wrapper exists because Steam on macOS starts games through LaunchServices, which drops the
environment — so the usual `VAR=value %command%` trick does nothing here. It loads a small helper
library into the game; without it, the game runs exactly as it always did.

**3. Run the client**, then launch the game from Steam as usual.

### What works on macOS today
Security clearance, sector and door unlocks, elevator gating, the in-game Archipelago page, and
location tracking. Inventory items and ability upgrades do not yet — the client says so rather
than failing silently. `MACOS_PORT.md` has the detail on what is left.

### If macOS refuses to load the helper
A file downloaded by a browser is quarantined, and a quarantined library will not load into the
game. Clear it with:
```sh
xattr -dr com.apple.quarantine ~/Library/Application\ Support/Ap.Control
```

As on Windows, Steam's "verify integrity of game files" and game updates revert the patches — run
`apply all` again afterwards.

## Client
To run the client, you can open the executable before launching the game. You will have access to a page to log into the Archipelago in the Main Menu as well as in the Pause Menu. If you do not have a save yet or your current save is from the Archipelago, you can login from the Main Menu just fine, or wait until you are in game. If you already have a save loaded that is not from the Archipelago, I would start a new game and then log in from the pause menu.

## Note on location unlocking

I do know that locations do not unlock automatically. That is because the location reading is based on the save data of the game which is not always updated in real time. However, it should always update eventually.

## I found a bug, what should I do?
Bugs fall into two categories.
- If it is a bug related to world generation or a logic problem in the order in which items can be obtained, then the issue belongs to the apworld project. In that case, you can open an issue on that page instead of this one.
- If it is a bug related to anything else (items not being granted properly, locations not being detected, etc.), it most likely belongs to the client. In that case, you can open the issue here and I'll look at it eventually.
- Either way, if you prefer, there is a Control thread on the AP After Dark Discord server. You can post your problem there; I check it regularly.

In any case, if you have questions or want to contribute to the project, don't hesitate to contact me!