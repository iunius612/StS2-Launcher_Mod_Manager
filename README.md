# StS2 Launcher Mod (Mod Manager Fork)

An Android launcher for Slay the Spire 2, built on a custom Godot 4.5.1 engine with .NET/Mono and Harmony runtime patching.

> **Fork notice**: This is a community fork of [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher). The upstream launcher's mod loader stopped working with recent game builds; this fork fixes that, adds a Save Manager + cloud-conflict resolution, and adds a few mobile-UX tweaks. See the **Fork changes** sections below (latest first).

> **Disclaimer**: This is an unofficial community project. Slay the Spire 2 is developed and published by Mega Crit Games. A valid Steam account that owns Slay the Spire 2 is required. Game files are downloaded directly from Steam after authentication. No game assets are included in this repository.

> **사용설명서 (한국어)**: 처음 설치하는 사용자를 위한 단계별 가이드는 [docs/USER_GUIDE.md](docs/USER_GUIDE.md) 참조.

## Fork changes (v0.3.3)

Versioned as **0.3.3 (versionCode 241)**. Drop-in upgrade from 0.3.x — saves and credentials carry over. Targets issue #8 (BaseLib v3.x crashes the game on PatchAll) and issue #9 (Android `release_info` not loaded → main-menu `????` build label + LAN multiplayer version mismatch).

### What's fixed

