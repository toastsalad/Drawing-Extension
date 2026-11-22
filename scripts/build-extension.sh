#!/usr/bin/env bash
# Build the PlotterHelper engine as a single-file binary and assemble the
# Inkscape extension folder (PlotterHelper + plotter-helper.py + plotter-helper.inx).
#
# The assembled folder lands in dist/plotter-helper-extension/. It is not copied
# into Inkscape's extension directory unless you ask for it:
#
#   AUTO_INSTALL=true ./scripts/build-extension.sh
#   AUTO_INSTALL=true INKSCAPE_EXTENSIONS_DIR=/path ./scripts/build-extension.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${ROOT}/dist/plotter-helper-extension"
RID="${RID:-linux-x64}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
AUTO_INSTALL="${AUTO_INSTALL:-false}"

echo ">> Publishing PlotterHelper for $RID (self-contained=$SELF_CONTAINED)"
PUB="$(mktemp -d)"
SC_FLAG=""
if [ "$SELF_CONTAINED" = "true" ]; then SC_FLAG="--self-contained true"; fi
dotnet publish "$ROOT/src/PlotterHelper.Cli/PlotterHelper.Cli.csproj" -c Release \
    -r "$RID" -o "$PUB" $SC_FLAG -p:InkscapeAutoInstall="$AUTO_INSTALL"

echo ">> Assembling extension in $OUT"
rm -rf "$OUT"
mkdir -p "$OUT"
cp "$PUB/PlotterHelper" "$OUT/PlotterHelper"
chmod +x "$OUT/PlotterHelper"
cp "$ROOT/inkscape/plotter-helper.py" "$OUT/plotter-helper.py"
cp "$ROOT/inkscape/plotter-helper.inx" "$OUT/plotter-helper.inx"
cp "$ROOT/inkscape/plotter-helper-icon.svg" "$OUT/plotter-helper-icon.svg"
chmod +x "$OUT/plotter-helper.py"

if [ "$AUTO_INSTALL" = "true" ]; then
    echo ">> Copied into Inkscape's user extensions directory."
else
    echo ">> Done. Copy it yourself, or rerun with AUTO_INSTALL=true:"
    echo "   cp -r \"$OUT\" \"\${INKSCAPE_EXTENSIONS_DIR:-\$HOME/.config/inkscape/extensions}/plotter-helper-extension\""
fi
ls -la "$OUT"
