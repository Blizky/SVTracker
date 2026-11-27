Attempting to perform the InitializeDefaultDrives operation on the 'FileSystem' provider failed.
# SVTracker

A command-based SMAPI mod for tracking items you want to find. It saves each save file's wishlist and notes, then shows in-game HUD reminders when tracked items, quest items, Community Center bundle items, or timely birthday gifts enter your inventory.

## Requirements

- Stardew Valley 1.6
- SMAPI 4.0 or later

## Install

1. Install and launch Stardew Valley through SMAPI once.
2. Copy the `SVTracker` mod folder into the game's `Mods` folder. It should contain `manifest.json` and `SVTracker.dll` directly inside it.
3. Start the game with SMAPI and load a save.

## Commands

Enter these in the in-game chat while a save is loaded:

| Command | What it does |
| --- | --- |
| `/track [item], [note]` | Add an item to the wishlist. A note after the comma is optional. Running it again with a note adds or replaces that note. |
| `/check [item name]` | Show how many of that item you have in your inventory. |
| `/check [NPC name]` | List that villager's loved items and how many you have in your inventory and scanned chests. |
| `/check quests` or `/check q` | List active item-delivery quest items and how many you own in your inventory and scanned chests. |
| `/untrack [item]` | Stop tracking the item and delete its note. |
| `/tracking` | List currently tracked items and their notes. |
| `/track reset` | Clear all saved tracked items and notes for the current save. |
| `/track help` | Show the command help in chat. |

Use the item's displayed name or qualified item ID. If a name matches multiple items, the mod reports the matching IDs. To include a note, put a comma after the item name, for example `/track Acorn, Needed for a tree`. Notes are limited to 64 characters; longer notes are silently truncated.

## Reminders

- When a tracked item is acquired, it is automatically removed from the active tracking list and shows a ten-second HUD reminder with its note. The note remains saved; use `/track [item]` to track it again or `/track [item], [note]` to update its note.
- A newly acquired item needed for an active item-delivery quest shows a HUD reminder with the quest information.
- A newly acquired item still required for an incomplete Community Center bundle shows a reminder with the room and bundle name. Already-filled slots and completed bundles are ignored.
- Repeated finds of the same item within one second produce only one HUD notification.
- Each in-game morning, the mod checks birthdays in the next five days for villagers whose friendship isn't at its heart cap. If you acquire an item one of them loves, it shows a four-second gift reminder. Giving a villager a gift only suppresses further reminders for that villager when their birthday is today; it doesn't suppress reminders for upcoming birthdays. The nearest birthday appears first, with other villagers included when space allows. Birthday watches refresh daily and aren't saved as wishlist entries.
- On a villager's birthday morning, the mod checks the player's inventory and chests in loaded locations and farm buildings. If it finds one or more gifts that villager loves, it shows a ten-second reminder saying the birthday is today and listing them.
- Notices appear in the upper-left HUD area. Item, villager, and quest names are highlighted in amber; other reminder text is green. Errors use amber text.

Wishlist entries and notes are stored separately for each save file. Birthday reminders are temporary and rebuilt each morning.


The “Now tracking” confirmation stays visible for five seconds and fades during its final second.

## Testing notes

Acquisition detection uses the player's inventory change event with a periodic inventory check as a fallback. It can only notify for an item when it enters the player's inventory, so sources that bypass inventory may not trigger a reminder. Multiplayer behavior has not been verified.

## Build from source

The project targets .NET 6 and references the Stardew Valley and SMAPI assemblies from a local game installation. From the project directory, run:

```powershell
dotnet build -p:InstallMod=false
```

This writes the compiled mod to `bin/Debug/net6.0`. To copy the build into the default Steam installation's `Mods` folder, run `dotnet build` without `-p:InstallMod=false`. Set `-p:GamePath="path to Stardew Valley"` if the game is installed elsewhere.

## License

SVTracker is distributed under the MIT License. See [LICENSE](LICENSE).