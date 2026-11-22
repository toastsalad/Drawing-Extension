using PlotterHelper.Geometry;

namespace PlotterHelper.Compiler;

/// <summary>
/// Standard G-code interface for CNC/plotter controllers (Marlin/Grbl-flavored):
/// G0/G1 moves, M3/M5 spindle, G90/G91 positioning, G21/G20 units. Output format
/// matches svg_to_gcode's Gcode interface so generated files are drop-in compatible.
/// </summary>
public class GcodeInterface : Interface
{
    private double? _nextSpeed;
    private double? _currentSpeed;
    private readonly int _precision;

    public GcodeInterface()
    {
        // Round outputs to the operational tolerance's significant figures (6).
        _precision = Math.Max(0, (int)Math.Round(-Math.Log10(Tolerances.Operation)));
    }

    public override string SetMovementSpeed(double speed)
    {
        _nextSpeed = speed;
        return "";
    }

    public override string LinearMove(double? x, double? y, double? z = null)
    {
        if (_nextSpeed == null)
            throw new InvalidOperationException("Undefined movement speed. Call SetMovementSpeed before movement commands.");
        if (x == null && y == null && z == null)
            return "";

        string command = "G1";
        if (_currentSpeed != _nextSpeed)
        {
            _currentSpeed = _nextSpeed;
            command += $" F{_currentSpeed.Value:0}";
        }
        if (x.HasValue) command += $" X{Format(x.Value)}";
        if (y.HasValue) command += $" Y{Format(y.Value)}";
        if (z.HasValue) command += $" Z{Format(z.Value)}";

        if (Position.HasValue || (x.HasValue && y.HasValue))
        {
            double px = x ?? Position!.Value.X;
            double py = y ?? Position!.Value.Y;
            Position = new Vector(px, py);
        }
        return command + ";";
    }

    public override string ToolOff() => "M5;";

    public override string SetToolPower(double power)
    {
        if (power < 0 || power > 1)
            throw new ArgumentOutOfRangeException(nameof(power), "Tool power must be in [0,1].");
        return $"M3 S{Formulas.PowerToSpindle(power)};";
    }

    public override string SetAbsoluteCoordinates() => "G90;";
    public override string SetRelativeCoordinates() => "G91;";
    public override string Dwell(double milliseconds) => $"G4 P{milliseconds}";
    public override string SetOriginAtPosition() { Position = Vector.Zero; return "G92 X0 Y0 Z0;"; }
    public override string SetUnit(string? unit) => unit switch
    {
        "mm" => "G21;",
        "in" => "G20;",
        _ => "",
    };
    public override string HomeAxes() => "G28;";

    private string Format(double value) => value.ToString($"F{_precision}", System.Globalization.CultureInfo.InvariantCulture);
}
