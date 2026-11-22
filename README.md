# PlotterHelper

Converts SVG drawings into G-code for pen plotters and CNC routers: outlines,
hatch fill, other fills, and PNG line art traced to centerlines. C# on .NET 10.

## Install

Download a binary from Releases. Both are self-contained, no .NET runtime needed.

```
# Linux x64
chmod +x PlotterHelper-linux-x64 && ./PlotterHelper-linux-x64 --help

# Windows x64, in PowerShell
.\PlotterHelper-win-x64.exe --help
```

For Inkscape, run `./scripts/build-extension.sh` and copy `dist/plotter-helper-extension`
into Inkscape's extensions directory (*Edit > Preferences > System*).

As a library, reference `src/PlotterHelper.Core/PlotterHelper.Core.csproj`: `SvgParser`
parses, `Compiler` writes G-code.

## Usage

```bash
PlotterHelper --mode cut      --input drawing.svg --gcode cut.gcode
PlotterHelper --mode hatch    --input drawing.svg --preview hatched.svg --hatch-spacing 1mm
PlotterHelper --mode hatchcut --input drawing.svg --gcode filled.gcode --hatch-spacing 1mm
```

`--help` lists the options; PNG line art tracing is in [docs/tracing.md](docs/tracing.md).

## License

GPL-2.0-or-later, see [LICENSE](LICENSE). The hatch fill and pen-lift optimizer are
ported from EggBot's `eggbot_hatch.py` (GPL-2.0-or-later, (c) Windell H. Oskay and
contributors), as used by `evil-mad/axidraw`.
