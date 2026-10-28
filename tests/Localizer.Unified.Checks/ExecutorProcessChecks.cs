using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class ExecutorProcessChecks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        string[] points = ["request.prepared", "request.sending", "request.received", "request.result_transaction", "request.committed",
            "display.intent_transaction", "display.prepared", "display.marked", "display.returned", "display.verified", "manual.saved"];
        var sdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(sdk)) sdk = Path.Combine(new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        async Task<int> Child(string directory, string? point)
        {
            var start = new ProcessStartInfo(sdk!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--executor-process"); start.ArgumentList.Add(directory); start.ArgumentList.Add(point ?? "recover");
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await File.WriteAllTextAsync(directory + (point is null ? "-recovery.log" : "-crash.log"), await output + await error); return process.ExitCode;
        }
        foreach (var point in points)
        {
            var directory = Path.Combine(root, "process-" + point.Replace('.', '-'));
            check("real process exits at " + point, await Child(directory, point) == 79);
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "CRASH-POINT.json")));
            check("crash point is authoritative at " + point, marker.RootElement.GetProperty("stage").GetString() == point);
            check("recovery process succeeds at " + point, await Child(directory, null) == 0);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "PROCESS-RECOVERY.json")));
            var data = result.RootElement; var unknown = point is "request.sending" or "request.received" or "request.result_transaction";
            var expectedSends = point is "request.sending" or "manual.saved" ? 0 : 1;
            check("no repeated HTTP after recovery at " + point, data.GetProperty("actualSyntheticSends").GetInt32() == expectedSends);
            check("correct unknown or completed recovery at " + point, data.GetProperty("workState").GetString() == (unknown ? "paused" : "completed")
                && data.GetProperty("hostWrites").GetInt32() == (unknown ? 0 : 1) && data.GetProperty("versions").GetInt32() == (unknown ? 0 : 1));
            check("SQLite and journal recovery valid at " + point, data.GetProperty("integrity").GetString() == "ok" && data.GetProperty("foreignKeys").GetInt32() == 0);
        }
    }
    internal static async Task Child(string root, string point)
    {
        var fresh = !Directory.Exists(root); var database = new UnifiedDatabase(root, create: fresh); var content = new ContentStore(database); var settings = new SettingsStore(database);
        var host = new PersistentSyntheticHost(root);
        if (fresh)
        {
            var library = Id(); settings.Save(1, "zh-Hans", new(true, false, false), [library], false);
            settings.SaveConnection(0, "https://example.invalid/v1", "synthetic", 5000, 1000, Id(), "{}"); settings.EstablishBaseline();
            host.State = new(new("synthetic-server", library, Id()), "C:\\synthetic\\crash.mp4", "原文 A", "LAB · 原文 A", 0); host.Save();
            var media = content.RegisterMedia(host.State.Key, host.State.Path, "{}"); var field = content.Observe(media, "Name", host.State.Original, "{}", "LAB · ");
            if (point == "manual.saved")
            {
                content.SaveManual(field, "zh-Hans", "已保存的人工作品", true, host.State.Display, "manual-crash");
                PersistentSyntheticHost.Write(Path.Combine(root, "CRASH-POINT.json"), JsonSerializer.Serialize(new { stage = point })); Environment.Exit(79);
            }
            var queue = new WorkStore(database); queue.Submit("single-submission", "fill", settings.Read().Revision, [new(field, host.State.Display)]);
        }
        var work = new WorkStore(database); var transport = new PersistentSyntheticTransport(root);
        using var executor = new UnifiedExecutor(work, host, transport, (stage, _) =>
        {
            if (stage != point) return;
            PersistentSyntheticHost.Write(Path.Combine(root, "CRASH-POINT.json"), JsonSerializer.Serialize(new { stage })); Environment.Exit(79);
        });
        for (var i = 0; i < 50 && await executor.RunNextAsync(); i++) { }
        using var db = database.Open();
        var result = new { workState = Scalar<string>(db, null, "SELECT state FROM work_items WHERE target_kind='media'"),
            actualSyntheticSends = transport.Sends, hostWrites = host.State.Writes, versions = Scalar<long>(db, null, "SELECT COUNT(*) FROM translation_versions"),
            integrity = Scalar<string>(db, null, "PRAGMA integrity_check"), foreignKeys = Scalar<long>(db, null, "SELECT COUNT(*) FROM pragma_foreign_key_check"),
            realModelRequests = 0, realHostWrites = 0 };
        PersistentSyntheticHost.Write(Path.Combine(root, "PROCESS-RECOVERY.json"), JsonSerializer.Serialize(result));
    }
    internal sealed record SyntheticHostState(MediaIdentity Key, string Path, string Original, string Display, int Writes);
    private sealed class PersistentSyntheticHost : IUnifiedHost
    {
        private readonly string file;
        internal SyntheticHostState State { get; set; } = null!;
        internal PersistentSyntheticHost(string root)
        { file = Path.Combine(root, "synthetic-host.json"); if (File.Exists(file)) State = JsonSerializer.Deserialize<SyntheticHostState>(File.ReadAllText(file))!; }
        internal void Save() => Write(file, JsonSerializer.Serialize(State));
        internal static void Write(string file, string value)
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream); writer.Write(value); writer.Flush(); stream.Flush(true);
        }
        public Task<HostFieldObservation> ReadAsync(MediaIdentity key, string field, CancellationToken token) => Task.FromResult(new HostFieldObservation(key, State.Path, field, State.Original, State.Display, "LAB · "));
        public Task<HostWriteOutcome> WriteAsync(HostWriteCommand command, CancellationToken token)
        {
            if (!command.AuthorityStillValid() || command.Original != State.Original || command.Key != State.Key || command.MediaPath != State.Path) return Task.FromResult(HostWriteOutcome.Blocked);
            if (State.Display == command.TargetDisplay) return Task.FromResult(HostWriteOutcome.AlreadyMatches);
            if (State.Display != command.ExpectedDisplay) return Task.FromResult(HostWriteOutcome.Conflict);
            State = State with { Display = command.TargetDisplay, Writes = State.Writes + 1 }; Save(); return Task.FromResult(HostWriteOutcome.Written);
        }
    }
    private sealed class PersistentSyntheticTransport(string root) : IUnifiedTransport
    {
        private string FilePath => Path.Combine(root, "synthetic-sends.txt");
        internal int Sends => File.Exists(FilePath) ? int.Parse(File.ReadAllText(FilePath), System.Globalization.CultureInfo.InvariantCulture) : 0;
        public string PromptVersion(string field, string language) => "synthetic-v1";
        public Task<GenerationReply> GenerateAsync(GenerationInput input, ConnectionVersion connection, Action markSending, CancellationToken token)
        { markSending(); PersistentSyntheticHost.Write(FilePath, (Sends + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)); return Task.FromResult(new GenerationReply("已保存的译文", HttpStatus: 200)); }
    }
}