1. **BaseLib v3.x mobile compat shim (issue #8).** BaseLib (Alchyr) v3.x and other mods that exercise its async hook system used to hang the game on a black screen during `PatchAll`. Symptom in logcat: a Godot native `BUG: Unreferenced static string to 0: _draw_rect` at `string_name.cpp:116`, followed by the renderer thread freezing while C# threads stay alive until the SteamKit2 connection idle-times out 30 s later.

   Diagnosis (full notes in issue #8 comments):
   - The crash fires immediately after BaseLib's `BaseLib.Utils.Patching.AsyncMethodCall.Create` injects new yield states into a compiler-emitted async state-machine `MoveNext` (e.g. `Hook+<AfterCardPlayed>d__15`).
   - Initial hypothesis was the bundled `0Harmony.dll`'s `MonoILFixup` (an Ekyso-added IL post-pass) having inverted priority for raw-int local-index resolution. Ruled out by testing three override variants (LocalBuilder reuse / LocalVariableInfo reuse / leave raw operand untouched after `CecilILGenerator` local-count reflection check). All three produced the same crash at the same instruction.
   - Real cause is downstream of `MonoILFixup`: BaseLib's emit sequence (`Box` / `BoxArg0` / generic `AwaitUnsafeOnCompleted` / hoisted-field `stfld`) triggers a memory corruption when committed via `MonoMod.Utils.Cil.CecilILGenerator` + runtime detour on Mono Android. The corruption happens to land on Godot's static `StringName` cache slot for `_draw_rect` (the `CanvasItem` virtual method name), driving its refcount below zero.

   Workaround in this release:
   - New `BaseLibCompatPatches` registers an `AppDomain.AssemblyLoad` listener at launcher init. When `BaseLib.dll` loads (post-PLAY, via the game's mod loader), the listener Harmony-prefixes `BaseLib.Utils.Patching.AsyncMethodCall.Create` to return the original IL unchanged and skip the original method. The state-machine surgery never runs; everything else BaseLib does runs normally.

   **Trade-off (degraded mode — must be communicated to users):**

   | Capability | Status with shim |
   |------------|------------------|
   | BaseLib DLL/PCK load + 153 Harmony patches install | ✅ |
   | Node factories (`Control`, `NCreatureVisuals`, `NRestSiteCharacter`, etc.) | ✅ |
   | Content / encounter / event patches (`DeprecatedAct`, `Glory`, `Hive`, `Overgrowth`, `Underdocks` ...) | ✅ |
   | Config UI / `SimpleModConfig` | ✅ |
   | `KeyGenerator` (enum extension, e.g. `CardKeyword`) | ✅ |
   | `CustomPile` patch | ✅ |
   | **Async hook system** (`AfterCardPlayed`, `BeforePlay`, etc.) | ❌ no-op (callbacks never fire) |
   | Mods adding cards / characters / content via BaseLib's non-hook APIs | ✅ usually works |
   | Mods that depend on BaseLib hooks for runtime triggers (e.g. "on card play do X") | partial — mod loads but trigger never fires |

   This is an explicit workaround, not a real fix. Real fix paths require either (a) identifying the underlying MonoMod / Cecil / Mono Android emit bug and patching it upstream, (b) BaseLib shipping a mobile-aware variant that doesn't rely on the failing emit pattern, or (c) replacing the bundled Ekyso `0Harmony.dll` build.

2. **`release_info.json` loaded on Android (issue #9).** The main-menu build label used to show `????` and the launcher reported `NON_RELEASE` to the LAN multiplayer handshake (causing version-mismatch errors against PC clients) because `ReleaseInfoManager.LoadConfig` looked at the executable directory which is empty on Android. New `ReleaseInfoPatches` postfix loads `release_info.json` from the game payload directory (`<files>/game/`) instead. Build label and LAN handshake now show the correct game version.

### Known incompatibilities (cannot be fixed launcher-side)

- **QuickReload (mmmmie), RitsuLib (OLC)**, and any other mod that imports `Steamworks.NET` directly — will crash on PLAY with a black screen on the mobile launcher. These mods call into `libsteam_api.so` (the Steamworks SDK native library) which Valve does not ship for Android. The launcher provides a no-op stub `libsteam_api.so` to satisfy the dynamic linker but actual API calls (`SteamUser.GetSteamID`, lobby APIs, Steam Cloud API, etc.) cannot work. The launcher's bundled SteamKit2 is a separate API surface (Steam network protocol for cloud / auth / depot manifests, not in-process Steam Client integration) and cannot substitute. Until each mod author ships a mobile-aware build that gates Steamworks calls, these mods are not usable on the launcher.

## Fork changes (v0.3.2)

Versioned as **0.3.2 (versionCode 233)**. Drop-in upgrade from 0.3.x — saves and credentials carry over. Targets issue #7 (Save Manager dialog blind to in-progress runs) which combined with several smaller defects produced a destructive cross-device loop where a quick swipe-to-quit could lose the most recently entered floor.

### What's fixed

1. **Save Manager dialog now surfaces the in-progress run.** The `현재 진행` row replaces the `캐릭터 N명` accumulator and reads as `1막 3층` (or `—` when no run is active). For users hopping between PC and mobile mid-run this is the only signal the dialog gives that one side has progress the other doesn't — pre-fix the dialog showed identical accumulator stats with only mtime/size differing, so users couldn't tell which `Keep` button was the safe choice.
2. **Conflict detection looks at `current_run.save`, not just `progress.save`.** `CloudSyncDecisions.DetermineAsync` now flags a conflict when current-run files differ even if accumulator files match. Before this, the most common cross-device case (one device has an in-progress run, the other has none/older) silently classified as `Identical` and the user was never prompted to sync.
3. **Card shows which profile it represents.** A small "프로필 N" subtitle under the card title — the picked summary may now be from profile2 or profile3 rather than always profile1, depending on which profile triggered the conflict. Also `(N개 프로필)` in the body when more than one profile differs.
4. **`최근` badge uses both progress + current_run mtime.** Pre-fix the badge compared `progress.save` mtime alone, which mispointed at cloud whenever the in-progress run was the newer signal but progress.save hadn't been touched (verified: a strict swipe scenario where local current_run was newer than cloud, but cloud progress.save had a fresher mtime from a prior KeepLocal — old code said cloud, true newer = local).
5. **Conflict resolution covers `current_run.save` too, and aligns mtimes.** `ApplyChosenSideAsync` previously processed only `progress.save` and skipped the `SetLastModifiedTime` step. After a `Keep*` press the in-progress run was left out of sync (next AutoSync's "cloud wins" fallback could then overwrite a local newer copy on identical floor counts) and mtimes drifted apart, re-triggering the same conflict on every relaunch. Now it pushes/pulls all of `progress.save / current_run.save / current_run_mp.save × profile 1/2/3 × {vanilla, modded}` and stamps local mtime to match cloud (KeepCloud) or NOW (KeepLocal) so the next decision sees consistent state.
6. **`SaveProgressComparer.CompareCurrentRun` size tiebreaker.** Floor counts often match on both sides even when the run files differ by hundreds of bytes (post-floor-entry actions update `current_run.save` without touching `map_point_history` length). The old `Equal → cloud wins` fallback then silently destroyed local progress on the next sync. New tiebreaker: same floor → larger file wins. Combined with #5 this auto-recovers the "swipe before queue drained" case without user input.
7. **PLAY locks while a cloud op is in flight.** `SetSyncBusy` now disables PLAY along with Push/Pull. Pre-fix you could tap Save Manager and then PLAY before the dialog resolved — the cloud handshake would then race the game's startup. (Android lifecycle still doesn't guarantee `Flush(5000)` finishes before swipe kills the process; the size-tiebreaker auto-recovery in #6 is the real safety net for that case.)

### Diagnostics scaffolding (gated, opt-in)

`Issue7Diagnostics` is included but a no-op unless the marker file `/storage/emulated/0/StS2LauncherMM/.diagnose_issue7` exists. When present it dumps per-profile audit state, AutoSync outcomes, IsCorrupt branch entries (with first-bytes hex), and CloudWriteQueue depth — useful if a future regression in this area needs to be reproduced and mailed in. Marker is created with `adb shell touch /storage/emulated/0/StS2LauncherMM/.diagnose_issue7`.

## Fork changes (v0.3.1)

Versioned as **0.3.1 (versionCode 228)**. Drop-in upgrade from 0.3.0.

1. **Save Manager dialog fits short viewports.** On the Fold's folded (cover) screen the conflict dialog overflowed below the keyboard area, hiding the choice buttons. The dialog now scales every layout dimension by a continuous viewport-Y density factor (clamped to 0.55 at ≤900 px, 1.0 at ≥1700 px) with hard readability floors on font sizes — buttons stay reachable on all foldable display modes.

## Fork changes (v0.3.0)

Versioned as **0.3.0 (versionCode 227)**. **This is a breaking release** — the Android package id changes from `com.game.sts2launcher` to `com.game.sts2launcher.modmanager`, and the external storage root moves from `/storage/emulated/0/StS2Launcher/` to `/storage/emulated/0/StS2LauncherMM/`. Existing 0.2.x users have to:

- Reinstall (the new package is a separate app — old data is not migrated automatically).
- Move mods from the old path: file manager → `StS2Launcher/Mods/<ModId>/` → copy contents to `StS2LauncherMM/Mods/<ModId>/`.
- Re-login to Steam and redownload the ~3 GB game payload (the old package's private data is sandboxed).

The old package can stay installed alongside or be uninstalled at the user's discretion.

### What's fixed / added

1. **Cloud-save destruction fix (issue #4).** The launcher used to silently overwrite real Steam Cloud progress with fresh-default save files when the EnumerateUserFiles cache failed to load before `SaveManager.InitSettingsData()` ran. Symptoms: open the launcher, press PLAY, see "no save" main menu, and your PC progress is gone too once Steam syncs the destruction back. The fix:
   - `CloudFileCache.WaitForLoadAsync()` — synchronous-blocking cache preload before any sync decision is made.
   - `LauncherPatches.ConstructDefaultPrefix()` — first `SaveManager.Instance` access (during `NGame._Ready`) now blocks up to 15s on the cache load and falls back to a local-only `SaveManager` if it can't be obtained, so writes never reach the cloud-wrapped store with the cache in an unknown state.
   - `CloudWriteQueue.Flush()` — now waits for the in-flight upload action, not just the queue depth (the original implementation returned the moment the queue dequeued, before the actual cloud RPC finished).
   - `CCloud_ClientBeginFileUpload_Request.platforms_to_sync = uint.MaxValue` — was being left at 0, which made Steam Cloud treat mobile uploads as "no-platform" and surface a sync conflict on PC even between matching beta-branch installs.
2. **Save Manager dialog.** A new explicit conflict-resolution UI replaces the silent timestamp-based resolver. On every PLAY, if local and cloud differ, a modal shows two side-by-side summary cards (W/L per character, max ascension, file size, file timestamp) and the user explicitly picks "Keep Local" / "Keep Cloud" / "Cancel". The same dialog is reachable from the **SAVE MANAGER** button on the launcher home — pressing it any time shows current sync state and offers an explicit re-sync.
3. **Repurposed MOD MANAGER button → SAVE MANAGER.** The mod manager UI is still WIP; the button now opens the save sync dialog. The mod manager screen code is preserved (commented out at the controller call site) for when the flow is finished.
4. **Package id renamed (issue #3).** `com.game.sts2launcher` → `com.game.sts2launcher.modmanager`. Stops the fork from sharing app data, external storage, ownership marker, and SharedPreferences with Ekyso's upstream APK. Side effect: the namespace and external-storage path also change.
5. **External storage root renamed.** `/storage/emulated/0/StS2Launcher/` → `/storage/emulated/0/StS2LauncherMM/`. Same rationale — no overlap with upstream. The `Mods` and `Saves` subfolders are auto-created at launcher start once "All Files Access" is granted.
6. **App label.** Home-screen / app-drawer label is now **"StS2 Launcher Mod"** so users running both forks can tell them apart.
7. **In-game Quit waits for cloud upload completion (was hard 5s timeout).** The flush now exits the moment `CloudWriteQueue` signals "no work in flight" rather than waiting a fixed window. Healthy uploads finish in 1-5 s, cellular ones in 10-15 s; the 5-minute ceiling only kicks in if Steam itself is unreachable, in which case waiting longer wouldn't help. Background-mode (swipe-to-recents) flush stays at 5 s — Android can force-kill us before a longer wait completes.
   - **Note for users:** during this Quit flush window the launcher process and its network connection stay alive in the background to finish the upload. If you watch the in-app log or system network indicators you'll see Steam traffic for those few seconds — that's the cloud sync completing, not a leak.

### Save sync — important caveats

- **Keep PC and mobile on the same Steam branch.** Mobile beta uploads to cloud are not readable by a PC client running Public, and vice versa. Steam shows a generic "sync conflict" with no auto-recovery; the only fix is to bring the PC branch in line with mobile's, run the game once, and then sync.
- **If a destructive sync did happen on a 0.2.x device**, the recovery path is documented at [shrederr/sts2-progress-rebuild](https://github.com/shrederr/sts2-progress-rebuild). Drop the tool's exe into `%AppData%\SlayTheSpire2\steam\` and run it with Steam closed; it rebuilds `progress.save` from the per-run `.run` history and pushes the rebuild into Steam's cloud cache + bumps `remotecache.vdf` so cloud sync picks it up on the next Steam launch.

## Fork changes (v0.2.2)

Versioned as **0.2.2 (versionCode 212)**. Installs cleanly over the official 0.2.0 / our 0.2.1 with `adb install -r` — the 3GB game payload, saves, and credentials are preserved as long as the APK keystore signature matches.

1. **Steam branch picker.** Tapping `CHECK FOR UPDATES` (or `DOWNLOAD GAME FILES` on a fresh install) now lists every public Steam branch — `public`, `public-beta`, etc. — pulled from `PICSGetProductInfo`. Pick a branch and the launcher checks/downloads against it. Selection is persisted to `OS.GetDataDir()/selected_branch`. Password-gated betas are listed but greyed out (slated for a later release).
2. **Branch-switch forces a fresh download.** Switching branches wipes `game/` + `download_state/` and pulls every file from scratch. The delta path occasionally produced byte-correct-by-SHA but visually broken installs (e.g. card art mismatched against the wrong slot) when crossing `public ↔ public-beta`; full redownload (~3GB) sidesteps that until the underlying delta gap is identified. Login, saves, and the ownership marker are kept.
3. **Mod-screen suppression on `public-beta`.** The MegaCrit beta build auto-opens an in-game `NSendFeedbackScreen` whose category dropdown throws `NullReferenceException` from `LocString.GetFormattedText` (missing localization rows in beta), leaving a stuck "Sending" overlay. We reflect over `MegaCrit.Sts2.Core.Nodes.Screens.FeedbackScreen.*` and force `_Ready` to skip with `Visible=false`, sidestepping the broken UI without disposing the node (which would crash `ScreenContext` polling on `NMainMenu`).
4. **Storage-permission prompt up front.** `All Files Access` is requested once on first launch via a confirmation dialog. Mods, save backup, and any future external-storage features depend on it; previously the request was buried inside the Mod Manager flow that the user might never reach.
5. **Branch picker hit-target.** The radio rows in the picker are wrapped in a flat `Button` so tapping anywhere on the row toggles the radio, with the `CheckBox` icon enlarged to ~28dp (issue #2).

## Fork changes (v0.2.1)

Versioned as **0.2.1 (versionCode 201)**.

1. **Mods load on current game versions.** The upstream reflection-based `ModLoaderPatches` (scanning after `ModManager.Initialize`) crashed with `NullReferenceException` on current game builds because the private field names it touched were renamed. This fork replaces the approach with a Harmony IL transpiler that rewrites `Path.Combine(..., "mods")` inside `ModManager.Initialize` itself, so the game's own recursive scanner walks `/storage/emulated/0/StS2Launcher/Mods/`. The Steam-only mod enumerator (`ReadSteamMods`) is also short-circuited because Android has no Steamworks runtime.
2. **Foldable UX.** `android:resizeableActivity="true"` so folding/unfolding triggers a smooth resize instead of Samsung's "Reopen app" prompt (see issue #1).
3. **Back-button guard.** A stray back swipe no longer instantly restarts the launcher and drops your in-progress run — the first press shows a "Press back again to exit" toast, and only a second press within 2 seconds propagates.

## Installing mods

> **Heads up**: the "MOD MANAGER" button on 0.2.x has been repurposed to **"SAVE MANAGER"** in 0.3.0 (it opens the cloud sync dialog). The in-launcher SAF mod-import flow is still WIP, so use the manual file-manager method below until it lands.

1. Grant the launcher "All files access" on first run when it prompts. Once granted, the launcher creates `/storage/emulated/0/StS2LauncherMM/Mods/` on its own.
2. Install any Android file manager that can browse internal storage — Material Files, Solid Explorer, FE File Explorer, Samsung's built-in **내 파일**, etc.
3. Navigate to `/storage/emulated/0/StS2LauncherMM/Mods/` and drop each mod as its own subfolder. A valid mod folder contains the mod's `.dll`, optional `.pck`, and a `<ModId>.json` manifest at its root — the same layout PC users paste into `Steam\steamapps\common\Slay the Spire 2\mods\`.

   ![Mods folder layout](docs/images/mods_folder.jpg)

4. Launch the game and tap PLAY. When the game's built-in "Load mods?" dialog appears, tap **OK** — the game will save the choice, restart through the launcher once, and come back up with mods loaded.

If no mods appear, check `adb logcat | grep "\[Mods\]"` — successful scans log `Redirected ModManager.Initialize to /storage/emulated/0/StS2LauncherMM/Mods`.

## Features

- **Steam authentication**  
  Login via SteamKit2 with Steam Guard 2FA support.
- **Game file download**  
  Depot download directly from Steam, with update checking.
- **Cloud saves**  
  Full Steam cloud sync via SteamKit2's CCloud API, with timestamp-aware conflict resolution and non-blocking background uploads.
- **Mobile adaptation**  
  Touch input, UI scaling, layout adjustments, and app lifecycle handling via Harmony runtime patches.
- **LAN multiplayer**  
  UDP broadcast discovery and manual IP join.
- **Shader warmup**  
  Vulkan pipeline cache persistence and canvas ubershader support to eliminate first-encounter stutters.
- **Credential security**  
  Steam refresh tokens encrypted at rest via Android Keystore (AES-256-GCM, hardware-backed TEE).

## How It Works

At startup, `STS2Mobile.dll` is loaded via `coreclr_create_delegate` and applies [Harmony](https://github.com/pardeike/Harmony) patches to adapt the desktop game for mobile. The launcher intercepts `GameStartupWrapper()` to present a Steam login screen before the game starts.

- **Launcher-only mode**  
If no game files are present, the app loads a minimal `bootstrap.pck` and shows the launcher UI for Steam login and game download.  
- **Normal mode**  
With game files downloaded, all patches apply against `sts2.dll` and the game runs natively after authentication.

## Engine Patches

Custom patches to the Godot 4.5.1 engine source for Android-specific issues:

- **Vulkan pipeline cache persistence**  
Saves compiled pipelines when the app loses focus, preventing recompilation after Android kills the process.
- **Canvas ubershaders**  
Enable ubershader fallback for 2D rendering, eliminating first-encounter VFX stutters from blocking pipeline compilation.

## Project Structure

```
src/STS2Mobile/
  ModEntry.cs              # Entry point ([UnmanagedCallersOnly] Apply())
  PatchHelper.cs           # Shared patch utility + logging
  Patches/                 # Harmony patches (one file per concern)
  Launcher/                # Programmatic Godot UI (MVC)
  Steam/                   # SteamKit2 login, depot download, cloud saves
android/                   # Godot Android gradle project
  src/.../GodotApp.java    # Activity, assembly setup, Keystore encryption
  assets/bootstrap.pck     # Minimal PCK for launcher-only mode
src/stubs/                 # Native library stubs (Steam API, Sentry)
scripts/                   # Build and tooling scripts
```

## Prerequisites

- .NET 9 SDK
- Android SDK + NDK (see `android/config.gradle` for versions)
- Python 3 (for `make-bootstrap-pck.py` and SCons)
- Original game files in `upstream/godot-export/`
- Custom Godot engine build (see `scripts/build-godot.sh`)
- FMOD SDK in `vendor/fmod-sdk/`

## Building

**Note: This is a WIP. There are other binaries that are required and will fail if you just run the `./build.sh` script. Godot Engine can be found on their repo https://github.com/godotengine/godot. Harmony can be found here https://github.com/Ekyso/Harmony but the version used in StS2 Launcher is compiled using dotnet 9.0. FMOD can be found here https://www.fmod.com/. Spine can be found here https://esotericsoftware.com/. I plan to upload the custom fork of Godot Engine used and the dotnet 9.0 Harmony soon. However, Spine and FMOD will not be uploaded due to licensing restrictions. Information on licensing can be found in the [THIRD-PARTY-NOTICES.txt](https://github.com/Ekyso/StS2-Launcher/blob/main/THIRD_PARTY_LICENSES.md) of the root folder.** 

```bash
bash scripts/build.sh
```

This runs the full pipeline:
1. `dotnet publish` the patcher (outputs `STS2Mobile.dll` + SteamKit2 dependencies)
2. Copies published DLLs to `android/assets/dotnet_bcl/`
3. Copies `libSystem.Security.Cryptography.Native.Android.so` to JNI libs (for TLS)
4. Bumps the version in `gradle.properties`
5. Builds the APK via `./gradlew assembleMonoRelease`

Output: `android/build/outputs/apk/mono/release/StS2Launcher-v<version>.apk`

### Installing

```bash
adb install -r android/build/outputs/apk/mono/release/StS2Launcher-v*.apk

# Fresh install (clear saved credentials + cached assemblies)
adb shell pm clear com.game.sts2launcher.modmanager
```

### Other build tasks

```bash
# Regenerate bootstrap PCK (only if project.godot changes)
python3 scripts/make-bootstrap-pck.py

# Rebuild Godot engine (only if engine source changes)
bash scripts/build-godot.sh

# Rebuild native stubs (requires Android NDK)
bash src/stubs/build_stubs.sh
```

## LAN Multiplayer

Both devices must be on the same local network. The mobile app discovers nearby games via UDP broadcast, or you can enter the PC's IP address manually.

On the PC, add `--fastmp` to the Steam launch options:
**Steam > Slay the Spire 2 > Properties > Launch Options** and enter `--fastmp`

This enables the fast multiplayer mode that the mobile client expects.

## Technical Notes

- Native library stubs (`src/stubs/`) provide no-op `.so` files for desktop-only libraries (Steamworks SDK, Sentry) so the linker is satisfied at runtime.
- The bootstrap PCK is a minimal `project.godot` wrapper that enables .NET module initialization without game files.
- The game's Sentry plugin has no `android.arm64` build, so it's disabled via PCK patching and Harmony patches.
- GodotSharp interop is manually bootstrapped in `ModEntry.cs` since the Godot SDK source generators aren't available.

## License

This project is licensed under the [MIT License](LICENSE). See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for third-party dependency licenses.

FMOD requires a commercial license if your project generates revenue. Spine Runtimes require a valid Spine Editor license. See the third-party licenses file for details.
