namespace PlotterHelper.Compiler;

/// <summary>
/// Abstracts the numerical-control commands a machine expects. Implement this to
/// target a non-standard controller without touching the compiler. Mirrors
/// svg_to_gcode's Interface.
/// </summary>
public abstract class Interface
{
    /// <summary>Current tool position in work units (null until the first move sets it).</summary>
    public Geometry.Vector? Position { get; protected set; }

    public abstract string SetMovementSpeed(double speed);
    public abstract string LinearMove(double? x, double? y, double? z = null);
    public abstract string ToolOff();
    public abstract string SetToolPower(double power);
    public abstract string SetAbsoluteCoordinates();
    public abstract string SetRelativeCoordinates();
    public abstract string Dwell(double milliseconds);
    public abstract string SetOriginAtPosition();
    public abstract string SetUnit(string? unit);
    public abstract string HomeAxes();
}
