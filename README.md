# Destiny 2 Ability Recharge Tracker

A Windows overlay for timing grenade, melee, and class-ability recharge from the visible Destiny 2 HUD. It samples screen pixels only and does not read game memory. Global low-level keyboard and mouse hooks support configurable input bindings; they pass all input through to other applications.

## Run

1. Install the .NET 9 SDK on Windows.
2. From this folder, run `dotnet run`.
3. Choose the display containing Destiny 2, then tightly select the square ability slot, including its border and the area where the cooldown line travels. Repeat for grenade, melee, and class ability as desired.
4. Leave detection on. The timer starts when the cooldown line appears. When that line disappears, the tracker waits for the slot's ready fill to return to confirm the brief blank completion flash, but records the disappearance time as the end. The measured duration remains visible after completion.

The selector shows a frozen preview of only the chosen display, fitted to that display's orientation and size. Dragging on the preview avoids the game taking control of the cursor and works on portrait and ultrawide monitors. Regions are saved relative to their display and scaled when its resolution changes (for example, setup at 4K and play at 1080p).

No sample calibration is required. The detector follows the moving horizontal cooldown boundary using narrow background strips along both sides of the slot, avoiding the ability artwork as the line crosses it. It starts timing on the line's first appearance and marks its first disappearance as a possible completion; the ready-state background fill confirms that the brief blank flash was the true endpoint, while transient line dropouts are ignored. Charged fill is detected from saturation in the slot corners, without matching ability icons or depending on any particular element color. Ready backgrounds can therefore be Solar orange, Arc blue, Void purple, or another element's color. Each configured slot has an independent state and duration.

Capture runs independently from the control-window UI, targets up to 240 samples per second, combines selected slots into one capture per display, reuses native screen-capture buffers, and scans their pixel data directly. The control window reports the measured capture rate. This is a best-effort desktop capture rate, not a guarantee of observing every game-rendered frame: Windows can only provide frames actually presented to the desktop, and system load or capture permissions can reduce the rate.

Click the pause-binding button and press any key or mouse button to configure the global pause/resume shortcut; Ctrl, Alt, Shift, and Windows modifiers are recorded with it. Keyboard keys, mouse buttons, and wheel directions are supported. Pressing the shortcut never blocks that input from reaching Destiny 2. Settings are stored locally under `%AppData%\D2AbilityTracker`.
