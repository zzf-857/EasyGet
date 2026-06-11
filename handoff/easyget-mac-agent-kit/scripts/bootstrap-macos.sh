#!/usr/bin/env bash
set -euo pipefail

echo "Checking EasyGet agent dependencies..."

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is not installed. Install it first: https://brew.sh/"
  exit 1
fi

if ! command -v yt-dlp >/dev/null 2>&1; then
  echo "Installing yt-dlp..."
  brew install yt-dlp
else
  echo "yt-dlp found: $(command -v yt-dlp)"
  yt-dlp --version || true
fi

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "Installing ffmpeg..."
  brew install ffmpeg
else
  echo "ffmpeg found: $(command -v ffmpeg)"
  ffmpeg -version | head -n 1 || true
fi

CONFIG_DIR="$HOME/Library/Application Support/EasyGetAgent"
COOKIE_DIR="$CONFIG_DIR/cookies"
mkdir -p "$COOKIE_DIR"

echo "Config directory: $CONFIG_DIR"
echo "Cookie profile directory: $COOKIE_DIR"
echo "Done."
