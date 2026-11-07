HUD sequence fixtures

Regenerate from the repository root (no application/UI launch):
  dotnet run --project tests/D2AbilityTracker.Tests.csproj -- --extract-hud-fixtures <recording.zip> <annotations.json> tests/fixtures/hud-sequences/<recording-name>.regions.json
Run once for each source recording. The optional settings argument uses only Slots/Kind/Region;
without it the harness uses the current AppData settings, so pass the saved region snapshot.
The exporter streams zip entries, decoding one frame at a time in memory. It never extracts
recordings. Six selected slot crops are saved as PNG; all other pixel buffers are discarded.

Each gzip contains one JSON line per slot per frame, preserving original UTC timestamps,
all AbilitySignal fields (including Twin), and frame numbers. Colors use lossless ARGB integers.
Full compact histories preserve border learning, model fitting, previous cycles and recast state
without introducing artificial tracker snapshots. Adjacent annotations are unedited source copies.

CI replays the signals through AbilitySlotTracker, checks exact start/completion counts for
both recordings, and checks selected annotated endpoints with a strict +/-2-frame tolerance:
normal starts, pulse-then-bar starts, gray-bar completions, the quick recast, and the completion
animation that must not create a recast. It also tests raw pixel features on all six crops,
including a sword-cycle crop at frame 3500. Known inaccurate endpoints are deliberately not
asserted within +/-2: 123051 melee3 start and sword completion; 124028 class1/class2 starts.
Existing test assertions are unchanged. There are no annotation adjustments.

Limit: signal histories were collected with the live detector's adaptive search guides. They
exercise tracker changes but cannot model how a changed tracker feeds back into pixel extraction.
Use full --replay-hud validation after detector/model changes. PNG tests cover border thickness,
fill luminance/saturation, charged and flash flags, not adaptive progress or guide selection.

Manual diagnostic: --replay-hud-signals prints starts/ends and pulse corrections from fixtures.
