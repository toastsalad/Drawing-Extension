using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace PlotterHelper.Bench;

/// <summary>
/// Entry point for the PlotterHelper benchmark harness. Wraps BenchmarkDotNet with a
/// few convenience flags.
///
/// Usage:
///   dotnet run -c Release --project bench/PlotterHelper.Bench -- [flags]
///
/// Flags (all optional):
///   --svg &lt;path&gt;          restrict to one SVG (resolved against assets/ if bare)
///   --filter &lt;glob&gt;        BDN name glob(s), |-separated, e.g. "*Hatch_*|*Connect*"
///   --quick                 short run (1 warmup + 3 iterations) for fast feedback
///   --stable                long run (8 warmup + 25 iterations) for judging a small change
///   --list                  print benchmark names and exit (no execution)
///
/// With no flags it runs the full suite across every SVG in assets/ with the default
/// (thorough) BDN job. Results land in BenchmarkDotNet.Artifacts/.
/// </summary>
public static class BenchProgram
{
    public static int Main(string[] args)
    {
        string? svg = null;
        string? filter = null;
        bool quick = false;
        bool stable = false;
        bool list = false;

        for (int argIndex = 0; argIndex < args.Length; argIndex++)
        {
            switch (args[argIndex])
            {
                case "--svg": svg = args[++argIndex]; break;
                case "--filter": filter = args[++argIndex]; break;
                case "--quick": quick = true; break;
                case "--stable": stable = true; break;
                case "--list": list = true; break;
                case "--help": case "-h": PrintHelp(); return 0;
            }
        }

        PipelineBench.SvgOverride = svg;   // pick the [ParamsSource] set

        var job = Job.Default;
        if (quick)
            job = job.WithWarmupCount(1).WithIterationCount(3);
        else if (stable)
            job = job.WithWarmupCount(8).WithIterationCount(25);

        var cfg = ManualConfig.CreateEmpty()
            .AddJob(job)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default)
            .AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance);
        if (filter is not null)
            cfg = cfg.AddFilter(new GlobFilter(filter.Split('|', StringSplitOptions.RemoveEmptyEntries)));

        if (list)
        {
            foreach (var b in BenchmarkConverter.TypeToBenchmarks(typeof(PipelineBench), cfg).BenchmarksCases)
                Console.WriteLine($"  {b.DisplayInfo}");
            return 0;
        }

        // The calibration workload is part of every run so its numbers can be normalised against
        // machine drift; see CalibrationBench.
        BenchmarkRunner.Run([typeof(PipelineBench), typeof(CalibrationBench), typeof(FieldBench)], cfg);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PlotterHelper bench -- BenchmarkDotNet harness");
        Console.WriteLine("  --svg <path>     restrict to one SVG (bare name resolved in assets/)");
        Console.WriteLine("  --filter <glob>  BDN name glob(s), |-separated (e.g. \"*Hatch_*|*Connect*\")");
        Console.WriteLine("  --quick          short run for fast feedback");
        Console.WriteLine("  --stable         long run, for judging a small change");
        Console.WriteLine("  --list           list benchmarks, don't run");
    }
}
