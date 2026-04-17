#!/usr/bin/env bash
# One-shot bootstrap: harvests build-time + run-time binaries from the user-supplied
# dependency files (in $DEPS_DIR) and drops them at the paths this repo expects.
# Idempotent — re-running just overwrites.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO_PARENT="$(cd "$ROOT/.." && pwd)"
DEPS_DIR="${DEPS_DIR:-$REPO_PARENT/req_files}"

EKYSO_APK="$DEPS_DIR/StS2Launcher-v0.2.0.apk"
GODOT_TPZ="$DEPS_DIR/Godot_v4.5.1-stable_mono_export_templates.tpz"
FMOD_TARBALL="$DEPS_DIR/fmodstudioapi20313android.tar.gz"
STEAM_GAME_DIR="$DEPS_DIR/data_sts2_windows_x86_64"

# csproj references `../../upstream/...` relative to src/STS2Mobile/ → repo root.
UPSTREAM_PUBLISH="$ROOT/upstream/godot-export/.godot/mono/publish/arm64"
LIBS_RELEASE="$ROOT/android/libs/release"
LIBS_DEBUG="$ROOT/android/libs/debug"
BCL_DIR="$ROOT/android/assets/dotnet_bcl"
FMOD_VENDOR="$ROOT/vendor/fmod-sdk"
WORK="$ROOT/.setup-work"

log() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

# 1. Preflight
log "Preflight checks..."
[ -f "$EKYSO_APK"     ] || fail "Missing $EKYSO_APK"
[ -f "$GODOT_TPZ"     ] || fail "Missing $GODOT_TPZ"
[ -f "$FMOD_TARBALL"  ] || fail "Missing $FMOD_TARBALL"
[ -d "$STEAM_GAME_DIR" ] || fail "Missing $STEAM_GAME_DIR"
[ -f "$STEAM_GAME_DIR/sts2.dll" ] || fail "Missing $STEAM_GAME_DIR/sts2.dll"

# 2. Prepare scratch workspace
log "Preparing scratch workspace at $WORK"
rm -rf "$WORK"
mkdir -p "$WORK/ekyso_apk" "$WORK/tpz" "$WORK/android_source"

# 3. Extract Ekyso APK
log "Extracting Ekyso APK..."
unzip -oq "$EKYSO_APK" -d "$WORK/ekyso_apk"

# 4. Populate android/assets/dotnet_bcl/ with the launcher's full BCL.
# dotnet publish will overwrite STS2Mobile.dll (and its deps) later.
log "Populating $BCL_DIR with BCL from APK..."
mkdir -p "$BCL_DIR"
cp -f "$WORK/ekyso_apk/assets/dotnet_bcl/"*.dll "$BCL_DIR/"

# 5. Populate android/libs/release/arm64-v8a/ with native .so files.
# Skip libgodot_android.so + libc++_shared.so — those live inside the AAR.
log "Populating $LIBS_RELEASE/arm64-v8a/ with native libs..."
mkdir -p "$LIBS_RELEASE/arm64-v8a"
for so in "$WORK/ekyso_apk/lib/arm64-v8a/"*.so; do
    name="$(basename "$so")"
    case "$name" in
        libgodot_android.so|libc++_shared.so) continue ;;
    esac
    cp -f "$so" "$LIBS_RELEASE/arm64-v8a/$name"
done

# Debug variant mirrors release for our purposes (we don't ship a dev-built AAR).
log "Mirroring natives into $LIBS_DEBUG/arm64-v8a/"
mkdir -p "$LIBS_DEBUG/arm64-v8a"
cp -f "$LIBS_RELEASE/arm64-v8a/"*.so "$LIBS_DEBUG/arm64-v8a/"

# 6. Extract Godot templates → get the AAR
log "Extracting Godot export templates..."
unzip -oq "$GODOT_TPZ" -d "$WORK/tpz"
[ -f "$WORK/tpz/templates/android_source.zip" ] \
    || fail "android_source.zip not found inside tpz"
unzip -oq "$WORK/tpz/templates/android_source.zip" -d "$WORK/android_source"

[ -f "$WORK/android_source/libs/release/godot-lib.template_release.aar" ] \
    || fail "Release AAR not found in android_source"

log "Copying AAR → $LIBS_RELEASE/"
cp -f "$WORK/android_source/libs/release/godot-lib.template_release.aar" "$LIBS_RELEASE/"

if [ -f "$WORK/android_source/libs/debug/godot-lib.template_debug.aar" ]; then
    log "Copying debug AAR → $LIBS_DEBUG/"
    cp -f "$WORK/android_source/libs/debug/godot-lib.template_debug.aar" "$LIBS_DEBUG/"
fi

# 7. Patch the AAR so its embedded libgodot_android.so is Ekyso's custom build.
# Same effect as scripts/build-godot.sh's `zip -u` step, but via Python since
# Git Bash on Windows doesn't ship `zip`.
log "Patching AAR with Ekyso's libgodot_android.so..."
EKYSO_GODOT_SO="$WORK/ekyso_apk/lib/arm64-v8a/libgodot_android.so"
[ -f "$EKYSO_GODOT_SO" ] || fail "Ekyso libgodot_android.so not found"

