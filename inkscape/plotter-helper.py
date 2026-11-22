#!/usr/bin/env python3
"""
PlotterHelper Inkscape extension.

An ``inkex.EffectExtension`` that:
  * receives the document as ``self.document`` (Inkscape 1.x does NOT pipe SVG to
    stdin for python extensions),
  * serializes it to a temp file and invokes the bundled .NET engine (PlotterHelper),
  * writes the generated G-code (and an optional preview SVG file) to the chosen
    Output Directory,
  * shows a zenity progress dialog for long runs (driven by "PROGRESS <pct>"
    lines the engine emits on stderr), with cancel support,
  * leaves the Inkscape document untouched (replacing it crashes Inkscape), and
  * is silent on success -- only errors are reported.

All geometry work (SVG parsing, flattening, hatching, G-code compilation) happens
in the .NET binary, which is many orders of magnitude faster than Python implementations.
"""
import os
import json
import re
import sys
import shutil
import subprocess
import tempfile

try:
    import inkex
    from lxml import etree
except ImportError:  # running outside Inkscape without inkex/lxml on the path
    sys.stderr.write(
        "PlotterHelper: inkex/lxml is not importable. This script must be run by Inkscape "
        "(or with Inkscape's extensions directory on PYTHONPATH).\n"
    )
    sys.exit(1)


def _err(msg):
    """Write a message to stderr (Inkscape shows it in the message area)."""
    sys.stderr.write(str(msg) + "\n")


def here(name):
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), name)


def find_engine():
    for candidate in ("PlotterHelper", "PlotterHelper.exe"):
        p = here(candidate)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
    on_path = shutil.which("PlotterHelper")
    if on_path:
        return on_path
    raise RuntimeError("PlotterHelper engine executable not found next to plotter-helper.py or on PATH")


def _s(value):
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def _run_engine(engine, args):
    """Run the PlotterHelper engine, optionally driving a zenity progress dialog from
    "PROGRESS <pct>\\t<label>" lines on stderr. Returns (returncode, stderr_text)
    where stderr_text excludes the PROGRESS lines (those went to the dialog)."""
    if shutil.which("zenity") is None:
        r = subprocess.run([engine] + args, capture_output=True, text=True)
        text = "\n".join(l for l in (r.stderr or "").splitlines() if not l.startswith("PROGRESS "))
        return r.returncode, text

    import threading
    import queue

    zproc = subprocess.Popen(
        ["zenity", "--progress", "--auto-close",
         "--title=Plotter-Helper", "--text=Starting\u2026", "--percentage=0"],
        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, text=True)
    eproc = subprocess.Popen([engine] + args, stdout=subprocess.DEVNULL,
                             stderr=subprocess.PIPE, text=True, bufsize=1)
    q = queue.Queue()

    def reader():
        for line in eproc.stderr:
            q.put(line.rstrip("\n"))
        q.put(None)

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    other = []
    canceled = False
    while True:
        try:
            line = q.get(timeout=0.1)
        except queue.Empty:
            # zenity gone: only treat as a real cancel if it was the Cancel button
            # (exit code 1). If zenity merely died (GTK crash / window closed), keep
            # the engine running to completion instead of killing a good job (-15).
            if zproc.poll() is not None and zproc.returncode == 1:
                canceled = True
                break
            continue
        if line is None:
            break
        if line.startswith("PROGRESS "):
            parts = line.split("\t", 1)
            pct = parts[0][len("PROGRESS "):].strip()
            txt = parts[1] if len(parts) > 1 else ""
            try:
                zproc.stdin.write(pct + "\n")
                zproc.stdin.write("#" + txt + "\n")
                zproc.stdin.flush()
            except Exception:
                pass
            if zproc.poll() is not None and zproc.returncode == 1:
                canceled = True
                break
        else:
            other.append(line)

    try:
        if canceled:
            eproc.terminate()
            try:
                eproc.wait(timeout=2)
            except Exception:
                eproc.kill()
        else:
            eproc.wait()
            try:
                zproc.stdin.write("100\n")
                zproc.wait(timeout=2)
            except Exception:
                pass

        # Drain anything still queued.
        try:
            while True:
                line = q.get_nowait()
                if line is not None and not line.startswith("PROGRESS "):
                    other.append(line)
        except queue.Empty:
            pass
    finally:
        # Always close the engine stderr + zenity stdin pipes (a broken-pipe write
        # used to skip zproc.stdin.close() and leak a TextIOWrapper -> ResourceWarning).
        for _pipe in (zproc.stdin, eproc.stderr):
            try:
                _pipe.close()
            except Exception:
                pass
        try:
            if zproc.poll() is None:
                zproc.terminate()
        except Exception:
            pass
    return eproc.returncode, "\n".join(other)


