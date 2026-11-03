# A Place for Every Potion – Co-op Mod

Play **A Place for Every Potion** together: two PCs on the same home network, one shared lab.

> **Unofficial mod.** It is not made or supported by the game's developer, so please report problems here on GitHub, not to them. Official multiplayer is coming to the game in a future update.

![The other player, carrying bottles in the shared lab](docs/lab.jpg)

- **One lab, two players.** The host's save is the world. Bottles, shelves, vats, the Essence Separator and the cat are shared, and every pick-up, pour and throw shows up on the other PC.
- **Your own level.** XP and skills stay yours. The first time you join someone you start fresh or at their level, and your solo save is never touched.
- **No liquid gets lost.** The worlds keep checking each other and repair any difference.

![The Co-op window](docs/menu.jpg)

## Install

**1. Find the game folder.** On Steam: right-click the game → **Manage** → **Browse local files**. It is the folder with `A Place for Every Potion.exe` in it.

**2. Install MelonLoader** (the mod loader):
- Download `MelonLoader.Installer.exe` from the [MelonLoader releases](https://github.com/LavaGang/MelonLoader/releases).
- Run it, select `A Place for Every Potion.exe`, choose version **0.7.3** (or newer 0.7.x) and install.
- Start the game once. The first start takes a minute or two while MelonLoader sets itself up. Close the game when the main menu appears.

**3. Install the mod:**
- Download `PotionCoop-x.y.z.zip` from [Releases](https://github.com/csermet/A-Place-for-Every-Potion-Coop-Mod/releases).
- Unzip it into the game folder, so that you get this:

```
A Place for Every Potion\
├── A Place for Every Potion.exe
├── MelonLoader\
└── Mods\
    └── PotionCoop.dll
```

Or with PowerShell, from the folder where you downloaded the zip:

```powershell
Expand-Archive .\PotionCoop-0.4.7.zip -DestinationPath "C:\path\to\A Place for Every Potion" -Force
```

**4. Start the game.** The main menu now has **Co-op**. When Windows asks about network access, allow it for **private networks**.

Your friend installs it the same way. You both need the same game version (tested with 1.2.6) and the same mod version.

## Play

- **Host:** main menu → **Co-op** → **HOST**. You go straight into your lab.
- **Join:** main menu → **Co-op** → **JOIN** next to your friend's game. If it isn't listed, type the host's IP (shown in their Co-op window) under **Join by IP**.

**Your friend's game doesn't show up?**
- Both PCs must be on the same router (not a guest Wi-Fi or a VPN).
- Windows needs to treat the network as *Private*: **Settings** → **Network & internet** → your connection → **Private network**.
- Still nothing: copy `PotionCoop_firewall.bat` from the zip into the game folder, right-click it → **Run as administrator**.

Logs are in `MelonLoader\Latest.log`.

---

Undo is off while connected. Build from source with `dotnet build -c Release` (the game in `..\game`, MelonLoader installed). [MIT license](LICENSE).
