using AetheriumForge.Core.Gpu;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.App.Services;

/// <summary>
/// Everything one run needs, resolved by <see cref="ForgeRunner"/> before any GPU session is created.
/// </summary>
/// <param name="Project">The open project.</param>
/// <param name="Store">The project's job store (owned by the session).</param>
/// <param name="Options">Pipeline options (work and output folders, job limit, model override).</param>
/// <param name="TexconvPath">The texconv executable.</param>
/// <param name="TexconvDevice">The DXGI adapter index texconv uses for GPU BC7 encoding, or <see langword="null"/> for its default.</param>
/// <param name="InstalledModels">The installed models.</param>
/// <param name="Models">The plan of each model the queued jobs use, keyed by the model name on the job (or the override).</param>
/// <param name="DefaultDeviceId">DirectML device for a model that is not in <paramref name="Models"/>.</param>
internal sealed record ForgeRunPlan(
    ProjectInfo Project,
    JobStore Store,
    PipelineOptions Options,
    string TexconvPath,
    int? TexconvDevice,
    IReadOnlyList<ModelInfo> InstalledModels,
    IReadOnlyDictionary<string, ModelRunPlan> Models,
    int DefaultDeviceId);

/// <summary>
/// How one model runs: the installed variant, the adapter, the tile size and the ETA seed from the GPU profile.
/// </summary>
/// <param name="Model">The model name on the job (or the override); also the ETA key.</param>
/// <param name="Variant">The installed model that runs (the FP16 variant when the profile chose FP16 and it is installed).</param>
/// <param name="DeviceId">The DirectML device index, re-resolved from the current adapters.</param>
/// <param name="TileSize">The upscaler tile size in source pixels.</param>
/// <param name="SecondsPerMegapixel">The ETA seed from the GPU profile, or <see langword="null"/> without a profile.</param>
/// <param name="Profile">The GPU profile used, or <see langword="null"/>.</param>
internal sealed record ModelRunPlan(string Model, string Variant, int DeviceId, int TileSize, double? SecondsPerMegapixel, GpuProfile? Profile);

/// <summary>
/// The per-run services built from a <see cref="ForgeRunPlan"/>; disposing them releases the GPU sessions and archive readers.
/// </summary>
internal interface IForgeRunResources : IDisposable
{
    /// <summary>Gets the pipeline of this run.</summary>
    IForgePipeline Pipeline { get; }
}

/// <summary>
/// The parts of <see cref="ForgeRunner"/> that touch the file system, the GPU profile file and ONNX Runtime; replaced by fakes in tests.
/// </summary>
internal interface IForgeRunBackend
{
    /// <summary>Gets the models folder, for messages.</summary>
    string ModelsFolder { get; }

    /// <summary>Lists the installed models.</summary>
    /// <returns>The models.</returns>
    IReadOnlyList<ModelInfo> DiscoverModels();

    /// <summary>Finds texconv.</summary>
    /// <returns>The full path.</returns>
    /// <exception cref="FileNotFoundException">texconv was not found.</exception>
    string FindTexconv();

    /// <summary>Finds the GPU profile of a model family on an adapter.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="family">The model family.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profile, or <see langword="null"/>.</returns>
    Task<GpuProfile?> FindProfileAsync(GpuAdapterInfo adapter, string family, CancellationToken cancellationToken);

    /// <summary>Builds the per-run services.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The services; the caller disposes them when the run ends.</returns>
    IForgeRunResources CreateRun(ForgeRunPlan plan);
}

/// <summary>
/// Pure planning helpers of <see cref="ForgeRunner"/>.
/// </summary>
internal static class ForgeRunPlanning
{
    /// <summary>The tile size without a GPU profile.</summary>
    public const int DefaultTileSize = 384;

    /// <summary>The texconv executable name.</summary>
    public const string TexconvFileName = "texconv.exe";

    /// <summary>The solution file that marks the repository root.</summary>
    public const string SolutionFileName = "AetheriumForge.slnx";

    /// <summary>How many parent folders above the executable folder are searched for the repository root.</summary>
    public const int MaxRepositoryLevels = 6;

