using System.Globalization;
using PlotterHelper.Geometry;

namespace PlotterHelper.Svg;

/// <summary>
/// Parses an SVG path's "d" attribute into a list of <see cref="Curve"/>s,
/// faithfully implementing the SVG path data grammar (M/L/H/V/Z/C/S/Q/T/A,
/// relative and absolute, implicit repeated commands, flag arguments that need
/// no delimiter). Port of svg_to_gcode/svg_parser/_path.py.
/// </summary>
public sealed class PathParser
{
    private readonly string _d;
    private readonly Affine _transform;

    private Vector _current = Vector.Zero;
    private Vector _initial = Vector.Zero;
    private Vector? _lastControl;

    public List<Curve> Curves { get; } = new();

    public PathParser(string pathData, double canvasHeight, bool transformOrigin, Affine transform)
    {
        _d = pathData;
        // Compose the document origin flip (SVG y-down -> cartesian y-up) with the
        // supplied element transform. This matches svg_to_gcode's Transformation.
        Affine origin = transformOrigin
            ? Affine.Translation(0, canvasHeight).Compose(Affine.Scale(1, -1))
            : Affine.Identity;
        _transform = origin.Compose(transform);
    }

    private Vector ToDoc(Vector point) => _transform.Apply(point);

    public void Parse()
    {
        var tokens = Tokenize(_d);
        int tokenIndex = 0;
        char cmd = '\0';
        while (tokenIndex < tokens.Count)
        {
            var tok = tokens[tokenIndex];
            if (tok.IsCommand)
            {
                cmd = tok.Command;
                tokenIndex++;
                // Commands with no coordinate arguments (Z/z).
                if (cmd is 'Z' or 'z')
                {
                    HandleClose();
                    continue;
                }
            }

            int arity = Arity(cmd);
            if (tokenIndex + arity > tokens.Count)
                break; // truncated input; stop gracefully

            switch (cmd)
            {
                case 'M': HandleMoveAbs(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; cmd = 'L'; break;
                case 'm': HandleMoveRel(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; cmd = 'l'; break;
                case 'L': HandleLineAbs(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; break;
                case 'l': HandleLineRel(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; break;
                case 'H': HandleHAbs(tokens[tokenIndex]); tokenIndex += 1; break;
                case 'h': HandleHRel(tokens[tokenIndex]); tokenIndex += 1; break;
                case 'V': HandleVAbs(tokens[tokenIndex]); tokenIndex += 1; break;
                case 'v': HandleVRel(tokens[tokenIndex]); tokenIndex += 1; break;
                case 'C': HandleCubicAbs(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3], tokens[tokenIndex + 4], tokens[tokenIndex + 5]); tokenIndex += 6; break;
                case 'c': HandleCubicRel(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3], tokens[tokenIndex + 4], tokens[tokenIndex + 5]); tokenIndex += 6; break;
                case 'S': HandleSmoothCubicAbs(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3]); tokenIndex += 4; break;
                case 's': HandleSmoothCubicRel(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3]); tokenIndex += 4; break;
                case 'Q': HandleQuadAbs(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3]); tokenIndex += 4; break;
                case 'q': HandleQuadRel(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3]); tokenIndex += 4; break;
                case 'T': HandleSmoothQuadAbs(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; break;
                case 't': HandleSmoothQuadRel(tokens[tokenIndex], tokens[tokenIndex + 1]); tokenIndex += 2; break;
                case 'A': HandleArcAbs(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3], tokens[tokenIndex + 4], tokens[tokenIndex + 5], tokens[tokenIndex + 6]); tokenIndex += 7; break;
                case 'a': HandleArcRel(tokens[tokenIndex], tokens[tokenIndex + 1], tokens[tokenIndex + 2], tokens[tokenIndex + 3], tokens[tokenIndex + 4], tokens[tokenIndex + 5], tokens[tokenIndex + 6]); tokenIndex += 7; break;
                default: tokenIndex++; break; // unknown: skip a token to make progress
            }
        }
    }

    private static int Arity(char command) => command switch
    {
        'M' or 'm' or 'L' or 'l' or 'T' or 't' => 2,
        'H' or 'h' or 'V' or 'v' => 1,
        'C' or 'c' => 6,
        'S' or 's' or 'Q' or 'q' => 4,
        'A' or 'a' => 7,
        _ => 0,
    };

    private void HandleMoveAbs(Token x, Token y)
    {
        _initial = new(x.Value, y.Value);
        _current = _initial;
        _lastControl = null;
    }

    private void HandleMoveRel(Token x, Token y)
    {
        _initial = _current + new Vector(x.Value, y.Value);
        _current = _initial;
        _lastControl = null;
    }

    private void HandleLineAbs(Token x, Token y) => AddLine(_current, new(x.Value, y.Value));

    private void HandleLineRel(Token x, Token y) => AddLine(_current, _current + new Vector(x.Value, y.Value));

    private void HandleHAbs(Token x) => AddLine(_current, new(x.Value, _current.Y));

    private void HandleHRel(Token x) => AddLine(_current, new(_current.X + x.Value, _current.Y));

    private void HandleVAbs(Token y) => AddLine(_current, new(_current.X, y.Value));

    private void HandleVRel(Token y) => AddLine(_current, new(_current.X, _current.Y + y.Value));

    private void HandleClose()
    {
        if (_current != _initial)
            AddLine(_current, _initial);
        _current = _initial;
        _lastControl = null;
    }

    private void HandleCubicAbs(Token x1, Token y1, Token x2, Token y2, Token x, Token y)
        => AddCubic(new(x1.Value, y1.Value), new(x2.Value, y2.Value), new(x.Value, y.Value));

    private void HandleCubicRel(Token x1, Token y1, Token x2, Token y2, Token x, Token y)
    {
        var control1 = _current + new Vector(x1.Value, y1.Value);
        var control2 = _current + new Vector(x2.Value, y2.Value);
        var end = _current + new Vector(x.Value, y.Value);
        AddCubic(control1, control2, end);
    }

    private void HandleSmoothCubicAbs(Token x2, Token y2, Token x, Token y)
    {
        var control2 = new Vector(x2.Value, y2.Value);
        var end = new Vector(x.Value, y.Value);
        AddSmoothCubic(control2, end);
    }

    private void HandleSmoothCubicRel(Token x2, Token y2, Token x, Token y)
    {
        var control2 = _current + new Vector(x2.Value, y2.Value);
        var end = _current + new Vector(x.Value, y.Value);
        AddSmoothCubic(control2, end);
    }

    private void HandleQuadAbs(Token x1, Token y1, Token x, Token y)
        => AddQuad(new(x1.Value, y1.Value), new(x.Value, y.Value));

    private void HandleQuadRel(Token x1, Token y1, Token x, Token y)
    {
        var control1 = _current + new Vector(x1.Value, y1.Value);
        var end = _current + new Vector(x.Value, y.Value);
        AddQuad(control1, end);
    }

    private void HandleSmoothQuadAbs(Token x, Token y)
        => AddSmoothQuad(new(x.Value, y.Value));

    private void HandleSmoothQuadRel(Token x, Token y)
        => AddSmoothQuad(_current + new Vector(x.Value, y.Value));

    private void HandleArcAbs(Token rx, Token ry, Token rotation, Token largeArc, Token sweep, Token x, Token y)
        => AddArc(new Vector(rx.Value, ry.Value), rotation.Value, (int)largeArc.Value, (int)sweep.Value, new Vector(x.Value, y.Value));

    private void HandleArcRel(Token rx, Token ry, Token rotation, Token largeArc, Token sweep, Token x, Token y)
        => AddArc(new Vector(rx.Value, ry.Value), rotation.Value, (int)largeArc.Value, (int)sweep.Value, _current + new Vector(x.Value, y.Value));

    private void AddLine(Vector start, Vector end)
    {
        if (start != end)
            Curves.Add(new Line(ToDoc(start), ToDoc(end)));
        _current = end;
        _lastControl = null;
    }

    private void AddCubic(Vector control1, Vector control2, Vector end)
    {
        var start = _current;
        var cubic = new CubicBezier(ToDoc(start), ToDoc(control1), ToDoc(control2), ToDoc(end));
        Curves.Add(cubic);
        _lastControl = control2;
        _current = end;
    }

    private void AddSmoothCubic(Vector control2, Vector end)
    {
        Vector control1 = _lastControl.HasValue
            ? 2 * _current - _lastControl.Value
            : _current;
        AddCubic(control1, control2, end);
    }

    private void AddQuad(Vector control1, Vector end)
    {
        var start = _current;
        var quad = new QuadraticBezier(ToDoc(start), ToDoc(control1), ToDoc(end));
        Curves.Add(quad);
        _lastControl = control1;
        _current = end;
    }

    private void AddSmoothQuad(Vector end)
    {
        Vector control1 = _lastControl.HasValue
            ? 2 * _current - _lastControl.Value
            : _current;
        AddQuad(control1, end);
    }

    private void AddArc(Vector radii, double rotationDeg, int largeArc, int sweep, Vector end)
    {
        var start = _current;
        // The arc applies the document transform lazily (matching svg_to_gcode),
        // so we pass the *untransformed* start/end and the full transform.
        var arc = EllipticalArc.FromEndpoint(start, end, radii.X, radii.Y, rotationDeg, largeArc, sweep, _transform);
        if (arc != null)
            Curves.Add(arc);
        _current = end;
        _lastControl = null;
    }

    /// <summary>
    /// Tokenize an SVG path data string into commands and numbers. Numbers may be
    /// separated by whitespace, commas, or nothing at all (e.g. "1.5.5" -> 1.5, 0.5
    /// and "1e-3-4" -> 1e-3, -4). Arc flags are single digits with no delimiter.
    /// </summary>
    private static List<Token> Tokenize(string pathData)
    {
        var tokens = new List<Token>();
        int pos = 0, len = pathData.Length;
        while (pos < len)
        {
            char curChar = pathData[pos];
            if (curChar is ' ' or '\t' or '\n' or '\r' or ',') { pos++; continue; }

            if (IsPathCommand(curChar))
            {
                tokens.Add(new(curChar));
                pos++;
                continue;
            }

            // Parse a number. Handle leading sign, decimal, exponent.
            int start = pos;
            if (curChar is '+' or '-') pos++;
            bool sawDot = false, sawExp = false;
            while (pos < len)
            {
                char numChar = pathData[pos];
                if (numChar is >= '0' and <= '9') { pos++; continue; }
                if (numChar == '.' && !sawDot && !sawExp) { sawDot = true; pos++; continue; }
                if ((numChar is 'e' or 'E') && !sawExp)
                {
                    sawExp = true;
                    pos++;
                    if (pos < len && pathData[pos] is '+' or '-') pos++;
                    continue;
                }
                break;
            }
            string numStr = pathData[start..pos];
            if (numStr.Length == 0) { pos++; continue; } // stray char, skip
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                tokens.Add(new(val));
            else
                pos++; // malformed; advance to avoid infinite loop
        }
        return tokens;
    }

    private static bool IsPathCommand(char command) =>
        command is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'Z' or 'z'
           or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a';

    private readonly struct Token
    {
        public readonly double Value;
        public readonly char Command;
        public readonly bool IsCommand;
        public Token(double value) { Value = value; Command = '\0'; IsCommand = false; }
        public Token(char command) { Command = command; Value = 0; IsCommand = true; }
    }
}