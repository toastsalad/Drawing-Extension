using BenchmarkDotNet.Attributes;

namespace PlotterHelper.Bench;

/// <summary>
/// A fixed amount of arithmetic, unrelated to any drawing, so a run can be normalised against machine
/// drift.
/// </summary>
/// <remarks>
/// Comparing two revisions by running them in two sessions does not work on this machine: in one run
/// where a change was made, an untouched benchmark moved 4.17 -> 5.46 ms, thirty per cent. That is
/// bigger than most of the changes worth making.
///
/// Running the same workload twice in one session is the only reliable comparison, and it is not
/// possible when the two revisions are different builds. So every run also measures this, and the
/// ratio <c>benchmark / calibration</c> is comparable across sessions even when the absolute numbers
/// are not. Quote the ratio when a change is small.
/// </remarks>
[MemoryDiagnoser]
public class CalibrationBench
{
    private readonly double[] _data = new double[4096];

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _data.Length; i++) _data[i] = i * 0.5 + 1;
    }

    /// <summary>Deliberately simple and branch-light: this measures the machine, not the code.</summary>
    [Benchmark(Baseline = true)]
    public double FixedWorkload()
    {
        double total = 0;
        for (int pass = 0; pass < 2000; pass++)
            for (int i = 0; i < _data.Length; i++)
                total += _data[i] % (i + 3);
        return total;
    }
}
