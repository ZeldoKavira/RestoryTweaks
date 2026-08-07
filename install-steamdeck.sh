#!/usr/bin/env bash
#
# Restory Tweaks - installer for Steam Deck / Linux.
#
# Installs BepInEx and the latest release of the mod, then tells you the one launch option you
# need to set. Re-run it any time to update the mod.
#
# Run straight from the repo, no clone needed:
#
#   curl -sSL https://raw.githubusercontent.com/ZeldoKavira/RestoryTweaks/main/install-steamdeck.sh | bash
#
# To uninstall, arguments go after "-s --" because the script arrives on stdin:
#
#   curl -sSL https://raw.githubusercontent.com/ZeldoKavira/RestoryTweaks/main/install-steamdeck.sh | bash -s -- --uninstall
#
# Or, if you have a local copy:
#
#   ./install-steamdeck.sh              install or update
#   ./install-steamdeck.sh --uninstall  remove the mod (leaves BepInEx)
#
# Nothing here reads $0 or the script's own directory, so piping into bash behaves identically to
# running a downloaded copy.
#
# Note on Proton: Restory is a Windows build running under Proton, so this installs the WINDOWS
# build of BepInEx. Its loader is a winhttp.dll shim that Proton will only pick up when the
# WINEDLLOVERRIDES launch option is set - hence the reminder at the end. A native-Linux BepInEx
# (run_bepinex.sh) is NOT what this game needs.

set -euo pipefail

REPO="ZeldoKavira/RestoryTweaks"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip"
PLUGIN="RestoryTweaks.dll"
LAUNCH_OPTION='WINEDLLOVERRIDES="winhttp=n,b" %command%'

say()  { printf '\033[36m==>\033[0m %s\n' "$1"; }
ok()   { printf '\033[32m    %s\033[0m\n' "$1"; }
warn() { printf '\033[33m    %s\033[0m\n' "$1"; }
die()  { printf '\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }

# ---------------------------------------------------------------- find the game

find_game() {
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/Restory"
        "$HOME/.local/share/Steam/steamapps/common/Restory"
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Restory"
    )

    # SD cards and other Steam libraries mount under /run/media on the Deck.
    while IFS= read -r dir; do
        [ -n "$dir" ] && candidates+=("$dir")
    done < <(find /run/media -maxdepth 4 -type d -name Restory -path '*/steamapps/common/*' 2>/dev/null || true)

    for dir in "${candidates[@]}"; do
        [ -f "$dir/Restory.exe" ] && { printf '%s' "$dir"; return 0; }
    done
    return 1
}

GAME="${GAME_DIR:-}"
if [ -z "$GAME" ]; then
    say "Looking for Restory..."
    # When piped, the env var has to go in front of bash - not in front of curl, which would only
    # set it for curl's own process.
    GAME="$(find_game)" || die "Couldn't find Restory. Re-run as: curl -sSL <url> | GAME_DIR=/path/to/Restory bash"
fi
[ -f "$GAME/Restory.exe" ] || die "Not a Restory install: $GAME"
ok "$GAME"

PLUGIN_DIR="$GAME/BepInEx/plugins"

# ---------------------------------------------------------------- uninstall

if [ "${1:-}" = "--uninstall" ]; then
    say "Uninstalling..."
    if [ -f "$PLUGIN_DIR/$PLUGIN" ]; then
        rm -f "$PLUGIN_DIR/$PLUGIN"
        ok "Removed $PLUGIN"
    else
        warn "Mod was not installed."
    fi
    echo
    echo "BepInEx itself was left in place. To remove it too, delete from the game folder:"
    echo "  BepInEx/  winhttp.dll  doorstop_config.ini  .doorstop_version  changelog.txt"
    exit 0
fi

# ---------------------------------------------------------------- tools

for tool in curl unzip; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' is required but not installed."
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---------------------------------------------------------------- BepInEx

if [ -f "$GAME/BepInEx/core/BepInEx.dll" ]; then
    say "BepInEx already installed, skipping."
else
    say "Downloading BepInEx..."
    curl -fsSL "$BEPINEX_URL" -o "$TMP/bepinex.zip" || die "BepInEx download failed."

    say "Installing BepInEx..."
    unzip -qo "$TMP/bepinex.zip" -d "$GAME" || die "Could not extract BepInEx."
    ok "BepInEx installed."
fi

# ---------------------------------------------------------------- the mod

say "Fetching the latest build of the mod..."

# Pinned to the rolling "latest" tag that CI republishes on every push, NOT to the API's
# /releases/latest. That endpoint picks whichever release it considers newest, and when the rolling
# release was briefly absent it quietly fell back to an old version tag - installing a stale DLL and
# reporting success. A fixed tag either has the current build or fails outright, which is far better.
DL_URL="https://github.com/$REPO/releases/download/latest/$PLUGIN"

say "Installing $PLUGIN..."
curl -fsSL "$DL_URL" -o "$TMP/$PLUGIN" \
    || die "Could not download $PLUGIN from $DL_URL - check the repo's Actions tab for a failed build."

mkdir -p "$PLUGIN_DIR"
cp "$TMP/$PLUGIN" "$PLUGIN_DIR/$PLUGIN"

# Verify rather than trust the copy.
[ -s "$PLUGIN_DIR/$PLUGIN" ] || die "The plugin did not copy across."
if [ "$(stat -c%s "$TMP/$PLUGIN")" != "$(stat -c%s "$PLUGIN_DIR/$PLUGIN")" ]; then
    die "The copied plugin does not match what was downloaded."
fi
# Print a fingerprint as well as the size, so "did my update actually land?" has an answer you can
# compare against the release page instead of a guess.
SUM="unavailable"
command -v sha256sum >/dev/null 2>&1 && SUM="$(sha256sum "$PLUGIN_DIR/$PLUGIN" | cut -c1-16)"
ok "$PLUGIN installed ($(stat -c%s "$PLUGIN_DIR/$PLUGIN") bytes, sha256 $SUM)."

# ---------------------------------------------------------------- launch option

cat <<EOF

Done.

ONE MORE STEP - the mod will not load without this:

  Steam > Restory > Properties > Launch Options, set:

    $LAUNCH_OPTION

  Restory is a Windows game running under Proton. BepInEx loads through a winhttp.dll shim, and
  Proton ignores that unless this override tells it to prefer the local copy.

Settings (after the first launch):
  $GAME/BepInEx/config/net.zeldo.restorytweaks.cfg

EOF
