# Restory Tweaks

Quality-of-life mod for **Restory**, built on BepInEx 5.

## Features

**Order parts from the notepad** — double-click a part on the repair table's notepad. If you already
have one in the parts box it's taken out and placed on the table; otherwise it's bought. Buying sets
your basket aside and restores it afterwards, so only the part you clicked is purchased.

**Delivered parts go straight to the parts box** — no carrying them over from the delivery box. Only
parts are moved; palettes, sticker packs and devices are left alone.

**Cleaner opens on pickup** — pick up a part that needs cleaning or soldering and the cleaning
window opens on it straight away, with no drag onto the cleaner. The right tool is equipped for you:
a brush while there's dirt or soot to clear, and the soldering iron the moment the board is clean
enough to resolder.

If you own an **ultrasonic bath**, parts that only need cleaning go straight into it instead — the
drawer opens and the part drops in. The cycle starts by itself once there's no reason to keep
loading — the basket is full, or nothing left on the device would go in it. Parts needing solder
still open the brush window, since the bath can't resolder. Auto-assemble leaves anything in the
basket alone and waits for it rather than fitting it half-cleaned.

**Auto-assemble** — once every part is identified, cleaned and undamaged, the device is put back
together: parts, screws and multi-slot battery rows, paced so you can watch it happen. Parts still
bolted in count as unexamined, so it waits until you've actually been through the whole device, and
it stops the moment you leave the repair pad rather than finishing up behind your back. **F7** turns
it off and on mid-game — it also abandons a run already under way, so you can take a device back
apart by hand. The setting is saved, and F6 still assembles on demand while it's off.

**Force repair (Ctrl+F8)** — rescue only. Fills every remaining socket on the device at the bench,
recreating parts that no longer exist if it has to, and sets everything to perfect. It's for a
device that can't be finished any other way; needs Ctrl held so it can't be hit by accident.

## Settings

`BepInEx/config/net.zeldo.restorytweaks.cfg`, created on first run.

| Section | Notable settings |
| --- | --- |
| `OrderParts` | `BuyImmediately`, `QuantityPerDoubleClick`, `OnlyMissingParts` |
| `Delivery` | `PartsStraightToPartsBox`, `ShowNotification` |
| `AutoAssemble` | `RequireEveryPartReady`, `DelayBetweenPartsMs` (750), `DelayBetweenScrewsMs` (200), `AssembleNowKey` (F6), `ToggleKey` (F7), `ForceRepairKey` (Ctrl+F8) |
| `AutoOpenCleaner` | `Enabled`, `SelectTool`, `PreferUltrasonicBath`, `AutoStartUltrasonic`, `OnlyForDeviceParts` |

## Installing

**Steam Deck / Linux** — one command, no clone:

```bash
curl -sSL https://raw.githubusercontent.com/ZeldoKavira/RestoryTweaks/main/install-steamdeck.sh | bash
```

It finds the game, installs BepInEx and the latest build, and prints the launch option you need.
Run it again any time to update. To remove the mod, `… | bash -s -- --uninstall` (arguments go after
`-s --` because the script arrives on stdin).

Restory is a Windows build running under Proton, so this installs the *Windows* BepInEx. Its loader
is a `winhttp.dll` shim that Proton only picks up when you set this in the game's launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

**Windows** — unzip BepInEx 5 (x64) into the game folder, then drop `RestoryTweaks.dll` from the
[latest release](https://github.com/ZeldoKavira/RestoryTweaks/releases/latest) into `BepInEx/plugins/`.

## Building

Every push to `main` builds on CI and republishes the rolling `latest` release, so the installer
above always fetches a current DLL.

To build locally you need the game installed, since it references the game's own assemblies:

```bash
dotnet build RestoryTweaks/RestoryTweaks.csproj -c Release
```

Override the game location with `-p:GameDir="..."`. The real game code lives in
`Restory_Data/Managed/Restory.Assembly.dll` — `Assembly-CSharp.dll` is nearly empty.

CI can't install the game, so it compiles against stripped reference assemblies in
[`refs/Managed`](refs/Managed) — metadata only, no method bodies, none of the game's code.
Regenerate them after a game update, or when the mod starts referencing a new assembly:

```
.\refs\update-refs.ps1
```

## Notes

The mod leans on the game's own systems rather than reimplementing them: purchases go through
`ElementsShopInteractor`, parts are placed with `ElementService.DropItemsFromStorage`, and assembly
uses each socket's own availability rules. Where a private member is reached by reflection it's
because the public path also runs input-driven state machines that expect a player behind them.
