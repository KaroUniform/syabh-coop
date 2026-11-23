# SYABH Co-op Campaign

A fan-made mod for **Sir, You Are Being Hunted** that lets two friends play the
**story campaign together**: five islands, boats, 25 fragments, the stone circle
and the final scene, all in co-op.

[Русская версия](README.ru.md)

## Requirements

- **macOS** with [CrossOver](https://www.codeweavers.com/crossover)
- The **Windows Steam version** of the game installed in CrossOver, multiplayer
  build **1.1.2** (default Steam branch; it's what the launcher's *Multiplayer*
  button starts)
- Both players on **the same local network**

The mod is tested on two Macs on a home Wi-Fi network. Internet play, Windows
PCs and the native Mac version of the game have not been tested.

## Install (both players)

1. Download `syabh-coop-<version>.zip` from [Releases](../../releases) and
   unzip it. Both players need the **same version**.
2. Quit the game.
3. Double-click `install.command`. If macOS won't open it, right-click it →
   Open → Open.

The installer finds the game in CrossOver, backs up the original file and
builds the mod from your own copy of the game. It takes a few seconds. If it
can't find the game, it asks you to drag in the folder
`…/SirYouAreBeingHunted/x64/multiplayer/sir_Data/Managed`.

## Start the server (one player)

Double-click `server.command`. A server window opens, and the Terminal shows
**the address to connect to** (like `192.168.1.23`). Send it to your friend.

The server runs while its window is open. Close the window to stop it. The
campaign is saved, and the next `server.command` continues from the same island.

## Join (both players, the host too)

1. Start the game's multiplayer.
2. **Direct connect** → enter the address → **Connect**.
3. In the lobby, pick your class with `<` and `>`, then press **Ready**. The
   island starts when both players are ready.

If macOS asks whether to allow incoming connections, allow them. A guest Wi-Fi
network usually blocks devices from seeing each other.

## How to play

- **Sailing:** at the boat, press **B** to propose or agree, **N** to decline.
  You can't sail while anyone is bleeding, starving or dead.
- **Fragments and items** travel with you between islands.
- **Death:** hold **G** at your partner's body for 3 s to revive them (within
  60 s). Otherwise they respawn at the boat, and their items stay in the body.
  If both die, the island restarts from your arrival.
- **Chat:** `#coop` shows the campaign status. The host can type
  `#coop newcampaign` to start over.

## Uninstall

Double-click `restore.command`, or use Steam → game Properties → Installed
Files → Verify integrity of game files. A game update in Steam also removes the
mod; install it again afterwards.

## If something goes wrong

- **The server rejects you with a client version error:** one of you has no
  mod or a different version. Install the same zip on both Macs.
- **The installer refuses:** your game is not multiplayer build 1.1.2, or the
  file was already changed. Verify the game files in Steam and run it again.
- **Logs:** lines from the mod start with `[SYABH-COOP]`. The client log is
  `x64/multiplayer/sir_Data/output_log.txt`, and the server log is
  `x64/multiplayer/server-log-44466.txt`.

## For developers

The mod is a [MonoMod](https://github.com/MonoMod/MonoMod) patch of the game's
`Assembly-CSharp.dll`. Sources are in `src/SyabhCoop/`. To build it you need
Mono (`brew install mono`) and the game. Then run `tools/get_monomod.sh`,
`tools/build_mod.sh` and `tools/make_dist.sh`. The script headers explain each
step. Code comments are in Russian.

## Legal

This is an unofficial mod, not affiliated with or endorsed by Big Robot.
"Sir, You Are Being Hunted" is © Big Robot Ltd, and you need your own copy of the
game. Neither this repository nor the release contains any game files. The mod
is [MIT-licensed](LICENSE). Third-party tools are listed in
[THIRD-PARTY-NOTICES.txt](tools/dist/THIRD-PARTY-NOTICES.txt).
