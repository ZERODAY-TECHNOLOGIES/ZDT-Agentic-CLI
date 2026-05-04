#!/usr/bin/env bash
# zdtllmcli installer — Linux + macOS.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI/main/install.sh | bash
#   curl -fsSL .../install.sh | bash -s -- --version v0.1.0   # pin a specific release
#   curl -fsSL .../install.sh | bash -s -- --uninstall        # remove the binary, keep settings
#
# What this does:
#   1. Detects your OS (Linux / macOS) and CPU arch (x64 / arm64).
#   2. Downloads the matching self-contained `zdt` binary from the latest GitHub Release.
#   3. Verifies the SHA256 against checksums.txt published with the release.
#   4. Installs to ~/.zdtllm/bin/zdt and appends the directory to your shell rc file's PATH.
#   5. Prints how to activate the new PATH in the CURRENT shell.
#
# No .NET runtime is required on your machine — the binary bundles everything it needs.

set -euo pipefail

REPO="ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI"
INSTALL_DIR="${ZDT_INSTALL_DIR:-$HOME/.zdtllm/bin}"
VERSION="${ZDT_VERSION:-}"
ACTION="install"

# Args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2 ;;
    --uninstall) ACTION="uninstall"; shift ;;
    -h|--help)
      sed -n '2,18p' "$0" 2>/dev/null || true
      exit 0
      ;;
    *) echo "zdt installer: unknown flag '$1'" >&2; exit 2 ;;
  esac
done

# ANSI colours (gracefully degrade if stdout isn't a TTY)
if [[ -t 1 ]]; then
  CYAN=$'\033[38;2;27;234;205m'
  GOLD=$'\033[38;2;229;217;54m'
  MUTE=$'\033[38;2;104;123;137m'
  RED=$'\033[38;2;239;68;68m'
  RESET=$'\033[0m'
  BOLD=$'\033[1m'
else
  CYAN=""; GOLD=""; MUTE=""; RED=""; RESET=""; BOLD=""
fi

err() { printf "%s✗ %s%s\n" "$RED" "$*" "$RESET" >&2; exit 1; }
say() { printf "%s→%s %s\n" "$CYAN" "$RESET" "$*"; }
ok()  { printf "%s✓%s %s\n" "$CYAN" "$RESET" "$*"; }

# Uninstall path — short-circuit before any network calls.
if [[ "$ACTION" == "uninstall" ]]; then
  if [[ -f "$INSTALL_DIR/zdt" ]]; then
    rm -f "$INSTALL_DIR/zdt"
    rmdir "$INSTALL_DIR" 2>/dev/null || true
    ok "removed $INSTALL_DIR/zdt"
  else
    say "$INSTALL_DIR/zdt is not installed."
  fi
  printf "%s  Settings preserved at %s/.zdtllm — remove manually if no longer needed.%s\n" "$MUTE" "$HOME" "$RESET"
  exit 0
fi

# OS detection
case "$(uname -s)" in
  Linux*)  OS="linux" ;;
  Darwin*) OS="osx" ;;
  *) err "unsupported OS: $(uname -s). zdt currently supports Linux + macOS via this script (Windows uses install.ps1)." ;;
esac

# Arch detection
case "$(uname -m)" in
  x86_64|amd64) ARCH="x64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *) err "unsupported architecture: $(uname -m). zdt ships x64 and arm64 binaries." ;;
esac

RID="${OS}-${ARCH}"
ASSET="zdt-${RID}.tar.gz"

# Required tools
for cmd in curl tar; do
  command -v "$cmd" >/dev/null 2>&1 || err "missing required tool: $cmd"
done

# Resolve version
if [[ -z "$VERSION" ]]; then
  say "resolving latest release..."
  LATEST_JSON=$(curl -fsSL --connect-timeout 10 "https://api.github.com/repos/${REPO}/releases/latest" || true)
  VERSION=$(printf '%s' "$LATEST_JSON" | grep -oE '"tag_name":\s*"[^"]+"' | head -n1 | cut -d'"' -f4 || true)
  [[ -n "$VERSION" ]] || err "could not resolve latest release tag from GitHub API. Check connectivity or pin --version explicitly."
fi

DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${VERSION}/${ASSET}"
CHECKSUM_URL="https://github.com/${REPO}/releases/download/${VERSION}/checksums.txt"

say "downloading ${BOLD}${VERSION}${RESET}${MUTE} for ${RID}${RESET}"
TMP=$(mktemp -d 2>/dev/null || mktemp -d -t zdt-install)
trap 'rm -rf "$TMP"' EXIT

curl -fsSL --connect-timeout 30 -o "$TMP/$ASSET" "$DOWNLOAD_URL" \
  || err "download failed: $DOWNLOAD_URL"

