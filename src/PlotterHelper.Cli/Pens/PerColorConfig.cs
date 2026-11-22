using System.Text.Json;
using System.Text.Json.Serialization;
using PlotterHelper.Compiler;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Cli;
using PlotterHelper.Cli.Features;
using PlotterHelper.Cli.Output;

namespace PlotterHelper.Cli.Pens;

/// <summary>
/// Overridable per-pen settings. Every field is nullable: only non-null fields
/// override the global defaults for that pen/color; the rest fall through to the
/// global options. Loaded from a JSON config file keyed by 6-digit hex color
/// (with or without '#', case-insensitive) — see <see cref="PerColorConfig.Load"/>.
/// </summary>
internal sealed class PerColorOpts
{
    public FillStyle? FillStyle { get; set; }
    public double? HatchSpacing { get; set; }
    public double? HatchAngle { get; set; }
    public bool? CrossHatch { get; set; }
    public bool? Inset { get; set; }
    public double? InsetDist { get; set; }
    public bool? ConnectEnds { get; set; }
    public double? HatchScope { get; set; }
    public double? HatchTolerance { get; set; }
    public double? WaveAmplitude { get; set; }
    public double? WaveWavelength { get; set; }
    public bool? Jitter { get; set; }
    public double? JitterAmount { get; set; }
    public int? JitterSeed { get; set; }
    public int? Passes { get; set; }
    public double? PassDepth { get; set; }
    public double? CuttingSpeed { get; set; }
    public double? TravelSpeed { get; set; }
    public double? Overcut { get; set; }
    public double? DwellTime { get; set; }
    public double? DwellAfterPenUp { get; set; }
    public string? ToolPowerCommand { get; set; }
    public string? ToolOffCommand { get; set; }
}

/// <summary>Parses a per-color JSON config file into a hex → overrides map.</summary>
internal static class PerColorConfig
{
    private static readonly JsonSerializerOptions SJsonOpts = CreateJsonOpts();

    private static JsonSerializerOptions CreateJsonOpts()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new FillStyleConverter());
        return options;
    }
    /// <summary>
    /// Load a JSON file of the form:
    /// <code>
    /// {
    ///   "FF0000": { "fillStyle": "concentric", "hatchSpacing": 2, "passes": 2 },
    ///   "0000FF": { "fillStyle": "parallel", "hatchSpacing": 5, "cuttingSpeed": 500 }
    /// }
    /// </code>
    /// Keys are 6-digit hex (with or without '#', case-insensitive). Only the
    /// fields present override the global defaults. Colors not listed are not
    /// hatched (when hatching is active).
    /// </summary>
    public static Dictionary<string, PerColorOpts> Load(string path)
    {
        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<Dictionary<string, PerColorOpts>>(json, SJsonOpts) ?? new();

        var result = new Dictionary<string, PerColorOpts>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            if (entry.Value == null) continue;
            var key = entry.Key.Trim().TrimStart('#').ToUpperInvariant();
            if (key.Length == 6)
                result[key] = entry.Value;
        }
        return result;
    }
}

