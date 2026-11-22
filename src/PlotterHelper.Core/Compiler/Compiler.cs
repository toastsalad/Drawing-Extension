using PlotterHelper.Geometry;

namespace PlotterHelper.Compiler;

/// <summary>
/// Compiles geometric curves into numerical-control code via an
/// <see cref="Interface"/>. Curves are flattened to line segments and emitted as
/// moves; the tool is lifted (tool off) and repositioned between disjoint chains,
/// exactly like svg_to_gcode's Compiler. Supports multiple passes with a Z step.
/// </summary>
public sealed class Compiler
{
    private readonly Interface _interface;
    public double MovementSpeed { get; }
    public double CuttingSpeed { get; }
    public double PassDepth { get; }
    public double DwellTime { get; }
    public double DwellAfterPenUp { get; }
    public string? Unit { get; }

    /// <summary>Maximum deviation, in user units, used when flattening a curve into
    /// line segments. Passed in rather than read from a static so a library caller
    /// controls it.</summary>
    private readonly double _approximationTolerance;

    private readonly List<string> _header;
    private readonly List<string> _footer;
    private readonly List<string> _body = new();

    public Compiler(Interface iface, double movementSpeed, double cuttingSpeed, double passDepth,
        double dwellTime = 0, double dwellAfterPenUp = 0, string? unit = null, List<string>? customHeader = null, List<string>? customFooter = null,
        double approximationTolerance = Tolerances.Approximation)
    {
        _interface = iface;
        MovementSpeed = Math.Abs(movementSpeed);
        CuttingSpeed = Math.Abs(cuttingSpeed);
        PassDepth = Math.Abs(passDepth);
        DwellTime = dwellTime;
        DwellAfterPenUp = dwellAfterPenUp;
        Unit = unit;
        _approximationTolerance = approximationTolerance;

        var header = new List<string>
        {
            iface.SetAbsoluteCoordinates(),
            iface.SetMovementSpeed(movementSpeed),
        };
        // svg_to_gcode defaults both header and footer to [tool_off()] when not supplied.
        var customHdr = customHeader ?? new List<string> { iface.ToolOff() };
        header.AddRange(customHdr);
        _header = header;

        _footer = customFooter ?? new List<string> { iface.ToolOff() };
    }

    /// <summary>Append a single flattened polyline as one continuous cut.</summary>
    public void AppendLineChain(LineSegmentChain chain)
    {
        if (chain.Count == 0) return;

        var code = new List<string>();
        var start = chain[0];

        // Lift the tool, travel to the start, set cutting speed, drop the tool.
        var pos = _interface.Position;
        bool sameStart = pos.HasValue && Vector.DistanceSquared(pos.Value, start) <= Tolerances.Operation * Tolerances.Operation;
        if (!sameStart)
        {
            code.Add(_interface.ToolOff());
            // Dwell AFTER the tool is lifted so the pen fully retracts before
            // travelling to the next stroke start.
            if (DwellAfterPenUp > 0)
                code.Add(_interface.Dwell(DwellAfterPenUp));
            code.Add(_interface.SetMovementSpeed(MovementSpeed));
            code.Add(_interface.LinearMove(start.X, start.Y));
            code.Add(_interface.SetMovementSpeed(CuttingSpeed));
            code.Add(_interface.SetToolPower(1));
            // Dwell AFTER the tool is lowered at the new start, so the pen/blade
            // settles before the first cut move (svg_to_gcode placed it before
            // the lift, dwelling at the previous stroke's end -- useless for a
            // pen. line.svg parity uses dwell_time=0).
            if (DwellTime > 0)
                code.Add(_interface.Dwell(DwellTime));
        }

        for (int pointIndex = 1; pointIndex < chain.Count; pointIndex++)
        {
            var point = chain[pointIndex];
            code.Add(_interface.LinearMove(point.X, point.Y));
        }

        _body.AddRange(code);
    }

    /// <summary>Flatten and append a batch of curves (the common case).</summary>
    public void AppendCurves(IEnumerable<Curve> curves)
    {
        foreach (var curve in curves)
        {
            var chain = new LineSegmentChain();
            chain.AppendFlattened(curve, _approximationTolerance);
            AppendLineChain(chain);
        }
    }

