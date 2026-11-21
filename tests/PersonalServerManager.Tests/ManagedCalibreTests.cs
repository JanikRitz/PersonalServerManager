using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalServerManager.LocalLibraries;
using Xunit;

namespace PersonalServerManager.Tests;

public sealed class ManagedCalibreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("psm-managed-calibre-").FullName;

    [Fact]
    public async Task LibrariesHaveIndependentProcessesPortsAndReaderConnections()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        var installation = Directory.CreateDirectory(Path.Combine(_root, "installation")).FullName;
        await File.WriteAllTextAsync(Path.Combine(installation, "calibre-server.exe"), "fixture");
        var first = await LibraryAsync(store, "first", FreePort(), installation);
        var second = await LibraryAsync(store, "second", FreePort(), installation);
        var starts = new List<ProcessStartInfo>();
        using var http = new HttpClient(new ReadyHandler(first.ManagedPort));
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, start =>
            {
                starts.Add(start);
                return Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p fixture=")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
            });
        await server.StartServerAsync(first.Id);
        await server.StartServerAsync(second.Id);
        Assert.Equal(2, starts.Count);
        Assert.Contains(first.RootPath, starts[0].ArgumentList);
        Assert.DoesNotContain(second.RootPath, starts[0].ArgumentList);
        Assert.Contains(second.RootPath, starts[1].ArgumentList);
        var firstConfiguration = Path.Combine(_root, "managed-calibre", first.Id.ToString());
        var secondConfiguration = Path.Combine(_root, "managed-calibre", second.Id.ToString());
        Assert.True(starts[0].Environment.TryGetValue("CALIBRE_CONFIG_DIRECTORY", out var firstEnvironment));
        Assert.True(starts[1].Environment.TryGetValue("CALIBRE_CONFIG_DIRECTORY", out var secondEnvironment));
        Assert.Equal(firstConfiguration, firstEnvironment);
        Assert.Equal(secondConfiguration, secondEnvironment);
        Assert.All(starts, start =>
        {
            Assert.Equal("1", start.Environment["CALIBRE_NO_SI_DANGER_DANGER"]);
            Assert.Contains("--disable-local-write", start.ArgumentList);
            Assert.Contains("--disable-auth", start.ArgumentList);
        });
        Assert.True(Directory.Exists(firstConfiguration));
        Assert.True(Directory.Exists(secondConfiguration));
        Assert.Equal(first.ManagedPort, server.GetStatus(first.Id).Url!.Port);
        Assert.Equal(second.ManagedPort, server.GetStatus(second.Id).Url!.Port);
        Assert.Equal("first-id", server.WithReaderConnection(first).ContentServerLibraryId);
        Assert.Equal("second-id", server.WithReaderConnection(second).ContentServerLibraryId);
        var duplicate = first with { Id = Guid.NewGuid(), Name = "duplicate", ManagedPort = FreePort() };
        await store.SaveAsync(duplicate);
        await server.StartServerAsync(duplicate.Id);
        Assert.Contains("already has a managed server", server.GetStatus(duplicate.Id).Message);
        Assert.Equal(2, starts.Count);
        Assert.NotNull(server.GetStatus(first.Id).Url);
        Assert.NotNull(server.GetStatus(second.Id).Url);
        var unrelated = first with { Id = Guid.NewGuid() };
        Assert.Equal(unrelated, server.WithReaderConnection(unrelated));
        Assert.Equal(first with { RootPath = second.RootPath }, server.WithReaderConnection(first with { RootPath = second.RootPath }));
        await server.StopServerAsync(first.Id);
        Assert.Null(server.GetStatus(first.Id).Url);
        Assert.NotNull(server.GetStatus(second.Id).Url);
        await server.StartServerAsync(first.Id);
        Assert.Equal(firstConfiguration, starts[2].Environment["CALIBRE_CONFIG_DIRECTORY"]);
        await server.StopServerAsync();
        Assert.Null(server.GetStatus(second.Id).Url);
    }

    [Fact]
    public async Task OnlyExplicitlyEnabledManagedLibrariesStartWithApp()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var first = await LibraryAsync(store, "first", FreePort(), _root);
        await store.SaveAsync(first with { StartWithApp = true });
        var second = await LibraryAsync(store, "second", FreePort(), _root);
        using var http = new HttpClient(new ReadyHandler(first.ManagedPort));
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, _ => Process.Start(
                new ProcessStartInfo("cmd.exe", "/d /c set /p fixture=")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true }));
        await server.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.GetStatus(first.Id).Url is null) await Task.Delay(10, timeout.Token);
        Assert.Null(server.GetStatus(second.Id).Url);
        await server.StopAsync(CancellationToken.None);
        Assert.Null(server.GetStatus(first.Id).Url);
    }

    [Fact]
    public async Task LegacyGlobalAutostartDoesNotStartLibrariesAutomatically()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        var first = await LibraryAsync(store, "first", FreePort(), _root);
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store, NullLogger<ManagedCalibreServer>.Instance);
        await server.SaveSettingsAsync(new(first.ManagedPort, true));
        await server.StartAsync(CancellationToken.None);
        Assert.Null(server.GetStatus(first.Id).Url);
        Assert.Equal("Stopped", server.GetStatus(first.Id).Message);
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OccupiedPortFailureIsScopedToTheRequestedLibrary()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        try
        {
            var first = await LibraryAsync(store, "first", ((IPEndPoint)listener.LocalEndpoint).Port, _root);
            var second = await LibraryAsync(store, "second", FreePort(), _root);
            using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store, NullLogger<ManagedCalibreServer>.Instance);
            await server.StartServerAsync(first.Id);
            Assert.Contains("already in use", server.GetStatus(first.Id).Message);
            Assert.Equal("Stopped", server.GetStatus(second.Id).Message);
            await server.StopServerAsync(first.Id);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, first.ManagedPort);
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsManualStartDuringReadiness(bool hostedShutdown)
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var library = await LibraryAsync(store, "first", FreePort(), _root);
        using var handler = new BlockingReadyHandler();
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        using var process = StandInProcess();
        var processId = process.Id;
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, _ => process);
        await server.StartAsync(CancellationToken.None);
        var start = server.StartServerAsync(library.Id, caller.Token);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = server.StartServerAsync(library.Id);
            var stop = hostedShutdown ? server.StopAsync(CancellationToken.None) : server.StopServerAsync();
            await Task.WhenAll(start, queued, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(HasExited(processId));
            Assert.Equal("Stopped", server.GetStatus(library.Id).Message);
            Assert.Null(server.WithReaderConnection(library).ContentServerUrl);
        }
        finally
        {
            caller.Cancel();
            await start;
            await server.StopServerAsync();
            KillStandIn(process);
        }
    }

    [Fact]
    public async Task StartsAfterShutdownDoNotLaunchProcesses()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var library = await LibraryAsync(store, "first", FreePort(), _root);
        using var http = new HttpClient(new ReadyHandler(library.ManagedPort));
        var launches = 0;
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, _ =>
            {
                launches++;
                return StandInProcess();
            });
        await server.StopServerAsync();
        await server.StartServerAsync(library.Id);
        await server.StartServerAsync();
        Assert.Equal(0, launches);
        Assert.Null(server.GetStatus(library.Id).Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTerminationRetainsOwnershipAndDoesNotAbortOtherStops(bool returnsWithoutExit)
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var first = await LibraryAsync(store, "first", FreePort(), _root);
        var second = await LibraryAsync(store, "second", FreePort(), _root);
        using var firstProcess = StandInProcess();
        using var secondProcess = StandInProcess();
        var firstProcessId = firstProcess.Id;
        var secondProcessId = secondProcess.Id;
        using var http = new HttpClient(new ReadyHandler(first.ManagedPort));
        var fail = true;
        var launches = 0;
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http,
            _ => ++launches == 1 ? firstProcess : secondProcess,
            process =>
            {
                if (ReferenceEquals(process, firstProcess) && fail)
                {
                    if (returnsWithoutExit) return Task.CompletedTask;
                    throw new System.ComponentModel.Win32Exception(5);
                }
                return TerminateStandInAsync(process);
            });
        try
        {
            await server.StartServerAsync(first.Id);
            await server.StartServerAsync(second.Id);
            await server.StopServerAsync(first.Id);
            Assert.Contains("Retry stopping", server.GetStatus(first.Id).Message);
            Assert.True(server.GetStatus(first.Id).IsRunning);
            Assert.Null(server.GetStatus(first.Id).Url);
            Assert.NotNull(server.GetStatus(second.Id).Url);
            Assert.Null(server.WithReaderConnection(first).ContentServerUrl);
            await server.StartServerAsync(first.Id);
            Assert.Equal(2, launches);

            await server.StopServerAsync();
            Assert.False(HasExited(firstProcessId));
            Assert.True(HasExited(secondProcessId));
            Assert.Contains("Retry stopping", server.GetStatus(first.Id).Message);
            Assert.Equal("Stopped", server.GetStatus(second.Id).Message);

            fail = false;
            await server.StopServerAsync();
            Assert.True(HasExited(firstProcessId));
            Assert.Equal("Stopped", server.GetStatus(first.Id).Message);
            Assert.False(server.GetStatus(first.Id).IsRunning);
        }
        finally
        {
            fail = false;
            await server.StopServerAsync();
            KillStandIn(firstProcess);
            KillStandIn(secondProcess);
        }
    }

    [Fact]
    public async Task ReadinessFailureRetainsProcessWhenCleanupFails()
    {
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "calibre-server.exe"), "fixture");
        var library = await LibraryAsync(store, "first", FreePort(), _root);
        using var process = StandInProcess();
        var processId = process.Id;
        using var http = new HttpClient(new ReadyHandler(-1));
        var fail = true;
        var launches = 0;
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, http, _ => { launches++; return process; },
            owned => fail ? Task.FromException(new OperationCanceledException()) : TerminateStandInAsync(owned));
        try
        {
            await server.StartServerAsync(library.Id);
            Assert.False(HasExited(processId));
            Assert.Contains("Retry stopping", server.GetStatus(library.Id).Message);
            Assert.Null(server.WithReaderConnection(library).ContentServerUrl);
            await server.StartServerAsync(library.Id);
            Assert.Equal(1, launches);
            await server.StopServerAsync();
            Assert.False(HasExited(processId));
            Assert.Contains("Retry stopping", server.GetStatus(library.Id).Message);
            fail = false;
            await server.StopServerAsync(library.Id);
            Assert.True(HasExited(processId));
            Assert.Equal("Stopped", server.GetStatus(library.Id).Message);
        }
        finally
        {
            fail = false;
            await server.StopServerAsync();
            KillStandIn(process);
        }
    }

    [InstalledCalibreFact]
    public async Task InstalledCalibreServesTwoLibrariesAndStopsThemIndependently()
    {
        var installation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2");
        var store = new JsonLocalLibraryStore(Path.Combine(_root, "libraries.json"));
        var first = await LibraryAsync(store, "first", FreePort(), installation);
        var second = await LibraryAsync(store, "second", FreePort(), installation);
        foreach (var library in new[] { first, second })
        {
            File.Delete(Path.Combine(library.RootPath, "metadata.db"));
            var start = new ProcessStartInfo(Path.Combine(installation, "calibre-debug.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.Environment["CALIBRE_CONFIG_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(_root, "fixture-config")).FullName;
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("from calibre.db.legacy import LibraryDatabase; library_database = LibraryDatabase(" +
                System.Text.Json.JsonSerializer.Serialize(library.RootPath) + "); library_database.close()");
            using var creator = Process.Start(start)!;
            var error = creator.StandardError.ReadToEndAsync();
            var output = creator.StandardOutput.ReadToEndAsync();
            try
            {
                await creator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(creator.ExitCode == 0, await error);
                await output;
            }
            finally { KillStandIn(creator); }
        }
        var launched = new List<(int Id, Task<string> Error)>();
        using var server = new ManagedCalibreServer(Path.Combine(_root, "settings.json"), store,
            NullLogger<ManagedCalibreServer>.Instance, launchProcess: start =>
            {
                start.RedirectStandardError = true;
                start.RedirectStandardOutput = true;
                var process = Process.Start(start)!;
                launched.Add((process.Id, process.StandardError.ReadToEndAsync()));
                _ = process.StandardOutput.ReadToEndAsync();
                return process;
            });
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            await server.StartServerAsync(first.Id);
            await server.StartServerAsync(second.Id);
            foreach (var library in new[] { first, second })
            {
                var status = server.GetStatus(library.Id);
                var errors = string.Join("\n", launched.Where(entry => entry.Error.IsCompletedSuccessfully).Select(entry => entry.Error.Result));
                Assert.True(status.Url is not null, status.Message + "\n" + errors.Replace(_root, "<TEMP>")
                    .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<PROFILE>"));
                using var response = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(status.Url!, "ajax/library-info")));
                var map = response.RootElement.GetProperty("library_map").EnumerateObject().ToArray();
                Assert.Equal(library.Name, Assert.Single(map).Value.GetString());
            }
            Assert.Equal(2, launched.Count);
            Assert.All(launched, entry => Assert.False(HasExited(entry.Id)));
            await server.StopServerAsync(first.Id);
            Assert.True(HasExited(launched[0].Id));
            Assert.False(HasExited(launched[1].Id));
            Assert.Null(server.GetStatus(first.Id).Url);
            using var remaining = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(
                new Uri(server.GetStatus(second.Id).Url!, "ajax/library-info")));
            Assert.Equal("second", Assert.Single(remaining.RootElement.GetProperty("library_map").EnumerateObject()).Value.GetString());
        }
        finally
        {
            await server.StopServerAsync();
            Assert.All(launched, entry => Assert.True(HasExited(entry.Id)));
        }
    }

    private sealed class InstalledCalibreFactAttribute : FactAttribute
    {
        public InstalledCalibreFactAttribute()
        {
            var installation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2");
            if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(installation, "calibre-server.exe")) ||
                !File.Exists(Path.Combine(installation, "calibre-debug.exe")))
                Skip = "Requires an installed Calibre server and calibre-debug to create temporary libraries.";
        }
    }

    private static async Task TerminateStandInAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Process StandInProcess() => Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p fixture=")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static void KillStandIn(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) { }
    }

    private sealed class BlockingReadyHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Readiness should be cancelled.");
        }
    }

    private async Task<LocalLibraryInstance> LibraryAsync(ILocalLibraryStore store, string name, int port, string installation)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "metadata.db"), "fixture");
        var library = new LocalLibraryInstance(Guid.NewGuid(), name, folder, LocalLibraryKind.Calibre,
            ManagedPort: port, CalibreInstallationFolder: installation);
        await store.SaveAsync(library);
        return library;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ReadyHandler(int firstPort) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Port == firstPort
                ? "{\"library_map\":{\"first-id\":\"first\"}}" : "{\"library_map\":{\"second-id\":\"second\"}}") });
    }

    public void Dispose() => Directory.Delete(_root, true);
}