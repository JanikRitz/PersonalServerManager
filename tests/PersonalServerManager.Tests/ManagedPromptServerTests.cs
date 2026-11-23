using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.Covers;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class ManagedPromptServerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-managed-prompt-").FullName;

    [Fact]
    public async Task ConfigurationRoundTripsAndCorruptFileIsPreserved()
    {
        var path = Path.Combine(_root, "nested", "settings.json");
        using var server = CreateServer(path);
        Assert.Equal(new ManagedPromptServerSettings(), await server.GetConfigurationAsync());
        var settings = LongRunningSettings() with { WorkingDirectory = _root, StartWithApp = true };
        await server.SaveConfigurationAsync(settings);
        Assert.Equal(settings, await server.GetConfigurationAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => server.SaveConfigurationAsync(new(Arguments: new string('\u2603', 16384))));
        Assert.Equal(settings, await server.GetConfigurationAsync());
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => server.GetConfigurationAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentStartsAreIdempotentAndStopDoesNotTouchOtherProcesses()
    {
        using var server = CreateServer();
        var unrelatedSettings = LongRunningSettings();
        var settings = CountingSettings(_root);
        using var unrelated = Process.Start(new ProcessStartInfo(unrelatedSettings.ExecutablePath, unrelatedSettings.Arguments)
            { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await server.SaveConfigurationAsync(settings);
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => server.StartServerAsync()));
            Assert.True(server.Status.IsRunning);
            var launches = Path.Combine(_root, "launches.txt");
            await WaitUntilAsync(() => File.Exists(launches) && File.ReadAllText(launches).Trim().Length > 0);
            var processId = int.Parse((await File.ReadAllLinesAsync(launches)).Single());
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.SaveConfigurationAsync(new()));
            await server.StopServerAsync();
            Assert.Equal(new ManagedPromptServerStatus("Stopped"), server.Status);
            Assert.True(ProcessHasExited(processId));
            Assert.False(unrelated.HasExited);
            await server.StartServerAsync();
            Assert.True(server.Status.IsRunning);
        }
        finally
        {
            await server.StopServerAsync();
            if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedLifecycleHonorsAutostartAndStopsOwnedProcess(bool autostart)
    {
        using var server = CreateServer();
        await server.SaveConfigurationAsync(LongRunningSettings() with { StartWithApp = autostart });
        await server.StartAsync(CancellationToken.None);
        try
        {
            if (autostart) await WaitUntilAsync(() => server.Status.IsRunning);
            else Assert.False(server.Status.IsRunning);
        }
        finally { await server.StopAsync(CancellationToken.None); }
        Assert.Equal(new ManagedPromptServerStatus("Stopped"), server.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartServerAsync());
        Assert.Equal(new ManagedPromptServerStatus("Stopped"), server.Status);
    }

    [Fact]
    public async Task UnexpectedExitIsVisibleAndServerCanRestart()
    {
        using var server = CreateServer();
        var settings = LongRunningSettings();
        await server.SaveConfigurationAsync(settings with { Arguments = OperatingSystem.IsWindows()
            ? "-NoProfile -NonInteractive -Command exit" : "0" });
        await server.StartServerAsync();
        await WaitUntilAsync(() => server.Status.Message == "Prompt server exited.");
        Assert.False(server.Status.IsRunning);
        await server.SaveConfigurationAsync(settings);
        try
        {
            await server.StartServerAsync();
            Assert.True(server.Status.IsRunning);
        }
        finally { await server.StopServerAsync(); }
    }

    [Fact]
    public async Task InvalidExecutableIsRejectedAndStatusReportsFailure()
    {
        using var server = CreateServer();
        await server.SaveConfigurationAsync(new("relative-executable"));
        await Assert.ThrowsAsync<InvalidDataException>(() => server.StartServerAsync());
        Assert.False(server.Status.IsRunning);
        Assert.Contains("could not start", server.Status.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => server.SaveConfigurationAsync(new(Arguments: "bad\0argument")));
    }

    private ManagedPromptServer CreateServer(string? path = null) =>
        new(path ?? Path.Combine(_root, "settings.json"), NullLogger<ManagedPromptServer>.Instance);

    private static ManagedPromptServerSettings LongRunningSettings() => OperatingSystem.IsWindows()
        ? new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"")
        : new("/bin/sleep", "60");

    private static ManagedPromptServerSettings CountingSettings(string directory) => OperatingSystem.IsWindows()
        ? LongRunningSettings() with
        {
            Arguments = "-NoProfile -NonInteractive -Command \"Add-Content -Path 'launches.txt' -Value $PID; Start-Sleep -Seconds 60\"",
            WorkingDirectory = directory
        }
        : new("/bin/sh", "-c \"echo $$ >> launches.txt; exec sleep 60\"", directory);

    private static bool ProcessHasExited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() => Directory.Delete(_root, true);
}