    /// <summary>
    /// Picks the installed variant of <paramref name="model"/>: the profile's precision when installed, otherwise the exact name, otherwise
    /// the FP32 variant, otherwise any variant of the family.
    /// </summary>
    /// <param name="model">The model name on the job.</param>
    /// <param name="profile">The GPU profile, or <see langword="null"/>.</param>
    /// <param name="installed">The installed models.</param>
    /// <returns>The variant name, or <see langword="null"/> when no variant of the family is installed.</returns>
    public static string? ChooseVariant(string model, GpuProfile? profile, IReadOnlyList<ModelInfo> installed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(installed);

        var family = GpuProfiler.GetModelFamily(model);
        var variants = installed.Where(m => string.Equals(GpuProfiler.GetModelFamily(m.Name), family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (variants.Count == 0)
        {
            return null;
        }

        if (profile is not null && variants.FirstOrDefault(m => string.Equals(m.Precision, profile.Precision, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            return preferred.Name;
        }

        return (variants.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase))
            ?? variants.FirstOrDefault(m => string.Equals(m.Precision, ModelCatalog.DefaultPrecision, StringComparison.OrdinalIgnoreCase))
            ?? variants[0]).Name;
    }

    /// <summary>Lists the models the jobs need (parallax maps and jobs without a model run none).</summary>
    /// <param name="jobs">The jobs about to run.</param>
    /// <param name="modelOverride">The override, or <see langword="null"/>.</param>
    /// <returns>Distinct model names, sorted.</returns>
    public static IReadOnlyList<string> RequiredModels(IEnumerable<JobRecord> jobs, string? modelOverride)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return [.. jobs
            .Where(j => !string.Equals(j.Kind, "Parallax", StringComparison.OrdinalIgnoreCase))
            .Select(j => string.IsNullOrWhiteSpace(modelOverride) ? j.Model : modelOverride)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Lists the texconv candidates in lookup order: <c>&lt;exe&gt;\tools\texconv.exe</c>, then <c>&lt;repo&gt;\tools\texconv.exe</c>.</summary>
    /// <param name="baseDirectory">The executable's folder.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The candidates.</returns>
    public static IReadOnlyList<string> TexconvCandidates(string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        var candidates = new List<string> { Path.Combine(baseDirectory, "tools", TexconvFileName) };
        if (FindRepositoryRoot(baseDirectory, fileExists) is { } repo)
        {
            var repoTool = Path.Combine(repo, "tools", TexconvFileName);
            if (!candidates.Contains(repoTool, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(repoTool);
            }
        }

        return candidates;
    }

    /// <summary>Finds texconv.</summary>
    /// <param name="baseDirectory">The executable's folder.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The full path of texconv.exe.</returns>
    /// <exception cref="FileNotFoundException">texconv was not found; the message lists the paths tried and how to get it.</exception>
    public static string FindTexconv(string baseDirectory, Func<string, bool> fileExists)
    {
        var candidates = TexconvCandidates(baseDirectory, fileExists);
        foreach (var candidate in candidates)
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        var tried = string.Join(Environment.NewLine, candidates.Select(c => $"  - {c}"));
        throw new FileNotFoundException(
            $"texconv.exe was not found. Tried:{Environment.NewLine}{tried}{Environment.NewLine}Download it with 'pwsh tools\\fetch-tools.ps1' from the repository root.",
            TexconvFileName);
    }

    /// <summary>Walks up from <paramref name="start"/>, at most <see cref="MaxRepositoryLevels"/> parents, to the folder that holds the solution.</summary>
    /// <param name="start">The first folder to look in.</param>
    /// <param name="fileExists">File existence probe.</param>
    /// <returns>The repository root, or <see langword="null"/>.</returns>
    public static string? FindRepositoryRoot(string start, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var level = 0;
        for (var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start)); !string.IsNullOrEmpty(dir) && level <= MaxRepositoryLevels; dir = Path.GetDirectoryName(dir), level++)
        {
            if (fileExists(Path.Combine(dir, SolutionFileName)))
            {
                return Path.TrimEndingDirectorySeparator(dir);
            }
        }

        return null;
    }
}
