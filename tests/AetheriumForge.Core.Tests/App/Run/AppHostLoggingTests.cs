using AetheriumForge.App.Services;
using AetheriumForge.App.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetheriumForge.Core.Tests.App.Run;

public sealed partial class AppHostLoggingTests
{
    [Fact]
    public void Build_LogInformation_ReachesTheLogTailThroughTheSingleSerilogConfiguration()
    {
        // Arrange
        using var host = AppHost.Build();
        var sink = host.Services.GetRequiredService<ForgeRunnerLogSink>();
        var lines = new List<string>();
        sink.LinesReceived += (_, batch) => lines.AddRange(batch);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AppHostLoggingTests");

        // Act
        LogWiringCheck(logger);
        sink.Flush();

        // Assert
        Assert.Contains(lines, l => l.EndsWith("INF Log-tail wiring check", StringComparison.Ordinal));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Log-tail wiring check")]
    private static partial void LogWiringCheck(ILogger logger);
}
