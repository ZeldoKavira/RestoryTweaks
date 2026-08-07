# Restory Tweaks

Quality-of-life mod for **Restory**, built on BepInEx 5.

## Features

**Order parts from the notepad** — double-click a part on the repair table's notepad. If you already
have one in the parts box it's taken out and placed on the table; otherwise it's bought. Buying sets
your basket aside and restores it afterwards, so only the part you clicked is purchased.

**Delivered parts go straight to the parts box** — no carrying them over from the delivery box. Only
parts are moved; palettes, sticker packs and devices are left alone.

**Auto-assemble** — once every loose part is identified, cleaned and undamaged, the device is put
back together: parts, screws and multi-slot battery rows, paced so you can watch it happen.

## Settings

`BepInEx/config/net.zeldo.restorytweaks.cfg`, created on first run.

| Section | Notable settings |
| --- | --- |
| `OrderParts` | `BuyImmediately`, `QuantityPerDoubleClick`, `OnlyMissingParts` |
| `Delivery` | `PartsStraightToPartsBox`, `ShowNotification` |
| `AutoAssemble` | `RequireEveryPartReady`, `DelayBetweenPartsMs` (750), `DelayBetweenScrewsMs` (200), `AssembleNowKey` (F6) |

## Installing

**Windows** — unzip BepInEx 5 (x64) into the game folder, then drop `RestoryTweaks.dll` into
`BepInEx/plugins/`.

**Steam Deck / Linux** — run [`install-steamdeck.sh`](install-steamdeck.sh). It finds the game,
installs BepInEx and the latest release of the mod, and prints the launch option you need. The game
runs under Proton, so BepInEx needs `WINEDLLOVERRIDES="winhttp=n,b" %command%` set in the game's
launch options — the script tells you this and why.

## Building

Requires the game installed, since it references the game's own assemblies:

```
dotnet build RestoryTweaks/RestoryTweaks.csproj -c Release
```

Override the game location with `-p:GameDir="..."`. The real game code lives in
`Restory_Data/Managed/Restory.Assembly.dll` — `Assembly-CSharp.dll` is nearly empty.

## Notes

The mod leans on the game's own systems rather than reimplementing them: purchases go through
`ElementsShopInteractor`, parts are placed with `ElementService.DropItemsFromStorage`, and assembly
uses each socket's own availability rules. Where a private member is reached by reflection it's
because the public path also runs input-driven state machines that expect a player behind them.