patch_aar() {
    local aar="$1"
    local so="$2"
    python - "$aar" "$so" <<'PYEOF'
import shutil, sys, zipfile, os
aar, so = sys.argv[1], sys.argv[2]
tmp = aar + ".new"
entry_name = "jni/arm64-v8a/libgodot_android.so"
with zipfile.ZipFile(aar, 'r') as src, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        if item.filename == entry_name:
            continue
        dst.writestr(item, src.read(item.filename))
    dst.write(so, entry_name)
os.replace(tmp, aar)
print(f"  patched: {os.path.basename(aar)}")
PYEOF
}

patch_aar "$LIBS_RELEASE/godot-lib.template_release.aar" "$EKYSO_GODOT_SO"
if [ -f "$LIBS_DEBUG/godot-lib.template_debug.aar" ]; then
    patch_aar "$LIBS_DEBUG/godot-lib.template_debug.aar" "$EKYSO_GODOT_SO"
fi
log "AAR patched."

# 8. Extract FMOD SDK
log "Extracting FMOD SDK → $FMOD_VENDOR/"
mkdir -p "$FMOD_VENDOR"
tar -xzf "$FMOD_TARBALL" -C "$FMOD_VENDOR" --strip-components=1

FMOD_JAR="$FMOD_VENDOR/api/core/lib/fmod.jar"
[ -f "$FMOD_JAR" ] || fail "fmod.jar missing after extract"

log "Copying fmod.jar → $LIBS_RELEASE/ (and debug)"
cp -f "$FMOD_JAR" "$LIBS_RELEASE/fmod.jar"
cp -f "$FMOD_JAR" "$LIBS_DEBUG/fmod.jar"

# 8b. Crypto JAR: build.gradle references
#   vendor/godot/modules/mono/thirdparty/libSystem.Security.Cryptography.Native.Android.jar
# Without it, GodotApp's static init loads libSystem.Security.Cryptography.Native.Android.so
# which then SIGABRTs looking up net.dot.android.crypto.DotnetProxyTrustManager.
CRYPTO_JAR_DIR="$ROOT/vendor/godot/modules/mono/thirdparty"
CRYPTO_JAR="$CRYPTO_JAR_DIR/libSystem.Security.Cryptography.Native.Android.jar"
if [ ! -f "$CRYPTO_JAR" ]; then
    log "Fetching Mono crypto JAR from godot 4.5.1-stable..."
    mkdir -p "$CRYPTO_JAR_DIR"
    curl -fsSL -o "$CRYPTO_JAR" \
        "https://raw.githubusercontent.com/godotengine/godot/4.5.1-stable/modules/mono/thirdparty/libSystem.Security.Cryptography.Native.Android.jar"
fi
[ -f "$CRYPTO_JAR" ] || fail "Crypto JAR missing"

# 9. Populate compile-time references for the csproj
log "Populating compile-time references at $UPSTREAM_PUBLISH/"
mkdir -p "$UPSTREAM_PUBLISH"
cp -f "$WORK/ekyso_apk/assets/dotnet_bcl/0Harmony.dll"   "$UPSTREAM_PUBLISH/"
cp -f "$WORK/ekyso_apk/assets/dotnet_bcl/GodotSharp.dll" "$UPSTREAM_PUBLISH/"
cp -f "$STEAM_GAME_DIR/sts2.dll" "$UPSTREAM_PUBLISH/"
for extra in sts2.pdb sts2.deps.json; do
    [ -f "$STEAM_GAME_DIR/$extra" ] && cp -f "$STEAM_GAME_DIR/$extra" "$UPSTREAM_PUBLISH/" || true
done

# 10. Create android/local.properties pointing at the installed SDK/NDK
LOCAL_PROPS="$ROOT/android/local.properties"
if [ ! -f "$LOCAL_PROPS" ]; then
    log "Writing $LOCAL_PROPS"
    # Prefer LOCALAPPDATA (Windows Git Bash); fall back to common WSL/Linux paths.
    if [ -n "${LOCALAPPDATA:-}" ]; then
        SDK_WIN_PATH="$LOCALAPPDATA\\Android\\Sdk"
        SDK_WIN_ESC="${SDK_WIN_PATH//\\/\\\\}"
        NDK_WIN_ESC="${SDK_WIN_ESC}\\\\ndk\\\\28.1.13356709"
    else
        SDK_WIN_ESC="$HOME/Android/Sdk"
        NDK_WIN_ESC="$HOME/Android/Sdk/ndk/28.1.13356709"
    fi
    cat > "$LOCAL_PROPS" <<EOF
sdk.dir=$SDK_WIN_ESC
ndk.dir=$NDK_WIN_ESC
EOF
else
    log "$LOCAL_PROPS already exists — leaving it as-is."
fi

# 11. Clean up scratch
log "Cleaning scratch workspace..."
rm -rf "$WORK"

# 12. Report
log "Done."
echo ""
echo "Next step: run"
echo "    cd '$ROOT/src/STS2Mobile' && dotnet publish -c Release"
echo "then, from '$ROOT/android':"
echo "    ./gradlew assembleMonoRelease"
echo "(or use Android Studio: open the 'android' folder and build monoRelease.)"