# SHA256 verification (best-effort: skipped with a warning if checksums.txt isn't published).
if curl -fsSL --connect-timeout 10 -o "$TMP/checksums.txt" "$CHECKSUM_URL" 2>/dev/null; then
  EXPECTED=$(grep "  ${ASSET}\$" "$TMP/checksums.txt" | awk '{print $1}' || true)
  if [[ -z "$EXPECTED" ]]; then
    printf "%swarn: checksum entry for %s not found; skipping verification%s\n" "$GOLD" "$ASSET" "$RESET" >&2
  else
    if command -v sha256sum >/dev/null 2>&1; then
      ACTUAL=$(sha256sum "$TMP/$ASSET" | awk '{print $1}')
    else
      # macOS ships shasum, not sha256sum.
      ACTUAL=$(shasum -a 256 "$TMP/$ASSET" | awk '{print $1}')
    fi
    [[ "$EXPECTED" == "$ACTUAL" ]] || err "SHA256 mismatch — expected $EXPECTED, got $ACTUAL. Aborting."
    ok "checksum verified"
  fi
else
  printf "%swarn: checksums.txt not available; skipping integrity check%s\n" "$GOLD" "$RESET" >&2
fi

# Install
mkdir -p "$INSTALL_DIR"
tar -xzf "$TMP/$ASSET" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/zdt"

# Append PATH line to whatever shell startup file applies. We add ONLY if the line
# isn't already present, so re-running the installer is idempotent.
PATH_LINE='export PATH="'"$INSTALL_DIR"':$PATH"'
PATCHED_RC=""
case "$(basename "${SHELL:-/bin/bash}")" in
  zsh)
    RC="$HOME/.zshrc"
    [[ -f "$RC" ]] || touch "$RC"
    grep -qsF "$INSTALL_DIR" "$RC" || { printf '\n# Added by zdtllmcli installer\n%s\n' "$PATH_LINE" >> "$RC"; PATCHED_RC="$RC"; }
    ;;
  fish)
    RC="$HOME/.config/fish/config.fish"
    mkdir -p "$(dirname "$RC")"
    [[ -f "$RC" ]] || touch "$RC"
    FISH_LINE='set -gx PATH '"$INSTALL_DIR"' $PATH'
    grep -qsF "$INSTALL_DIR" "$RC" || { printf '\n# Added by zdtllmcli installer\n%s\n' "$FISH_LINE" >> "$RC"; PATCHED_RC="$RC"; }
    ;;
  bash|*)
    # On macOS interactive bash reads ~/.bash_profile, on Linux it reads ~/.bashrc. Patch both
    # if they exist (or pick the more conventional one) so Terminal.app and a fresh xterm both
    # pick up the new PATH.
    for RC in "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.profile"; do
      if [[ -f "$RC" ]]; then
        grep -qsF "$INSTALL_DIR" "$RC" || { printf '\n# Added by zdtllmcli installer\n%s\n' "$PATH_LINE" >> "$RC"; PATCHED_RC="$RC"; }
      fi
    done
    # If NONE of those exist, create ~/.profile so the next login picks it up.
    if [[ -z "$PATCHED_RC" ]]; then
      RC="$HOME/.profile"
      printf '\n# Added by zdtllmcli installer\n%s\n' "$PATH_LINE" >> "$RC"
      PATCHED_RC="$RC"
    fi
    ;;
esac

# Final banner — make the PATH situation extremely clear.
printf '\n'
printf "%s╭──────────────────────────────────────────────────────────────╮%s\n" "$CYAN" "$RESET"
printf "%s│%s  %s✓ zdt %s installed at %s%s%s\n" "$CYAN" "$RESET" "$BOLD" "$RESET" "$BOLD" "$INSTALL_DIR/zdt" "$RESET"
printf "%s│%s\n" "$CYAN" "$RESET"
if [[ -n "$PATCHED_RC" ]]; then
  printf "%s│%s  %sPATH updated in %s%s%s\n" "$CYAN" "$RESET" "$MUTE" "$RESET" "$PATCHED_RC" ""
else
  printf "%s│%s  %sPATH already includes %s%s%s\n" "$CYAN" "$RESET" "$MUTE" "$RESET" "$INSTALL_DIR" ""
fi
printf "%s│%s\n" "$CYAN" "$RESET"
printf "%s│%s  %sAdd this directory to your PATH if your shell didn't pick it up:%s\n" "$CYAN" "$RESET" "$BOLD" "$RESET"
printf "%s│%s      %s%s%s\n" "$CYAN" "$RESET" "$GOLD" "$INSTALL_DIR" "$RESET"
printf "%s│%s\n" "$CYAN" "$RESET"
printf "%s│%s  %sActivate now in this shell:%s\n" "$CYAN" "$RESET" "$BOLD" "$RESET"
printf "%s│%s      %sexport PATH=\"%s:\$PATH\"%s\n" "$CYAN" "$RESET" "$GOLD" "$INSTALL_DIR" "$RESET"
printf "%s│%s\n" "$CYAN" "$RESET"
printf "%s│%s  Or open a new terminal — the change persists across sessions.\n" "$CYAN" "$RESET"
printf "%s│%s\n" "$CYAN" "$RESET"
printf "%s│%s  Then run %szdt%s for the first-run wizard, or %szdt --help%s.\n" "$CYAN" "$RESET" "$BOLD" "$RESET" "$BOLD" "$RESET"
printf "%s╰──────────────────────────────────────────────────────────────╯%s\n" "$CYAN" "$RESET"
printf '\n'
