namespace PlotterHelper.Tests;

/// <summary>
/// Locate a drawing that the tests use, and fail when it is not there.
/// </summary>
/// <remarks>
/// The octopus regression tests used to hardcode <c>/home/desktop/Desktop/octopus.svg</c> and return early when
/// it did not exist. On every machine but the one that path belongs to, they asserted <c>true</c> and passed:
/// the regression they guard could come back and the suite would stay green. The file is in this repository,
/// in the benchmark project's assets, so the only honest behaviour is to find it and throw if it has moved.
/// </remarks>
public static class TestAssets
{
    private const string AssetFolder = "bench/PlotterHelper.Bench/assets";

    private static string? _repositoryRoot;

    /// <summary>Walk up from the test assembly until the asset folder is found.</summary>
    private static string RepositoryRoot()
    {
        if (_repositoryRoot is not null) return _repositoryRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, AssetFolder)))
            {
                _repositoryRoot = directory.FullName;
                return _repositoryRoot;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not find '{AssetFolder}' above '{AppContext.BaseDirectory}'; a test drawing lives there");
    }

    /// <summary>Absolute path of a test drawing, or a failure that names what is missing.</summary>
    public static string Drawing(string name)
    {
        var path = Path.Combine(RepositoryRoot(), AssetFolder, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"test drawing '{name}' is missing", path);
        return path;
    }
}
