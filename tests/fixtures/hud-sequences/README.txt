HUD sequence fixtures

Regenerate from the repository root (no application/UI launch), once per source recording:
  dotnet run --project tests/D2AbilityTracker.Tests.csproj -- --extract-hud-fixtures <recording.zip> <annotations.json> [regions.json]
Slot regions come from the explicit regions file, else the recording's own regions.json (recordings made from
this version on save their slot regions and paddings), else the current AppData settings. 123051 and 124028
predate the current slot areas: pass tests/fixtures/hud-sequences/<recording-name>.regions.json. 153154 and
162021 were extracted with the AppData regions of 2026-11-07, saved alongside as <recording-name>.regions.json.
The exporter streams zip entries one frame at a time; it never extracts recordings.

Each gzip holds one JSON line per slot per frame: original UTC timestamps, all AbilitySignal fields (including
Twin and BottomBarOpacity), and frame numbers. Eight slot crops (PNG + expected signal) test the pixel detector,
including the start bar seen through a bright effect (153154 class, 3748) and over snow (162021 grenade, 7900).

CI (HudSequences.Run) replays every fixture through AbilitySlotTracker and requires:
- the exact number of starts and completions for every annotated cycle (no false or missed events);
- every start and completion within 0.1 s of capture time (frames.csv timestamps; frames are not evenly spaced),
  and within ±2 frames of the recording's index except the 123051 sword cycle's completion;
- the same from a startup at every 300th frame, clean and after 10 garbage frames, except the offsets listed in
  KnownStartupFailures (a listed offset that starts passing also fails, so the list stays accurate). A cycle whose
  start comes within 3 s of startup may go unmeasured; cycles picked up mid-recharge are estimates and are scored
  only by --sweep-hud.
Signals were recorded with the full replay's adaptive search guides, so a changed tracker cannot feed back into
pixel extraction here. After detector or tracker changes, also run the pixel-level checks below.

Pixel-level checks (decode every frame; never extract):
  dotnet run --project tests/D2AbilityTracker.Tests.csproj -- --replay-hud <recording.zip> <annotations.json> [regions.json]
  dotnet run --project tests/D2AbilityTracker.Tests.csproj -- --sweep-hud <recording.zip> <annotations.json> [regions.json]
--sweep-hud decodes each frame once and runs an independent tracker session from every HUD_SWEEP_STEP frames
(default 300), clean and after HUD_GARBAGE (default 10) garbage frames. It scores only annotated cycles that
start after each session's startup (errors in seconds) and exits 1 on any false or missed start or completion or
any endpoint over 0.1 s. It also compares each mid-recharge estimate's total with the annotated duration, by the
share of the cycle observed.
HUD_SWEEP_VERBOSE=1 also lists sessions with any endpoint more than 2 frames off. --replay-hud accepts
HUD_START, HUD_GARBAGE and HUD_TRACE=slot,from,to.
