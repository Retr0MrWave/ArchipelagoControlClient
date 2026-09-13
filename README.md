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
Intel build). Extract it and keep the files together.

**1. Patch the game.** Same commands as Windows, without the `.exe`:
```sh
./Ap.Control.Patcher apply all
```
The patcher finds `Control/Game.app` in your Steam library on its own. If it does not, pass
`--game` pointing at the `Control` folder, at `Game.app`, or at `Game.app/Contents/Resources` —
any of the three works.

**2. Set up the launcher.**
```sh
./Ap.Control install-launcher
```
This writes a small helper library into `~/Library/Application Support/Ap.Control/` and prints the
line to paste into Control's Steam **Launch Options** (right-click Control → Properties → General).
It copies the line to your clipboard too, because it contains your home directory and a space, and
a mistyped one fails by the game simply starting unmodified.

The wrapper exists because Steam on macOS starts games through LaunchServices, which drops the
environment — so the usual `VAR=value %command%` trick does nothing here. It loads the helper into
the game; without it, the game runs exactly as it always did.

**3. Run the client**, then launch the game from Steam as usual.

### What works on macOS

Everything the Windows client does: security clearance, sector and door unlocks, elevator gating,
inventory items, ability upgrades, the progressive weapon and mod slots, the in-game Archipelago
page, and location tracking.

### Updating the client

Run `install-launcher` again after installing a new release — the helper library ships with the
client and the two are a matched pair. **Quit Control first.** A running game keeps using the
library it loaded when it started, so an install performed underneath it has no effect and no
error; it simply carries on with the old one until you restart it.

### If something is not working

`./Ap.Control probe-game` asks the running game what the client can see of it — whether the helper
loaded, which build, whether the game is running frames, and whether it can find your inventory —
printing each step separately, so the one that broke is visible. The helper's own log is at
`~/Library/Logs/Ap.Control/shim.log`.

If the game starts but nothing is connected, the launch option is the usual culprit: re-run
`install-launcher` and paste the line again.

A file downloaded by a browser is quarantined, and a quarantined library will not load into the
game. `install-launcher` handles this for the files it writes; if you copied them by hand instead:
```sh
xattr -dr com.apple.quarantine ~/Library/Application\ Support/Ap.Control
```

As on Windows, Steam's "verify integrity of game files" and game updates revert the patches — run
`apply all` again afterwards. A game update can also move the addresses the item and ability grants
use; the client checks the game's build id and says which features are unavailable rather than
failing silently.

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