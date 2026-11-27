using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetheriumForge.Core.Concurrency;

/// <summary>
/// Keeps the desktop app to one instance per logon session: the named mutex <c>Local\AetheriumForge.App.SingleInstance</c> marks the first
/// instance, which listens on the pipe <c>AetheriumForge.App.Activate.&lt;user SID&gt;</c>. A later instance sends "activate" with its
/// command-line arguments and exits; the first instance raises <see cref="ActivationRequested"/>.
/// </summary>
/// <remarks>
/// The mutex is only created, never owned, so there is no thread affinity: it exists while the first instance keeps its handle open and
/// disappears when that process exits (or crashes). Protocol: the server writes its process id (int32); the client may then allow it to
/// take the foreground, and writes a length-prefixed message (<c>"activate"</c>, argument count, arguments); the server answers one byte.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The mutex name.</summary>
    public const string DefaultMutexName = @"Local\AetheriumForge.App.SingleInstance";

    /// <summary>The pipe name prefix; the current user's SID follows.</summary>
    public const string PipeNamePrefix = "AetheriumForge.App.Activate.";

    private const string ActivateVerb = "activate";
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxArguments = 256;
    private const byte Ack = 1;

    private static readonly TimeSpan ServerReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private int _disposed;

    private SingleInstance(Mutex mutex, string pipeName, ILogger logger)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _logger = logger;
    }

    /// <summary>Raised on the listener thread when another instance asks this one to come to the front.</summary>
    public event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;

    /// <summary>Gets the pipe name for the current user: <c>AetheriumForge.App.Activate.&lt;SID&gt;</c>.</summary>
    public static string DefaultPipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return PipeNamePrefix + (identity.User?.Value ?? Environment.UserName);
        }
    }

    /// <summary>Gets a value indicating whether the listener runs.</summary>
    public bool IsListening => _listener is { IsCompleted: false };

    /// <summary>Claims the first-instance role.</summary>
    /// <param name="instance">This process's instance when it is the first; dispose it on exit.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when this process is the first instance.</returns>
    public static bool TryAcquire([NotNullWhen(true)] out SingleInstance? instance, ILogger? logger = null) =>
        TryAcquire(DefaultMutexName, DefaultPipeName, logger, out instance);

    /// <summary>Claims the first-instance role (tests use their own names).</summary>
    /// <param name="mutexName">The mutex name.</param>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <param name="instance">This process's instance when it is the first.</param>
    /// <returns><see langword="true"/> when this process is the first instance.</returns>
    internal static bool TryAcquire(string mutexName, string pipeName, ILogger? logger, [NotNullWhen(true)] out SingleInstance? instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex, pipeName, logger ?? NullLogger.Instance);
        return true;
    }

    /// <summary>Asks the first instance to come to the front.</summary>
    /// <param name="args">This process's command-line arguments, passed on.</param>
    /// <param name="timeout">How long to wait for the first instance.</param>
    /// <param name="beforeActivate">Called with the first instance's process id before it is asked to activate (for example to allow it to take the foreground).</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the first instance acknowledged the request.</returns>
    public static bool TrySignalPrimary(IReadOnlyList<string> args, TimeSpan timeout, Action<int>? beforeActivate = null, ILogger? logger = null) =>
        TrySignalPrimary(DefaultPipeName, args, timeout, beforeActivate, logger);

    /// <summary>Asks the first instance to come to the front (tests use their own pipe).</summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <param name="args">Arguments passed on.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="beforeActivate">Called with the first instance's process id.</param>
    /// <param name="logger">Logger, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the first instance acknowledged the request.</returns>
    internal static bool TrySignalPrimary(string pipeName, IReadOnlyList<string> args, TimeSpan timeout, Action<int>? beforeActivate, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(args);
        logger ??= NullLogger.Instance;

        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(timeout);
        // Pipe reads have no timeout: closing the stream at the deadline unblocks them.
        using var registration = deadline.Token.Register(client.Dispose);
        try
        {
            client.Connect(timeout);

            Span<byte> pidBytes = stackalloc byte[sizeof(int)];
            client.ReadExactly(pidBytes);
            var primaryPid = BinaryPrimitives.ReadInt32LittleEndian(pidBytes);
            beforeActivate?.Invoke(primaryPid);

            client.Write(BuildMessage(args));
            client.Flush();
            var acknowledged = client.ReadByte() == Ack;
            ConcurrencyLog.SignaledPrimary(logger, primaryPid, acknowledged);
            return acknowledged;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            ConcurrencyLog.SignalFailed(logger, ex, pipeName);
            return false;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Starts listening for activation requests (once).</summary>
    /// <exception cref="ObjectDisposedException">The instance was disposed.</exception>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _listener ??= Task.Run(() => ListenAsync(_stop.Token));
    }

    /// <summary>Stops the listener and releases the mutex, so a new first instance can start.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        // Dispose has no async form at the app's exit; the listener ends promptly once canceled and never throws.
        if (_listener is { } listener && !listener.Wait(ListenerStopTimeout))
        {
            ConcurrencyLog.ListenerStopSlow(_logger, ListenerStopTimeout);
        }

        _stop.Dispose();
        _mutex.Dispose();
    }

    private static byte[] BuildMessage(IReadOnlyList<string> args)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            var count = Math.Min(args.Count, MaxArguments);
            writer.Write(ActivateVerb);
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                writer.Write(args[i] ?? string.Empty);
            }
        }

        if (body.Length > MaxMessageBytes)
        {
            // Arguments are informational: drop them rather than fail the activation.
            return BuildMessage([]);
        }

        var message = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message, (int)body.Length);
        body.ToArray().CopyTo(message, sizeof(int));
        return message;
    }

    private static string[]? ParseMessage(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
        if (!string.Equals(reader.ReadString(), ActivateVerb, StringComparison.Ordinal))
        {
            return null;
        }

        var count = reader.ReadInt32();
        if (count is < 0 or > MaxArguments)
        {
            return null;
        }

        var args = new string[count];
        for (var i = 0; i < count; i++)
        {
            args[i] = reader.ReadString();
        }

        return args;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var args = await ReadRequestAsync(server, ct).ConfigureAwait(false);
                    if (args is null)
                    {
                        ConcurrencyLog.MalformedActivation(_logger);
                        continue;
                    }

                    ConcurrencyLog.ActivationReceived(_logger, args.Count);
                    RaiseActivationRequested(args);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that vanished or stalled: serve the next one.
                ConcurrencyLog.ActivationFailed(_logger, ex);
            }
        }
    }

    private static async Task<IReadOnlyList<string>?> ReadRequestAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ServerReadTimeout);

        var pid = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(pid, Environment.ProcessId);
        await server.WriteAsync(pid, timeout.Token).ConfigureAwait(false);
        await server.FlushAsync(timeout.Token).ConfigureAwait(false);

        var lengthBytes = new byte[sizeof(int)];
        await server.ReadExactlyAsync(lengthBytes, timeout.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > MaxMessageBytes)
        {
            return null;
        }

        var body = new byte[length];
        await server.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
        IReadOnlyList<string>? args;
        try
        {
            args = ParseMessage(body);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        if (args is not null)
        {
            await server.WriteAsync(new[] { Ack }, timeout.Token).ConfigureAwait(false);
            await server.FlushAsync(timeout.Token).ConfigureAwait(false);
        }

        return args;
    }

    private void RaiseActivationRequested(IReadOnlyList<string> args)
    {
        try
        {
            ActivationRequested?.Invoke(this, new ActivationRequestedEventArgs(args));
        }
        catch (Exception ex)
        {
            // A failing handler must not stop the listener.
            ConcurrencyLog.ActivationHandlerFailed(_logger, ex);
        }
    }
}

/// <summary>
/// Arguments of <see cref="SingleInstance.ActivationRequested"/>.
/// </summary>
/// <param name="args">The other instance's command-line arguments.</param>
public sealed class ActivationRequestedEventArgs(IReadOnlyList<string> args) : EventArgs
{
    /// <summary>Gets the other instance's command-line arguments.</summary>
    public IReadOnlyList<string> Args { get; } = args;
}
