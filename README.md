# Destiny 2 Ability Recharge Tracker

A Windows overlay for timing grenade, melee, and class-ability recharge from the visible Destiny 2 HUD. It samples screen pixels only; it does not read game memory, inject into the game, or send game input.

## Run

1. Install the .NET 9 SDK on Windows.
2. From this folder, run `dotnet run`.
3. Choose the display containing Destiny 2, then tightly select the square ability slot, including its border and the area where the cooldown line travels. Repeat for grenade, melee, and class ability as desired.
4. Leave detection on. The timer starts when the cooldown line appears. When that line disappears, the tracker waits for the slot's ready fill to return to confirm the brief blank completion flash, but records the disappearance time as the end. The measured duration remains visible after completion.

The selector shows a frozen preview of only the chosen display, fitted to that display's orientation and size. Dragging on the preview avoids the game taking control of the cursor and works on portrait and ultrawide monitors. Regions are saved relative to their display and scaled when its resolution changes (for example, setup at 4K and play at 1080p).

No sample calibration is required. The detector follows the moving horizontal cooldown boundary using narrow background strips along both sides of the slot, avoiding the ability artwork as the line crosses it. It starts timing on the line's first appearance and marks its first disappearance as a possible completion; the ready-state background fill confirms that the brief blank flash was the true endpoint, while transient line dropouts are ignored. Charged fill is detected from saturation in the slot corners, without matching ability icons or depending on any particular element color. Ready backgrounds can therefore be Solar orange, Arc blue, Void purple, or another element's color. Each configured slot has an independent state and duration.

Use the configurable global function key (F8 by default) or the control window button to pause before opening menus. Settings are stored locally under `%AppData%\D2AbilityTracker`.
