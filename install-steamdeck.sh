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

say "Fetching the latest release of the mod..."

# Ask the API for the newest release asset rather than assuming a version number, so this script
# keeps working as new releases are published.
API="https://api.github.com/repos/$REPO/releases/latest"
DL_URL="$(curl -fsSL "$API" | grep -o "https://github.com/$REPO/releases/download/[^\"]*$PLUGIN" | head -1 || true)"

if [ -z "$DL_URL" ]; then
    die "No $PLUGIN found in the latest release of $REPO. Has a release been published yet?"
fi

TAG="$(printf '%s' "$DL_URL" | awk -F/ '{print $(NF-1)}')"
say "Installing $PLUGIN ($TAG)..."
curl -fsSL "$DL_URL" -o "$TMP/$PLUGIN" || die "Mod download failed."

mkdir -p "$PLUGIN_DIR"
cp "$TMP/$PLUGIN" "$PLUGIN_DIR/$PLUGIN"

# Verify rather than trust the copy.
[ -s "$PLUGIN_DIR/$PLUGIN" ] || die "The plugin did not copy across."
if [ "$(stat -c%s "$TMP/$PLUGIN")" != "$(stat -c%s "$PLUGIN_DIR/$PLUGIN")" ]; then
    die "The copied plugin does not match what was downloaded."
fi
ok "$PLUGIN installed ($(stat -c%s "$PLUGIN_DIR/$PLUGIN") bytes)."

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
