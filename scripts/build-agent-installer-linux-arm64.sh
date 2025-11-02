#!/bin/bash
set -e

# === CONFIG ===
PROJECT_NAME="Kraken.Agent.Installer"
PLATFORM="linux-arm64"
PROJECT_PATH="../src/$PROJECT_NAME/$PROJECT_NAME.csproj"
OUTPUT_DIR="$(dirname "$0")/build/$PLATFORM/$PROJECT_NAME"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ZIP_PATH="$SCRIPT_DIR/build/$PROJECT_NAME-$PLATFORM.zip"
VERSION_FILE="$OUTPUT_DIR/version.txt"

# === PREP ===
mkdir -p "$OUTPUT_DIR"

# === VERSION SETUP ===
if [ -z "$BUILD_NUMBER" ]; then
    BUILD_NUMBER=1.0.0
    echo "[WARN] BUILD_NUMBER not set. Using default: $BUILD_NUMBER"
fi

echo "[INFO] Building $PROJECT_NAME for $PLATFORM - Version $BUILD_NUMBER"

# === 1. PUBLISH PROJECT ===
dotnet publish "$PROJECT_PATH" -c Release -r $PLATFORM -p:Version=$BUILD_NUMBER -o "$OUTPUT_DIR"

# === 2. CREATE version.txt ===
echo "$BUILD_NUMBER" > "$VERSION_FILE"
echo "[INFO] Created version.txt with value: $BUILD_NUMBER"

# === 3. ZIP ===
echo "[INFO] Zipping contents of: $OUTPUT_DIR"
echo "[INFO] ZIP output will be: $ZIP_PATH"
mkdir -p "$(dirname "$ZIP_PATH")"

(cd "$OUTPUT_DIR" && zip -r "$ZIP_PATH" .)

echo "[✅] ZIP created: $ZIP_PATH"

