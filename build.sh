#!/bin/sh
# Builds pdfview for macOS or Linux, whichever this is run on. The packages
# land in src/tauri/target/release/bundle.
#
# Needs Rust (https://rustup.rs). Linux also needs the WebKitGTK headers:
#
#   sudo apt install build-essential pkg-config libssl-dev libwebkit2gtk-4.1-dev \
#     libxdo-dev libayatana-appindicator3-dev librsvg2-dev
#
# Anything after the script name goes to the bundler, for example
#   sh build.sh --target universal-apple-darwin

set -e
cd "$(dirname "$0")/src/tauri"

if ! command -v cargo-tauri >/dev/null 2>&1; then
  echo "Installing the Tauri CLI..."
  cargo install tauri-cli --version '^2' --locked
fi

cargo tauri build "$@"
