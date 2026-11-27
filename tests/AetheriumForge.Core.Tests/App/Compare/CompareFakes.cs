using AetheriumForge.App.Pages.Compare;
using AetheriumForge.Core.Imaging;
using AetheriumForge.Core.Jobs;
using AetheriumForge.Core.Pipeline;
using AetheriumForge.Core.Rules;

namespace AetheriumForge.Core.Tests.App.Compare;

/// <summary>Serves a small synthetic uncompressed DDS (decoded by RawDds, no texconv) for any path; counts reads.</summary>
internal sealed class CompareFakeSource : ITextureSource
{
    private int _reads;

    public int Size { get; init; } = 8;

    public int Reads => Volatile.Read(ref _reads);

    public Task<byte[]> ReadAsync(string path, string archiveName, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        var seed = (byte)(path.Length * 7);
        var image = new RgbaImage(Size, Size);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = (byte)(seed + i);
            image.Pixels[i + 1] = (byte)(i / 4 * 3);
            image.Pixels[i + 2] = 128;
            image.Pixels[i + 3] = 255;
        }

        return Task.FromResult(RawDds.Write(image));
    }
}

/// <summary>Nearest ×2 upscaler; counts calls and can be held at the start with <see cref="Gate"/>.</summary>
internal sealed class CompareFakeUpscaler : IUpscaler
{
    private int _calls;

    public string ModelName { get; init; } = "fake";

    public int Scale => 2;

    public int Calls => Volatile.Read(ref _calls);

    public TaskCompletionSource? Gate { get; set; }

    public async Task<PlanarImage> UpscaleAsync(PlanarImage rgb, IProgress<TileProgress>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var output = new PlanarImage(rgb.Channels, rgb.Width * Scale, rgb.Height * Scale);
        for (var c = 0; c < rgb.Channels; c++)
        {
            for (var y = 0; y < output.Height; y++)
            {
                for (var x = 0; x < output.Width; x++)
                {
                    output.Data[output.IndexOf(c, x, y)] = rgb.Data[rgb.IndexOf(c, x / Scale, y / Scale)];
                }
            }
        }

        progress?.Report(new TileProgress(1, 1));
        return output;
    }

    public void Dispose()
    {
    }
}

internal sealed class CompareFakeBackend : IPreviewBackend
{
    public CompareFakeSource FakeSource { get; } = new();

    public CompareFakeUpscaler Upscaler { get; } = new();

    public bool HasProject { get; set; } = true;

    public ITextureSource? Source => HasProject ? FakeSource : null;

    public ITextureCodec? Codec => null;

    public string? WorkFolder { get; init; }

    public string? DiskCacheFolder { get; init; }

    public IReadOnlyList<ModelInfo> Models { get; init; } =
    [
        new ModelInfo("fake", "fake.onnx", "Fake", 2, "fp32", null, []),
        new ModelInfo("other", "other.onnx", "Other", 2, "fp32", null, []),
    ];

    public IUpscaler GetUpscaler(string model) => Upscaler;
}

internal sealed class CompareFakeData : ICompareProjectData
{
    public event EventHandler? Changed;

    public bool HasProject { get; set; } = true;

    public ForgeRules Rules { get; set; } = new()
    {
        Groups =
        [
            new GroupRule { Name = "Clutter", Model = "fake", NormalStrength = 1.4 },
            new GroupRule { Name = "Large", Model = "fake" },
        ],
    };

    public List<JobRecord> Jobs { get; } = [];

    public int Saves { get; private set; }

    public List<long> Requeued { get; } = [];

    public Task<ForgeRules> LoadRulesAsync(CancellationToken cancellationToken) => Task.FromResult(Rules);

    public Task SaveRulesAsync(ForgeRules rules, CancellationToken cancellationToken)
    {
        Rules = rules;
        Saves++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobRecord>> SearchFinishedAsync(string text, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobRecord>>([.. Jobs.Where(job => job.State == JobState.Done && job.Path.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit)]);

    public Task<IReadOnlyList<JobRecord>> GetGroupJobsAsync(string group, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobRecord>>([.. Jobs.Where(job => job.Group == group && job.State != JobState.Skipped).OrderBy(job => job.Id)]);

    public Task<IReadOnlyList<long>> FindDoneWithDifferentLookAsync(string group, LookSettings look, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<long>>([.. Jobs.Where(job => job.Group == group && job.State == JobState.Done).Select(job => job.Id)]);

    public Task<int> RequeueDoneAsync(IReadOnlyCollection<long> jobIds, CancellationToken cancellationToken)
    {
        Requeued.AddRange(jobIds);
        return Task.FromResult(jobIds.Count);
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public static JobRecord Job(long id, string group, JobState state = JobState.Done, string kind = "Diffuse", string? path = null) =>
        new(id, path ?? $"textures\\{group.ToLowerInvariant()}\\tex{id:D3}.dds", "Skyrim - Textures0.bsa", 8, 8, "BC1_UNORM", kind, group, 16, "fake", state, 0, null, null, null);
}

internal sealed class CompareFakeDialogs : ICompareDialogs
{
    public bool Answer { get; set; }

    public List<string> Questions { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Questions.Add(message);
        return Task.FromResult(Answer);
    }
}
