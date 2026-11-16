# Validation — 16 November 2026

Built on Windows x64 using .NET SDK 10.0.404. Release build completed without warnings or errors; the published output is one self-contained executable.

27 automated checks passed, including live GitHub release selection, downloading the official ZIP, checksum verification/rejection, installation to a mock LIVE folder, settings preservation, encoding handling, version/hash tracking, unsafe ZIP rejection, and rollback after an injected disk failure. Permission tests additionally covered successful probing without changes to existing contents, missing-directory cleanup, invalid destinations, locked/read-only files, a real temporary ACL denial, scoped grants, preservation of unrelated executable ACLs, and malformed helper arguments. All ACL mutations in tests were limited to fixtures.

Desktop checks confirmed:

- Configuration renders without clipping at the tested desktop DPI.
- Auto-detection finds `D:\Program Files\Roberts Space Industries\StarCitizen\LIVE`.
- The manual update button works against the isolated fixture and reports the installed/available release.
- Closing configuration hides its window and leaves the process running.
- Launching another instance reopens the original configuration window.
- A `--startup` launch downloaded and installed to the isolated fixture with no visible window, then remained running in the background.

The actual game installation was not modified. Test settings were removed, startup remains disabled, and the app was reopened with the detected real path ready for the user to configure.

Not exercised: accepting/cancelling a real UAC prompt or elevation with alternate administrator credentials, a real Windows sign-in/reboot, tray hover/click/context-menu interactions, non-default DPI settings, or in-game validation. The elevated helper dispatch and cancellation paths are implemented, but UAC interaction has not been tested end to end. The real game folder already allows updates and did not need permission changes.
