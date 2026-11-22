namespace PlotterHelper.Trace;

/// <summary>
/// Shared degree-of-parallelism handling for the tracer. Thresholding, the distance transform,
/// thinning, resampling and curve fitting compute each output from inputs no other worker
/// writes, so they parallelise without changing a result; the skeleton walk and graph surgery
/// stay sequential.
/// </summary>
internal static class Parallelism
{
    /// <summary>Below this much work the scheduling costs more than it saves.</summary>
    private const int MinItems = 2000;

    /// <summary>
    /// Workers to use for a stage over <paramref name="workItems"/> independent items.
    /// 1 means "just do it on the calling thread".
    /// </summary>
    public static int Workers(int maxParallelism, long workItems)
    {
        if (workItems < MinItems) return 1;
        int cores = maxParallelism > 0 ? maxParallelism : Environment.ProcessorCount;
        return Math.Max(1, cores);
    }

    public static ParallelOptions Options(int workers) => new() { MaxDegreeOfParallelism = workers };
}
