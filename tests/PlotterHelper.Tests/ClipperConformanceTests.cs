using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;

namespace PlotterHelper.Tests;

/// <summary>
/// Pins the geometry the hatch engine gets out of <see cref="HatchClipper"/>, so a change to the
/// region or offset code shows up as a readable diff instead of as a plot that comes out wrong.
/// </summary>
/// <remarks>
/// The 139 behaviour tests check G-code end to end, which is coarse for polygon work: a slightly
/// different offset moves every hatch line without changing pass or fail.
///
/// A fingerprint is a <b>region</b>, not a ring list. The same area can be described by one
/// self-intersecting ring or by two simple ones, and that difference is an implementation detail of
/// whoever produced it -- comparing ring text would fail on a correct rewrite. Instead each result
/// is sampled on a fixed grid and recorded as a picture, plus scalars that do not depend on
/// decomposition: the filled area and the total length of the clipped runs.
///
/// The cases represent what the fills meet in real drawings: a plain ring, a ring with a hole, a
/// concave shape, a neck narrower than twice the inset (an erosion has to split it), a
/// self-intersecting figure-eight (even-odd has to resolve it), and two overlapping rings.
///
/// Regenerate with <c>PLOTTERHELPER_UPDATE_CONFORMANCE=1 dotnet test</c>, then read the diff before
/// committing it. A baseline is only worth having if a change to it is reviewed.
/// </remarks>
public class ClipperConformanceTests
{
    private const double IntScale = 1_000_000.0;
    private const int GridSteps = 64;

    private sealed record Case(string Name, List<List<Vector>> Polygons);

    private static List<Vector> Ring(params double[] xy)
    {
        var ring = new List<Vector>(xy.Length / 2);
        for (int i = 0; i + 1 < xy.Length; i += 2) ring.Add(new Vector(xy[i], xy[i + 1]));
        return ring;
    }

