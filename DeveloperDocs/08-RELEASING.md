# Releasing

1. Version in `MenaceCoopMelon/MelonEntry.cs` (`PluginVersion`).
2. CHANGELOG entry; README updates (player readme for anything a player sees).
3. The pre-release runs in 06-TESTING.md §6.
4. `.\Build-MelonRelease.ps1` with no game running. It builds with dev tools off,
   proves the `[DEVTOOLS]` marker is absent, zips `MenaceCoop.dll` + `README.txt` at
   the zip root (never under `Mods\` -- players extract into `Mods\` and a nested DLL
   is never loaded), and refuses nested archives, scripts or loader files (Nexus rules).
5. Everyone in a session must run the same mod version **and** game version; the
   handshake refuses mismatches.

After a **game update**: rebuild first -- a clean compile against the regenerated
assemblies is the real test. Then check the startup `[PATCHES]` and `[API]` lines.
See [11-UPDATE-RESILIENCE.md](11-UPDATE-RESILIENCE.md).
