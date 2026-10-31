#!/usr/bin/env bash
# Cross-compiles DropInOverlay.dll (Windows x64 PE) from Linux/macOS.
# Requires mingw-w64, cmake and ninja.
set -euo pipefail

cd "$(dirname "$0")"
OUT_DIR="${1:-../../../dist/native-overlay}"
BUILD_DIR="build/mingw-x64"
TOOLCHAIN="$(cd ../../../vendor/Universal-Dear-ImGui-Hook && pwd)/cmake/mingw-w64-x86_64.cmake"

cmake -S . -B "$BUILD_DIR" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN"
cmake --build "$BUILD_DIR" --parallel

mkdir -p "$OUT_DIR"
cp "$BUILD_DIR/DropInOverlay.dll" "$OUT_DIR/DropInOverlay.dll"
file "$OUT_DIR/DropInOverlay.dll"
