using AetheriumForge.Cli.Hosting;
using AetheriumForge.Core.Pipeline;

namespace AetheriumForge.Cli.Rendering;

/// <summary>
/// The live console view of a forge run: redraws <see cref="ProgressFormatter.FormatLive"/> in place about ten times a second and
/// handles the P (pause), R (resume) and S (stop) keys. When output is redirected it prints a plain line whenever the counts or the
/// state change, and at least every 15 seconds.
/// </summary>
internal sealed class ForgeConsoleView : IProgress<PipelineProgress>
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PlainHeartbeat = TimeSpan.FromSeconds(15);

    private readonly TextWriter _out;
    private readonly bool _readKeys;
    private bool _interactive;
    private PipelineProgress? _latest;
    private int _top = -1;
    private (int Done, int Failed, PipelineState State)? _lastPlain;
    private DateTimeOffset _lastPlainAt;

    /// <summary>Initializes a new instance of the <see cref="ForgeConsoleView"/> class.</summary>
    /// <param name="output">The console output.</param>
    /// <param name="interactive">Redraw in place (a real console); otherwise print plain lines.</param>
    /// <param name="readKeys">Read P/R/S from the keyboard (input is not redirected).</param>
    public ForgeConsoleView(TextWriter output, bool interactive, bool readKeys)
    {
        ArgumentNullException.ThrowIfNull(output);

        _out = output;
        _interactive = interactive;
        _readKeys = readKeys;
    }

    /// <inheritdoc />
    public void Report(PipelineProgress value) => Volatile.Write(ref _latest, value);

    /// <summary>
    /// Draws until <paramref name="pipelineRun"/> completes, forwarding keys to <paramref name="pipeline"/>, then draws the final frame.
    /// </summary>
    /// <param name="pipeline">The running pipeline.</param>
    /// <param name="pipelineRun">The task returned by <see cref="IForgePipeline.RunAsync"/>.</param>
    /// <param name="stop">Cancelled by the S key.</param>
    /// <returns>A task that completes after the final frame.</returns>
    public async Task RunAsync(IForgePipeline pipeline, Task pipelineRun, ConsoleCancellation stop)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(pipelineRun);
        ArgumentNullException.ThrowIfNull(stop);

        var cursorWasVisible = _interactive && TryHideCursor();
        try
        {
            while (!pipelineRun.IsCompleted)
            {
                if (_readKeys)
                {
                    HandleKeys(pipeline, stop);
                }

                Draw(final: false);
                await Task.WhenAny(pipelineRun, Task.Delay(FrameInterval)).ConfigureAwait(false);
            }

            Draw(final: true);
        }
        finally
        {
            if (cursorWasVisible)
            {
                Console.CursorVisible = true;
            }
        }
    }

    private static bool TryHideCursor()
    {
        try
        {
            var visible = OperatingSystem.IsWindows() && Console.CursorVisible;
            Console.CursorVisible = false;
            return visible;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void HandleKeys(IForgePipeline pipeline, ConsoleCancellation stop)
    {
        while (Console.KeyAvailable)
        {
            switch (char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar))
            {
                case 'P':
                    pipeline.Pause();
                    break;
                case 'R':
                    pipeline.Unpause();
                    break;
                case 'S':
                    stop.Cancel();
                    break;
                default:
                    break;
            }
        }
    }

    private void Draw(bool final)
    {
        var progress = Volatile.Read(ref _latest);
        if (progress is null)
        {
            return;
        }

        if (_interactive)
        {
            try
            {
                DrawInPlace(progress);
                return;
            }
            catch (IOException)
            {
                // The console cannot position the cursor (for example a limited terminal); continue with plain lines.
                _interactive = false;
            }
            catch (ArgumentOutOfRangeException)
            {
                _interactive = false;
            }
        }

        DrawPlain(progress, final);
    }

    private void DrawInPlace(PipelineProgress progress)
    {
        var width = Math.Max(ProgressFormatter.MinimumWidth, Console.WindowWidth);
        var lines = ProgressFormatter.FormatLive(progress, width);
        if (_top < 0)
        {
            _top = Console.CursorTop;
        }

        Console.SetCursorPosition(0, _top);
        foreach (var line in lines)
        {
            _out.WriteLine(line.PadRight(width - 1));
        }

        _out.Flush();

        // If the buffer scrolled while writing, the frame now starts higher up.
        _top = Math.Max(0, Console.CursorTop - lines.Count);
    }

    private void DrawPlain(PipelineProgress progress, bool final)
    {
        var key = (progress.Done, progress.Failed, progress.State);
        var now = DateTimeOffset.UtcNow;
        if (!final && _lastPlain == key && now - _lastPlainAt < PlainHeartbeat)
        {
            return;
        }

        if (final && _lastPlain == key)
        {
            return;
        }

        _lastPlain = key;
        _lastPlainAt = now;
        _out.WriteLine(ProgressFormatter.FormatPlain(progress));
        _out.Flush();
    }
}
