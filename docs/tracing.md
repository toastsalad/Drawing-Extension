# Tracing raster line art

`--trace` turns a PNG of line art into plotter path data by centerline: each line
you can see becomes one stroke running from its beginning to its end. That is a
different operation from the region tracing Potrace, Illustrator and Inkscape's
brightness cutoff do, which give the outline of the ink: two parallel paths a
line-width apart, hollow in the middle. An outline tracer suits solid shapes; a
drawing made of lines needs a centerline tracer.

```bash
# Draw the traced lines.
PlotterHelper --mode cut   --input artwork.png --trace --gcode out.gcode --trace-svg traced.svg

# Or hatch the parts that are too thick to be a single pen stroke.
PlotterHelper --mode hatch --input artwork.png --trace --trace-width 180 --gcode out.gcode
```

Ink wider than `--trace-thick-width` is emitted as a closed region instead of a
centerline, so `--mode hatch` fills it with the usual hatch styles while
everything else is drawn as a line. `--trace-svg` keeps the intermediate SVG,
which is worth opening in Inkscape to check the trace before committing to a
plot. Only PNG is decoded in-tree; transcode a JPEG first.

The trace is controlled by the `--trace-*` options listed in `PlotterHelper --help`:
threshold window and sensitivity, upscaling and a pixel budget, spur removal,
junction merging and splicing, gap bridging, simplification and Bézier fitting,
and the width at which ink becomes a filled region. Two of them matter most:
`--trace-window` must stay smaller than the gap between adjacent lines or dense
hatching welds into a solid slab, and `--trace-upscale` is reduced automatically
to keep the working raster under `--trace-max-pixels`.