    /// <summary>Append a raw G-code line to the body (e.g. a comment or pause).</summary>
    public void AppendRaw(string line)
    {
        if (!string.IsNullOrEmpty(line)) _body.Add(line);
    }

    /// <summary>
    /// Lift the tool and emit a pause command between color groups, so the operator
    /// can swap pens. The tool is lifted first so it isn't dragging during the pause.
    /// </summary>
    public void AppendPause(string pauseCommand)
    {
        _body.Add(_interface.ToolOff());
        if (!string.IsNullOrEmpty(pauseCommand)) _body.Add(pauseCommand);
    }

    /// <summary>Assemble header + (body + Z step) × passes + footer into one string.</summary>
    public string Compile(int passes = 1)
    {
        var lines = new List<string>();
        foreach (var headerLine in _header) if (headerLine.Length > 0) lines.Add(headerLine);
        var unitCmd = _interface.SetUnit(Unit);
        if (unitCmd.Length > 0) lines.Add(unitCmd);

        for (int passIdx = 0; passIdx < passes; passIdx++)
        {
            foreach (var bodyLine in _body) if (bodyLine.Length > 0) lines.Add(bodyLine);
            if (passIdx < passes - 1)
            {
                lines.Add(_interface.ToolOff());
                if (PassDepth > 0)
                {
                    lines.Add(_interface.SetRelativeCoordinates());
                    var zMove = _interface.LinearMove(null, null, -PassDepth);
                    if (zMove.Length > 0) lines.Add(zMove);
                    lines.Add(_interface.SetAbsoluteCoordinates());
                }
            }
        }

        foreach (var footerLine in _footer) if (footerLine.Length > 0) lines.Add(footerLine);
        return string.Join('\n', lines);
    }

    /// <summary>Header lines (custom header + unit command), for per-section assembly.</summary>
    public IEnumerable<string> HeaderLines()
    {
        foreach (var headerLine in _header) if (headerLine.Length > 0) yield return headerLine;
        var unitCmd = _interface.SetUnit(Unit);
        if (unitCmd.Length > 0) yield return unitCmd;
    }

    /// <summary>Footer lines, for per-section assembly.</summary>
    public IEnumerable<string> FooterLines()
    {
        foreach (var footerLine in _footer) if (footerLine.Length > 0) yield return footerLine;
    }

    /// <summary>Body repeated `passes` times with the Z step between passes (no header/footer/unit). For per-color assembly where each pen gets its own pass count.</summary>
    public string CompileBody(int passes = 1)
    {
        var lines = new List<string>();
        for (int passIdx = 0; passIdx < passes; passIdx++)
        {
            foreach (var bodyLine in _body) if (bodyLine.Length > 0) lines.Add(bodyLine);
            if (passIdx < passes - 1)
            {
                lines.Add(_interface.ToolOff());
                if (PassDepth > 0)
                {
                    lines.Add(_interface.SetRelativeCoordinates());
                    var zMove = _interface.LinearMove(null, null, -PassDepth);
                    if (zMove.Length > 0) lines.Add(zMove);
                    lines.Add(_interface.SetAbsoluteCoordinates());
                }
            }
        }
        return string.Join('\n', lines);
    }

    public void CompileToFile(string path, int passes = 1) =>
        File.WriteAllText(path, Compile(passes));
}

/// <summary>
/// A G-code interface whose tool on/off commands are user-supplied strings,
/// matching the "Tool Power Command" / "Tool Off Command" options.
/// Newlines (\n) in the templates are expanded.
/// </summary>
public sealed class CustomGcodeInterface : GcodeInterface
{
    private readonly string _off;
    private readonly string _power;
    public CustomGcodeInterface(string offCommand, string powerCommand)
    {
        _off = Expand(offCommand);
        _power = Expand(powerCommand);
    }
    public override string ToolOff() => _off;
    public override string SetToolPower(double power) => _power;

    private static string Expand(string text) => text.Replace("\\n", "\n");
}