class PlotterHelper(inkex.EffectExtension):
    """Axidraw-style hatch fill, backed by the .NET PlotterHelper engine."""

    def __init__(self):
        super().__init__()

    def add_arguments(self, pars):
        Bool = inkex.Boolean
        pars.add_argument("--tab", type=str, dest="tab", default="mode")
        pars.add_argument("--hatch_sub", type=str, dest="hatch_sub", default="hatch_basic")
        pars.add_argument("--features_sub", type=str, dest="features_sub", default="pen")
        pars.add_argument("--mode", type=str, default="hatch")
        pars.add_argument("--preview", type=Bool, default=True)
        pars.add_argument("--show_outlines_in_preview", type=Bool, default=True)
        pars.add_argument("--include_outlines", type=Bool, default=True)

        pars.add_argument("--unit", type=str, default="mm")
        pars.add_argument("--travel_speed", type=float, default=3000)
        pars.add_argument("--cutting_speed", type=float, default=750)
        pars.add_argument("--passes", type=int, default=1)
        pars.add_argument("--pass_depth", type=float, default=1)
        pars.add_argument("--directory", type=str, default="")
        pars.add_argument("--filename", type=str, default="output.gcode")
        pars.add_argument("--filename_suffix", type=Bool, default=True)
        pars.add_argument("--tool_power_command", type=str, default="M3 S255;")
        pars.add_argument("--tool_off_command", type=str, default="M5;")
        pars.add_argument("--dwell_time", type=float, default=0)
        pars.add_argument("--dwell_after_pen_up", type=float, default=0)
        pars.add_argument("--approximation_tolerance", type=str, default="0.01")

        pars.add_argument("--machine_origin", type=str, default="bottom-left")
        pars.add_argument("--invert_y", type=Bool, default=False)
        pars.add_argument("--bed_width", type=float, default=0)
        pars.add_argument("--bed_height", type=float, default=0)
        pars.add_argument("--horizontal_offset", type=float, default=0)
        pars.add_argument("--vertical_offset", type=float, default=0)
        pars.add_argument("--scaling_factor", type=float, default=1)

        pars.add_argument("--header", type=str, default="")
        pars.add_argument("--footer", type=str, default="")
        pars.add_argument("--do_z_axis_start", type=Bool, default=False)
        pars.add_argument("--z_axis_start", type=float, default=0)
        pars.add_argument("--return_home_end", type=Bool, default=False)
        pars.add_argument("--do_tool_off_start", type=Bool, default=True)
        pars.add_argument("--do_tool_off_end", type=Bool, default=True)

        pars.add_argument("--hatch_spacing", type=float, default=3.0)
        pars.add_argument("--hatch_units", type=str, default="mm")
        pars.add_argument("--hatch_angle", type=float, default=45)
        pars.add_argument("--cross_hatch", type=Bool, default=False)
        pars.add_argument("--connect_ends", type=Bool, default=True)
        pars.add_argument("--hatch_scope", type=float, default=3.0)
        pars.add_argument("--inset", type=Bool, default=True)
        pars.add_argument("--inset_dist", type=float, default=0.5)
        pars.add_argument("--hatch_tolerance", type=float, default=2.0)
        pars.add_argument("--handle_viewbox", type=Bool, default=False)
        pars.add_argument("--fill_style", type=str, default="parallel")
        pars.add_argument("--wave_amplitude", type=float, default=1.0)
        pars.add_argument("--wave_wavelength", type=float, default=0.0)
        pars.add_argument("--jitter", type=Bool, default=False)
        pars.add_argument("--jitter_amount", type=float, default=0.3)
        pars.add_argument("--jitter_seed", type=str, default="12345")
        pars.add_argument("--group_by_color", type=Bool, default=False)
        pars.add_argument("--pause_between_colors", type=Bool, default=False)
        pars.add_argument("--pause_command", type=str, default="M0")
        pars.add_argument("--per_color_config", type=str, default="")
        pars.add_argument("--save_per_color_config", type=Bool, default=False)
        pars.add_argument("--tile_width", type=float, default=0)
        pars.add_argument("--tile_height", type=float, default=0)
        pars.add_argument("--tile_overlap", type=float, default=0)
        pars.add_argument("--tile_alignment_marks", type=Bool, default=False)
        pars.add_argument("--tile_mark_size", type=float, default=1)
        pars.add_argument("--overcut", type=float, default=0)
        pars.add_argument("--inner_before_outer", type=Bool, default=False)
        pars.add_argument("--optimize_travel", type=Bool, default=False)
        pars.add_argument("--optimize_travel_reverse", type=Bool, default=True)
        pars.add_argument("--connect_outlines", type=Bool, default=False)
        pars.add_argument("--connect_outlines_dist", type=float, default=2.0)
        pars.add_argument("--double_pass_short", type=Bool, default=False)
        pars.add_argument("--double_pass_length", type=float, default=2.0)
        pars.add_argument("--output_target", type=str, default="disk")
        pars.add_argument("--moonraker_url", type=str, default="")
        pars.add_argument("--moonraker_api_key", type=str, default="")
        pars.add_argument("--moonraker_filename", type=str, default="")
        pars.add_argument("--klipper_thumbnail", type=Bool, default=True)

    _DRAWABLE_TAGS = {"path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "use"}
    _DEF_TAGS = {"defs", "symbol", "marker", "pattern", "clippath", "mask"}

    def _color_to_hex(self, s):
        if not s:
            return None
        t = s.strip()
        low = t.lower()
        if low in ("none", "transparent", "currentcolor") or low.startswith("url("):
            return None
        try:
            c = inkex.Color(t)
            return "%02X%02X%02X" % (int(round(c.red)), int(round(c.green)), int(round(c.blue)))
        except Exception:
            return None

    def _is_visible(self, el):
        cur = el
        while cur is not None:
            try:
                d = cur.style.get("display") or cur.get("display")
                v = cur.style.get("visibility") or cur.get("visibility")
            except Exception:
                d = cur.get("display")
                v = cur.get("visibility")
            if d == "none" or v in ("hidden", "collapse"):
                return False
            cur = cur.getparent()
        return True

    def _in_defs(self, el):
        cur = el.getparent()
        while cur is not None:
            if cur.tag.rsplit("}", 1)[-1].lower() in self._DEF_TAGS:
                return True
            cur = cur.getparent()
        return False

    def _is_fillable(self, el):
        tag = el.tag.rsplit("}", 1)[-1]
        if tag in ("rect", "circle", "ellipse", "polygon", "use"):
            return True
        if tag == "path":
            return "z" in (el.get("d") or "").lower()
        return False

    def _collect_pen_colors(self):
        opts = self.options
        mode = getattr(opts, "mode", "hatch")
        hatch_on = (mode == "hatch")
        include_outlines = bool(getattr(opts, "include_outlines", True))
        outlines_on = (mode == "cut") or (hatch_on and include_outlines)
        order, seen = [], set()
        for el in self.document.getroot().iter():
            if not isinstance(el.tag, str):
                continue  # skip comments / processing instructions (lxml gives them a function .tag)
            tag = el.tag.rsplit("}", 1)[-1]
            if tag not in self._DRAWABLE_TAGS:
                continue
            if self._in_defs(el) or not self._is_visible(el):
                continue
            try:
                st = el.specified_style()
            except Exception:
                st = {}
            if outlines_on:
                h = self._color_to_hex(st.get("stroke"))
                if h and h not in seen:
                    seen.add(h)
                    order.append(h)
            if hatch_on:
                h = self._color_to_hex(st.get("fill"))
                if h and self._is_fillable(el) and h not in seen:
                    seen.add(h)
                    order.append(h)
        return order

    def _per_color_globals(self):
        o = self.options
        try:
            seed = int(getattr(o, "jitter_seed", "12345"))
        except Exception:
            seed = 12345
        return {
            "fillStyle": getattr(o, "fill_style", "parallel"),
            "hatchSpacing": getattr(o, "hatch_spacing", 3.0),
            "hatchAngle": getattr(o, "hatch_angle", 45),
            "crossHatch": bool(getattr(o, "cross_hatch", False)),
            "inset": bool(getattr(o, "inset", True)),
            "insetDist": getattr(o, "inset_dist", 0.5),
            "connectEnds": bool(getattr(o, "connect_ends", True)),
            "hatchScope": getattr(o, "hatch_scope", 3.0),
            "hatchTolerance": getattr(o, "hatch_tolerance", 2.0),
            "waveAmplitude": getattr(o, "wave_amplitude", 1.0),
            "waveWavelength": getattr(o, "wave_wavelength", 0),
            "jitter": bool(getattr(o, "jitter", False)),
            "jitterAmount": getattr(o, "jitter_amount", 0.3),
            "jitterSeed": seed,
            "passes": getattr(o, "passes", 1),
            "passDepth": getattr(o, "pass_depth", 1),
            "cuttingSpeed": getattr(o, "cutting_speed", 750),
            "travelSpeed": getattr(o, "travel_speed", 3000),
            "overcut": getattr(o, "overcut", 0),
            "dwellTime": getattr(o, "dwell_time", 0),
            "dwellAfterPenUp": getattr(o, "dwell_after_pen_up", 0),
            "toolPowerCommand": getattr(o, "tool_power_command", "M3 S255;"),
            "toolOffCommand": getattr(o, "tool_off_command", "M5;"),
        }

    def _save_per_color_template(self):
        opts = self.options
        directory = (getattr(opts, "directory", "") or "").strip()
        if not directory or directory.startswith("--") or not os.path.isdir(directory):
            _err("PlotterHelper: choose an Output Directory first -- the per-color config template is written there. Nothing written.")
            return
        colors = self._collect_pen_colors()
        if not colors:
            _err("PlotterHelper: no explicit pen colors found in the drawing for this mode. Nothing written.")
            return
        g = self._per_color_globals()
        obj = {("#" + h): dict(g) for h in colors}
        path = os.path.join(directory, "per-color-config.template.json")
        with open(path, "w") as f:
            f.write(json.dumps(obj, indent=2))
        self._popup(self._template_popup_text(path, colors))

    def _template_popup_text(self, path, colors):
        lines = ["\u003cb\u003ePlotter-Helper -- per-color config template saved\u003c/b\u003e", "",
                 "Wrote: \u003cb\u003e" + path + "\u003c/b\u003e", "",
                 str(len(colors)) + " color" + ("s" if len(colors) != 1 else "") + " found, each pre-filled with the current global settings:", ""]
        for h in colors:
            lines.append('\u003cspan foreground="#' + h + '"\u003e\u2588\u2588\u2588\u2588\u003c/span\u003e  \u003cb\u003e#' + h + '\u003c/b\u003e')
        lines += ["", "Edit the per-pen overrides in that file, then point 'Per-color config (JSON)' at it and run."]
        return "\n".join(lines)

    def _popup(self, markup_text):
        if shutil.which("zenity") is None:
            _err(re.sub("\u003c[^\u003e]+\u003e", "", markup_text))
            return
        try:
            subprocess.run(["zenity", "--info", "--title=Plotter-Helper", "--text=" + markup_text, "--width=560"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3600)
        except Exception:
            _err(re.sub("\u003c[^\u003e]+\u003e", "", markup_text))

    def effect(self):
        opts = self.options

        # Save a per-color config template: scan the drawing, write a JSON file with
        # every pen color pre-filled with the current global settings. No engine run.
        if bool(getattr(opts, "save_per_color_config", False)):
            self._save_per_color_template()
            return None

        engine = find_engine()

        # 1. Serialize the current Inkscape document to a temp SVG for the engine.
        tmpdir = tempfile.mkdtemp(prefix="plotter-helper_")
        input_svg = os.path.join(tmpdir, "input.svg")
        svg_bytes = etree.tostring(self.document.getroot(), xml_declaration=True, encoding="utf-8")
        with open(input_svg, "wb") as f:
            f.write(svg_bytes)

        mode = _s(getattr(opts, "mode", "hatch"))
        want_preview = bool(getattr(opts, "preview", True))
        show_outlines = bool(getattr(opts, "show_outlines_in_preview", True))
        # The preview SVG is written as a FILE next to the G-code. We do NOT modify
        # the Inkscape document -- replacing it crashes Inkscape when the dialog
        # closes. Path resolved after we know the directory.
        preview_path = None

        # 2. Resolve the output target. Upload-to-Klipper needs no directory;
        #    save-to-disk requires one.
        output_target = _s(getattr(opts, "output_target", "disk"))
        upload = output_target in ("upload", "upload-start")

        directory = getattr(opts, "directory", "") or ""
        dir_valid = (bool(directory.strip())
                     and not directory.strip().startswith("--")
                     and os.path.isdir(directory))

        filename = getattr(opts, "filename", "output.gcode") or "output.gcode"
        if "." not in filename:
            filename += ".gcode"

        if upload:
            # The engine uploads via Moonraker using --moonraker-filename; no disk
            # G-code is written. A preview file is still emitted if a directory was chosen.
            gcode_path = None
            if want_preview and dir_valid:
                preview_path = os.path.join(directory, os.path.splitext(filename)[0] + ".preview.svg")
        else:
            if not dir_valid:
                _err("PlotterHelper: please choose an Output Directory on the 'G-code "
                     "Settings' tab (or switch Output Target to Upload on the Klipper tab). "
                     "Nothing was written.")
                return None
            gcode_path = os.path.join(directory, filename)
            if bool(getattr(opts, "filename_suffix", True)):
                # Split the ORIGINAL name once; re-deriving base from gcode_path
                # inside the loop would keep concatenating suffixes (gcode1 -> gcode12 -> gcode123).
                base, ext = os.path.splitext(gcode_path)
                n = 1
                while os.path.isfile(gcode_path):
                    gcode_path = f"{base}{n}{ext}"
                    n += 1
            if want_preview:
                preview_path = os.path.splitext(gcode_path)[0] + ".preview.svg"

        # 3. Build the engine command line from the .inx options.
        args = ["--mode", mode, "--input", input_svg]
        if gcode_path:
            args += ["--gcode", gcode_path]
        if preview_path:
            args += ["--preview", preview_path]

        opt_map = {
            "unit": "--unit", "travel_speed": "--travel-speed", "cutting_speed": "--cutting-speed",
            "passes": "--passes", "pass_depth": "--pass-depth",
            "tool_power_command": "--tool-power-command", "tool_off_command": "--tool-off-command",
            "dwell_time": "--dwell-time", "dwell_after_pen_up": "--dwell-after-pen-up", "approximation_tolerance": "--approximation-tolerance",
            "machine_origin": "--machine-origin", "invert_y": "--invert-y",
            "bed_width": "--bed-width", "bed_height": "--bed-height",
            "horizontal_offset": "--horizontal-offset", "vertical_offset": "--vertical-offset",
            "scaling_factor": "--scaling-factor",
            "header": "--header", "footer": "--footer",
            "do_z_axis_start": "--do-z-axis-start", "z_axis_start": "--z-axis-start",
            "return_home_end": "--return-home-end",
            "do_tool_off_start": "--do-tool-off-start", "do_tool_off_end": "--do-tool-off-end",
            "include_outlines": "--include-outlines",
            "hatch_spacing": "--hatch-spacing", "hatch_angle": "--hatch-angle",
            "cross_hatch": "--cross-hatch", "hatch_units": "--hatch-units",
            "connect_ends": "--connect-ends", "hatch_scope": "--hatch-scope",
            "inset": "--inset", "inset_dist": "--inset-dist",
            "hatch_tolerance": "--hatch-tolerance", "handle_viewbox": "--handle-viewbox",
            "fill_style": "--fill-style", "wave_amplitude": "--wave-amplitude",
            "wave_wavelength": "--wave-wavelength", "jitter": "--jitter",
            "jitter_amount": "--jitter-amount", "jitter_seed": "--jitter-seed",
            "group_by_color": "--group-by-color", "pause_between_colors": "--pause-between-colors",
            "per_color_config": "--per-color-config",
            "pause_command": "--pause-command",
            "tile_width": "--tile-width", "tile_height": "--tile-height",
            "tile_overlap": "--tile-overlap", "tile_alignment_marks": "--tile-alignment-marks",
            "tile_mark_size": "--tile-mark-size",
            "overcut": "--overcut", "inner_before_outer": "--inner-before-outer",
            "optimize_travel": "--optimize-travel", "optimize_travel_reverse": "--optimize-travel-reverse",
            "connect_outlines": "--connect-outlines", "connect_outlines_dist": "--connect-outlines-dist",
            "double_pass_short": "--double-pass-short", "double_pass_length": "--double-pass-length",
            "output_target": "--output-target", "moonraker_url": "--moonraker-url",
            "moonraker_api_key": "--moonraker-api-key", "moonraker_filename": "--moonraker-filename",
            "klipper_thumbnail": "--klipper-thumbnail",
        }
        for opt_name, flag in opt_map.items():
            if hasattr(opts, opt_name):
                args += [flag, _s(getattr(opts, opt_name))]
        if want_preview and show_outlines:
            args += ["--show-outlines-in-preview", "true"]
        args += ["--progress", "true"]

        # 4. Run the engine, showing a zenity progress dialog if available.
        try:
            rc, stderr_text = _run_engine(engine, args)
        except Exception as ex:
            _err(f"PlotterHelper: failed to run engine: {ex}")
            return None

        if rc != 0:
            _err(stderr_text or f"PlotterHelper: engine exited with code {rc}")
            return None
        if stderr_text:
            sys.stderr.write(stderr_text)

        # 5. We deliberately do NOT modify self.document: replacing the Inkscape
        #    document with a stripped preview tree crashes Inkscape when the
        #    extension dialog closes. The G-code (and optional preview SVG file)
        #    are on disk; the drawing in the canvas is left untouched.
        #    Silent on success -- only errors are reported (above).
        return None


if __name__ == "__main__":
    PlotterHelper().run()