/// <summary>Case-insensitive JSON binding for the FillStyle enum ("concentric" -> Concentric).</summary>
internal sealed class FillStyleConverter : JsonConverter<FillStyle>
{
    public override FillStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return string.IsNullOrEmpty(text) ? FillStyle.Parallel : Enum.Parse<FillStyle>(text, ignoreCase: true);
    }
    public override void Write(Utf8JsonWriter writer, FillStyle value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>
/// Per-color / per-pen compilation, used when --per-color-config is provided.
/// Each color (matched by normalized fill/stroke hex) compiles with its own
/// compiler, so passes, speeds, tool power/off, dwell, overcut, double-pass and
/// every hatch option are per-pen. Colors not in the config are not hatched
/// (outlines only). Output is assembled as: global header + per-color bodies
/// (each repeated for that pen's passes) + pen-swap pauses + global footer.
/// </summary>
internal static class PerColorCompiler
{
    internal static int Run(Options opts, SvgDocument doc, bool hatchMode)
    {
        bool includeOutlines = !hatchMode || opts.IncludeOutlines;
        double tolerance = opts.FlatteningTolerance;

        var hatchable = hatchMode
            ? new HashSet<SvgPath>(HatchEngine.HatchableSources(doc.Paths, opts.AutoClose), ReferenceEqualityComparer.Instance)
            : new HashSet<SvgPath>(ReferenceEqualityComparer.Instance);

        // Bucket by normalized hex (string.Empty for default/no-color), first-appearance order.
        var order = new List<string>();
        var outlineBuckets = new Dictionary<string, List<SvgPath>>();
        var hatchSourceBuckets = new Dictionary<string, List<SvgPath>>();
        bool Seen(string colorKey) => outlineBuckets.ContainsKey(colorKey) || hatchSourceBuckets.ContainsKey(colorKey);
        static string Key(string? raw) => SvgColor.ToHex(ColorSort.NormalizeColor(raw)) ?? string.Empty;

        foreach (var path in doc.Paths)
        {
            if (includeOutlines && path.Curves.Count > 0)
            {
                var colorKey = Key(path.Stroke);
                if (!Seen(colorKey)) order.Add(colorKey);
                if (!outlineBuckets.TryGetValue(colorKey, out var list)) { list = new List<SvgPath>(); outlineBuckets[colorKey] = list; }
                list.Add(path);
            }
            if (hatchable.Contains(path))
            {
                var colorKey = Key(path.Fill);
                if (!Seen(colorKey)) order.Add(colorKey);
                if (!hatchSourceBuckets.TryGetValue(colorKey, out var list)) { list = new List<SvgPath>(); hatchSourceBuckets[colorKey] = list; }
                list.Add(path);
            }
        }

        int totalCurves = outlineBuckets.Values.Sum(bucket => bucket.Sum(path => path.Curves.Count))
                          + hatchSourceBuckets.Values.Sum(bucket => bucket.Count * 8); // rough hatch estimate for the progress bar
        Vector cur = Vector.Zero;
        int done = 0;
        var sections = new List<(GcodeCompiler comp, int passes, Options eff, string hex)>();
        var allColorGroups = new List<HatchGroup>();

        foreach (var colorKey in order)
        {
            PerColorOpts? overrides = null;
            bool mapped = colorKey.Length == 6 && opts.PerColor != null && opts.PerColor.TryGetValue(colorKey, out overrides);
            var eff = ApplyOverrides(opts, overrides);
            outlineBuckets.TryGetValue(colorKey, out var outlines);
            outlines ??= new List<SvgPath>();

            if (eff.DoublePassShort)
                foreach (var path in outlines) DoublePass.ApplyToPath(path, Units.ToDocUnits(eff.DoublePassLength, "mm", eff.Unit), tolerance);
            if (eff.Overcut > 0)
                foreach (var path in outlines) Overcut.ApplyToPath(path, eff.Overcut, tolerance);

            IList<HatchGroup> colorGroups = Array.Empty<HatchGroup>();
            if (hatchMode && mapped)
            {
                hatchSourceBuckets.TryGetValue(colorKey, out var srcs);
                if (srcs != null && srcs.Count > 0)
                {
                    colorGroups = HatchEngine.Generate(srcs, EngineBuilder.BuildHatchOptions(eff),
                        progress: (done, total) => Progress.Report(opts, 8 + 77 * done / Math.Max(1, total), $"Hatching {PenAnnounce.ColorName(colorKey)} {done}/{total}"));
                    allColorGroups.AddRange(colorGroups);
                }
            }

            var comp = EngineBuilder.BuildCompiler(eff);
            cur = StrokeEmitter.EmitColorGroup(eff, comp, outlines, colorGroups, cur, ref done, totalCurves, 85, 99, PenAnnounce.ColorName(colorKey), tolerance);
            sections.Add((comp, eff.Passes, eff, colorKey));
        }

        if (opts.Preview != null)
            File.WriteAllText(opts.Preview, PreviewSvg.Write(doc, allColorGroups, showOutlines: includeOutlines, tolerance));
        GcodeOutput.PrepareThumbnail(opts, doc, allColorGroups, showOutlines: includeOutlines);

        // Assemble: global header + per-color bodies (× per-pen passes) + pauses + footer.
        var globalComp = EngineBuilder.BuildCompiler(opts);
        var lines = new List<string>();
        foreach (var headerLine in globalComp.HeaderLines()) if (headerLine.Length > 0) lines.Add(headerLine);
        if (sections.Count > 0)
            lines.AddRange(PenAnnounce.FirstPenLines(opts, PenAnnounce.ColorName(sections[0].hex), sections.Count));
        for (int section = 0; section < sections.Count; section++)
        {
            if (section > 0 && opts.PauseBetweenColors)
                lines.AddRange(PenAnnounce.PenSwapLines(sections[section - 1].eff, PenAnnounce.ColorName(sections[section].hex), section + 1, sections.Count));
            foreach (var bodyLine in sections[section].comp.CompileBody(sections[section].passes).Split('\n'))
                if (bodyLine.Length > 0) lines.Add(bodyLine);
        }
        foreach (var footerLine in globalComp.FooterLines()) if (footerLine.Length > 0) lines.Add(footerLine);

        GcodeOutput.OutputString(opts, string.Join('\n', lines));
        Progress.Report(opts, 100, "Done");
        return 0;
    }

    internal static Options ApplyOverrides(Options opts, PerColorOpts? overrides)
    {
        var effective = opts.Clone();
        if (overrides == null) return effective;
        if (overrides.FillStyle.HasValue) effective.FillStyle = overrides.FillStyle.Value;
        if (overrides.HatchSpacing.HasValue) effective.HatchSpacing = overrides.HatchSpacing.Value;
        if (overrides.HatchAngle.HasValue) effective.HatchAngle = overrides.HatchAngle.Value;
        if (overrides.CrossHatch.HasValue) effective.CrossHatch = overrides.CrossHatch.Value;
        if (overrides.Inset.HasValue) effective.Inset = overrides.Inset.Value;
        if (overrides.InsetDist.HasValue) effective.InsetDist = overrides.InsetDist.Value;
        if (overrides.ConnectEnds.HasValue) effective.ConnectEnds = overrides.ConnectEnds.Value;
        if (overrides.HatchScope.HasValue) effective.HatchScope = overrides.HatchScope.Value;
        if (overrides.HatchTolerance.HasValue) effective.HatchTolerance = overrides.HatchTolerance.Value;
        if (overrides.WaveAmplitude.HasValue) effective.WaveAmplitude = overrides.WaveAmplitude.Value;
        if (overrides.WaveWavelength.HasValue) effective.WaveWavelength = overrides.WaveWavelength.Value;
        if (overrides.Jitter.HasValue) effective.Jitter = overrides.Jitter.Value;
        if (overrides.JitterAmount.HasValue) effective.JitterAmount = overrides.JitterAmount.Value;
        if (overrides.JitterSeed.HasValue) effective.JitterSeed = overrides.JitterSeed.Value;
        if (overrides.Passes.HasValue) effective.Passes = overrides.Passes.Value;
        if (overrides.PassDepth.HasValue) effective.PassDepth = overrides.PassDepth.Value;
        if (overrides.CuttingSpeed.HasValue) effective.CuttingSpeed = overrides.CuttingSpeed.Value;
        if (overrides.TravelSpeed.HasValue) effective.TravelSpeed = overrides.TravelSpeed.Value;
        if (overrides.Overcut.HasValue) effective.Overcut = overrides.Overcut.Value;
        if (overrides.DwellTime.HasValue) effective.DwellTime = overrides.DwellTime.Value;
        if (overrides.DwellAfterPenUp.HasValue) effective.DwellAfterPenUp = overrides.DwellAfterPenUp.Value;
        if (!string.IsNullOrEmpty(overrides.ToolPowerCommand)) effective.ToolPower = overrides.ToolPowerCommand;
        if (!string.IsNullOrEmpty(overrides.ToolOffCommand)) effective.ToolOff = overrides.ToolOffCommand;
        return effective;
    }
}
