# PotionCoop

Two-player LAN co-op for **A Place for Every Potion**: two PCs on the same home network, one shared lab.

An unofficial fan mod for [MelonLoader](https://github.com/LavaGang/MelonLoader). Not affiliated with or endorsed by the game's developers.

## What it does

- **One shared lab.** The host's save is the world: shelves, bottles, vats, the Essence Separator and the cat are shared. Picking up, shelving, throwing and pouring show up on the other PC as they happen.
- **Your own progress.** XP, level and skills stay personal. The first time you join a host you choose to start fresh or at the host's level. That profile is stored on your PC, one per host. Your own solo save is never touched.
- **Looks like the game.** The Co-op menu is built from the game's own windows: main menu, pause menu, host list, join by IP.
- **The other player** is a hooded alchemist in their own color, with glowing eyes and a name label, holding the bottles they carry and reaching, pouring and throwing as they do.
- **Self-healing sync.** Both sides compare world checksums all the time and repair differences. The host also checks that the lab's liquid adds up to what its bottles started with (liquid is never made or destroyed in this game). If a sync problem ever lost or doubled some, it puts it right.

## Requirements

- The game (same version on both PCs; the mod is built against 1.2.6).
- [MelonLoader 0.7.x](https://github.com/LavaGang/MelonLoader/releases) (tested with 0.7.3).
- Both PCs on the same local network (LAN or Wi-Fi). Internet play is not supported.

## Install

1. Install MelonLoader into the game folder with its installer, then start the game once and wait for MelonLoader to finish its first-run setup.
2. Copy `PotionCoop.dll` from the release into the game's `Mods` folder.
3. Do the same on the second PC.
4. When Windows asks whether to allow network access, allow it for **private networks**. If you missed the prompt, run `PotionCoop_firewall.bat` as administrator. It only allows connections from the local network.

## Play

- **Host:** main menu → **Co-op** → **HOST**. You enter your lab right away. Your IP is shown in the Co-op window.
- **Join:** main menu → **Co-op** → your friend's game appears under *Games on your network* → **JOIN**. If it doesn't appear, type the host's IP under *Join by IP*.
- In the lab the Co-op window is also in the pause menu (status, disconnect).
- When the host leaves or the connection drops, the guest returns to the main menu.

Spare hotkeys (hold Ctrl): F7 status to the log · F8 host on/off · F9 join · F10 resync · F11 leave · F12 hide the status bar.

## Known limitations

- Two players only.
- Undo is off while connected (it could take a bottle out of the other player's hands).
- Only the host's Essence Separator runs. The guest's shows the host's machine, and pours the guest starts on it are done by the host's. On the guest's screen the loop moves in steps instead of flowing.
- Vat capacity and separator upgrades follow the host's skills.

## Troubleshooting

- **The host list stays empty:** check that both PCs are on the same network (not a guest network or VPN), that the Windows network profile is *Private*, or join by IP.
- **"different versions":** both PCs need the same game version and the same mod version.
- **Logs:** `MelonLoader/Latest.log`. Please attach it to bug reports.

## Building from source

The project references the game's MelonLoader-generated assemblies. With the game in `..\game` relative to this folder, and MelonLoader installed and run once:

```
dotnet build -c Release
```

This copies the DLL into the game's `Mods` folder. While the game is running the DLL is locked. Use `dotnet build -c Release -p:Staging=true` instead to put it in `dist-next\`.

## License

MIT. See [LICENSE](LICENSE).
