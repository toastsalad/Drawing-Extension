using PlotterHelper.Compiler;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Cli;

namespace PlotterHelper.Cli.Output;

/// <summary>
/// Final G-code output: disk write vs Klipper/Moonraker upload (optionally
/// upload-and-start), with an optional PrusaSlicer-style PNG thumbnail embedded
/// in uploads so Mainsail/Fluidd/KlipperScreen show a preview. Disk output is
/// untouched by the thumbnail path.
/// </summary>
internal static class GcodeOutput
{
    /// <summary>
    /// Write the compiled G-code to disk (when <c>--gcode</c> is set) or upload it to a
    /// Moonraker/Klipper host (when <c>--output-target</c> is upload/upload-start).
    /// G-code is optional: the caller may run the engine for a preview only; a disk
    /// target with no path is skipped silently.
    /// </summary>
    internal static void Output(Options opts, GcodeCompiler compiler)
    {
        bool upload = opts.OutputTarget != "disk" && !string.IsNullOrEmpty(opts.MoonrakerUrl);
        if (!upload)
        {
            if (opts.Gcode != null) compiler.CompileToFile(opts.Gcode, opts.Passes);
            return;
        }
        var gcode = compiler.Compile(opts.Passes);
        gcode = EmbedThumbnail(opts, gcode);
        var name = ResolveFilename(opts);
        Klipper.Upload(gcode, name, opts.MoonrakerUrl, opts.MoonrakerApiKey,
            start: opts.OutputTarget == "upload-start", opts);
    }

    /// <summary>Write an already-assembled G-code string (the per-color path builds
    /// its own header + per-pen bodies + footer rather than using a single compiler).</summary>
    internal static void OutputString(Options opts, string gcode)
    {
        bool upload = opts.OutputTarget != "disk" && !string.IsNullOrEmpty(opts.MoonrakerUrl);
        if (!upload)
        {
            if (opts.Gcode != null) File.WriteAllText(opts.Gcode, gcode);
            return;
        }
        gcode = EmbedThumbnail(opts, gcode);
        var name = ResolveFilename(opts);
        Klipper.Upload(gcode, name, opts.MoonrakerUrl, opts.MoonrakerApiKey,
            start: opts.OutputTarget == "upload-start", opts);
    }

    /// <summary>
    /// Render the Klipper upload thumbnail (a PNG of the outlines + hatch fill) when
    /// the output target is an upload and thumbnails are enabled. Stored on
    /// <c>opts.ThumbnailPng</c> and embedded by <see cref="Output"/>. Failures are
    /// non-fatal (the upload proceeds without a thumbnail).
    /// </summary>
    internal static void PrepareThumbnail(Options opts, SvgDocument doc,
        List<HatchGroup> groups, bool showOutlines)
    {
        opts.ThumbnailPng = null;
        if (!opts.KlipperThumbnail || opts.OutputTarget == "disk") return;
        try { opts.ThumbnailPng = ThumbnailRenderer.Render(doc, groups, showOutlines, opts.FlatteningTolerance); }
        catch (Exception ex) { Progress.Report(opts, 93, $"thumbnail skipped: {ex.Message}"); }
    }

    private static string EmbedThumbnail(Options opts, string gcode)
    {
        if (opts.KlipperThumbnail && opts.ThumbnailPng != null)
        {
            Progress.Report(opts, 93, "Embedding thumbnail…");
            gcode = ThumbnailRenderer.BuildBlock(opts.ThumbnailPng) + gcode;
        }
        return gcode;
    }

    private static string ResolveFilename(Options opts) =>
        !string.IsNullOrWhiteSpace(opts.MoonrakerFilename)
            ? opts.MoonrakerFilename
            : (opts.Gcode != null ? Path.GetFileName(opts.Gcode) : "plotter-helper.gcode");
}