    private static readonly Case[] CaseList =
    [
        new("square", [Ring(10, 10, 90, 10, 90, 90, 10, 90)]),
        new("donut", [Ring(0, 0, 100, 0, 100, 100, 0, 100), Ring(40, 40, 60, 40, 60, 60, 40, 60)]),
        new("concave-l", [Ring(0, 0, 60, 0, 60, 20, 20, 20, 20, 60, 0, 60)]),
        new("thin-neck", [Ring(0, 0, 16, 0, 16, 9.6, 24, 9.6, 24, 0, 40, 0, 40, 20, 24, 20, 24, 10.4, 16, 10.4, 16, 20, 0, 20)]),
        new("bowtie", [Ring(0, 0, 40, 40, 40, 0, 0, 40)]),
        new("overlapping", [Ring(0, 0, 30, 0, 30, 30, 0, 30), Ring(20, 20, 50, 20, 50, 50, 20, 50)]),
    ];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in CaseList) data.Add(testCase.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Geometry_Matches_The_Recorded_Baseline(string name)
    {
        var testCase = CaseList.Single(c => c.Name == name);
        string actual = Report(testCase);
        string path = BaselinePath(name);

        if (Environment.GetEnvironmentVariable("PLOTTERHELPER_UPDATE_CONFORMANCE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing baseline '{path}'. Write it with PLOTTERHELPER_UPDATE_CONFORMANCE=1 and review the result.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void Every_Case_Has_A_Baseline()
    {
        if (Environment.GetEnvironmentVariable("PLOTTERHELPER_UPDATE_CONFORMANCE") == "1") return;
        foreach (var testCase in CaseList)
            Assert.True(File.Exists(BaselinePath(testCase.Name)), $"{testCase.Name}.txt is missing");
    }

    // ---- the report ---------------------------------------------------------------

    private static string Report(Case testCase)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(testCase.Name).Append('\n');
        text.Append("# HatchClipper conformance baseline. Each section is a sampled picture of the region\n");
        text.Append("# plus scalars that do not depend on how it is decomposed into rings.\n");
        text.Append("# Regenerate with PLOTTERHELPER_UPDATE_CONFORMANCE=1 and review the diff.\n");

        var polygons = testCase.Polygons;
        var union = HatchClipper.Union(polygons);

        Section(text, "region of the input rings");
        text.Append(Picture(union));

        Section(text, "eroded by 0.5");
        text.Append(Picture(HatchClipper.Inflate(union, -0.5, 0.25)));

        Section(text, "dilated by 1.0");
        text.Append(Picture(HatchClipper.Inflate(union, 1.0, 0.25)));

        Section(text, "clipped horizontal runs, 4 apart");
        text.Append(Runs(testCase));

        Section(text, "point-in-polygon, first ring");
        text.Append(Picture(new List<List<Vector>> { testCase.Polygons[0] }));

        return text.ToString().Replace("\r\n", "\n");
    }

    private static void Section(StringBuilder text, string title) =>
        text.Append("\n== ").Append(title).Append(" ==\n");

    /// <summary>How much of each grid cell is inside, as a picture, and the filled area.</summary>
    private static string Picture(PathsD region) => Picture(ToRings(region));

    private static string Picture(List<List<Vector>> rings)
    {
        var bounds = Bounds(rings);
        var region = new EvenOddRegion(rings);
        var text = new StringBuilder();

        long filled = 0;
        double cellArea = ((bounds.MaxX - bounds.MinX) / GridSteps) * ((bounds.MaxY - bounds.MinY) / GridSteps);
        for (int row = 0; row < GridSteps; row++)
        {
            double y = bounds.MinY + (bounds.MaxY - bounds.MinY) * (row + 0.5) / GridSteps;
            for (int column = 0; column < GridSteps; column++)
            {
                double x = bounds.MinX + (bounds.MaxX - bounds.MinX) * (column + 0.5) / GridSteps;
                bool inside = region.Contains(new Vector(x, y));
                text.Append(inside ? '#' : '.');
                if (inside) filled++;
            }
            text.Append('\n');
        }

        text.Append("area ~ ").Append(Number(filled * cellArea)).Append('\n');
        return text.ToString();
    }

    /// <summary>Horizontal runs clipped to the region: how many, and how long in total.</summary>
    private static string Runs(Case testCase)
    {
        var bounds = Bounds(testCase.Polygons);
        var open = new Paths64();
        for (double y = bounds.MinY + 2; y < bounds.MaxY; y += 4)
            open.Add(HatchClipper.ToPath64(new List<Vector> { new(bounds.MinX - 2, y), new(bounds.MaxX + 2, y) }));

        var clipped = HatchClipper.ClipOpenSubject(HatchClipper.ToPaths64(testCase.Polygons), open);

        double total = 0;
        var text = new StringBuilder();
        text.Append("runs: ").Append(clipped.Count).Append('\n');
        foreach (var path in clipped)
        {
            if (path.Count < 2) continue;
            Vector start = new(path[0].X / IntScale, path[0].Y / IntScale);
            Vector end = new(path[^1].X / IntScale, path[^1].Y / IntScale);
            total += (end - start).Length;
        }
        text.Append("total length ~ ").Append(Number(total)).Append('\n');
        return text.ToString();
    }

    // ---- helpers -------------------------------------------------------------------

    private static List<List<Vector>> ToRings(PathsD paths)
    {
        var rings = new List<List<Vector>>(paths.Count);
        foreach (var path in paths)
        {
            var ring = new List<Vector>(path.Count);
            foreach (var point in path) ring.Add(new Vector(point.x, point.y));
            if (ring.Count >= 3) rings.Add(ring);
        }
        return rings;
    }

    private static string Number(double value)
    {
        double rounded = Math.Round(value, 4);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(List<List<Vector>> polygons)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var polygon in polygons)
            foreach (var point in polygon)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
        return (minX, minY, maxX, maxY);
    }

    private static string BaselinePath(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Conformance", name + ".txt");
}
