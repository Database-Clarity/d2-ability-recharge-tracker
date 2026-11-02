# Changelog

Add a concise, single-line bullet for each notable user-facing change in the same commit as the code change. Keep entries factual and user-relevant; omit internal implementation details. The release workflow publishes only bullets added since the previous release tag. Remove an entry if its change is reverted before release.

- Releases include a self-contained portable Windows ZIP alongside the installer.
- Update checks stay visible beside the page navigation, including startup-check results and retry indicators for network errors.
- The main header is more compact.
- Release notes retain commit history beneath the concise user-facing changelog.
- The in-app changelog focuses on the current release or changes since the previous installed version, with commit history collapsed by default.
- Capture status appears once, in a pill beside the page navigation and a Tracking banner; a closed game shows an amber "Waiting for Destiny 2" notice instead of red errors on every row, and the overlay shows "Waiting for game" instead of "Watching".
