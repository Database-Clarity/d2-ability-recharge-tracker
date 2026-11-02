# Changelog

Add a concise, single-line bullet for each notable user-facing change in the same commit as the code change. Keep entries factual and user-relevant; omit internal implementation details. The release workflow publishes only bullets added since the previous release tag. Remove an entry if its change is reverted before release.

- Releases include a self-contained portable Windows ZIP alongside the installer.
- Update checks stay visible beside the page navigation, including startup-check results and retry indicators for network errors.
- The main header is more compact.
- Release notes retain commit history beneath the concise user-facing changelog.
- The in-app changelog focuses on the current release or changes since the previous installed version, with commit history collapsed by default.
- Capture status appears once, in a pill beside the page navigation and a Tracking banner; a closed game shows an amber "Waiting for Destiny 2" notice instead of red errors on every row, and the overlay shows "Waiting for game" instead of "Watching".
- Tracking is decluttered: a segmented **Track: All · Grenade · Melee · Class** selector replaces the per-row "Track only" buttons, area setup moves into an **Areas ▾** menu, and **⋯** holds Move overlay, Copy all results and Troubleshooting.
- A first-run card with **Auto-detect areas…** replaces the empty results table until ability areas are set.
- Copy buttons beside Tracking results appear on hover or keyboard focus, and results get the space of the removed area-size column (area size is in the ability tooltip and Areas menu).
- **Record HUD** and **Open recordings folder** move to Settings › Troubleshooting; while recording, Tracking shows a red recording chip with **Stop**.
- Buttons use clear primary, secondary and link styles; menus use the dark theme; settings section headings are larger.
- The header row is gone: **Pause detection** and a compact **Overlay** switch sit at the bottom of Tracking, and **History** is now a tab.
- Update checks and the changelog move to Settings › About; the main screen only shows an **Update to v…** pill when an update is available, and failed checks no longer appear there.
- Repeat test has a single **Start test** / **Stop test** button that explains inline why it can't start, clearer **Cycles**, **Delay after recharge (s)** and **Hold input for (s)** labels, wrapping status, and a note when the test switches solo tracking (restored afterwards).
- Settings gains a **Shortcuts** section for pause / resume, show / hide overlay and start / stop test, and Capture shows the live measured rate and backend.
- The Display picker leaves Settings; when Destiny 2 isn't found, area selection asks which display to preview in a small dialog.
- The overlay header is slimmer and shows repeat-test progress (for example "Test · Cycle 2/5 · measuring"), recharging rows get a thin progress bar, and ability labels align without padding.
- History uses clearer session verbs (**Make active**, **Add to session**, **Session filter**), and **Add column…** asks for the column name in a small dialog.
- The in-app changelog uses compact, consistent spacing between releases, headings and bullets.
- The selection guide shows one example with practical tips, and explains that a thin margin above and below each area is added automatically.
