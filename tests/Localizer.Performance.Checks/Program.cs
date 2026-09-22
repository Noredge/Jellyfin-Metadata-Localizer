using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Localizer.Core;
using Localizer.Plugin;
using Microsoft.Data.Sqlite;

if (args.Length != 3) throw new ArgumentException("Expected scratch directory, report file, repository root.");
var scratch = Path.GetFullPath(args[0]);
var reportPath = Path.GetFullPath(args[1]);
var repo = Path.GetFullPath(args[2]);
var allowed = Path.Combine(repo, "work", "m9-performance") + Path.DirectorySeparatorChar;
if (!scratch.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(scratch))
    throw new ArgumentException("Fresh owned synthetic scratch directory required.");
Directory.CreateDirectory(scratch);
var scope = new LibraryKey("synthetic-performance-server", "synthetic-performance-library");
var language = TargetLanguage.SimplifiedChinese;
var now = "2026-09-06T00:00:00.0000000+00:00";
var spec = new GenerationSpec("https://synthetic.invalid/v1", "synthetic-offline", "performance-v1", "synthetic-v1",
    "{\"temperature\":0.2,\"max_completion_tokens\":1024}", "{}",
    "Synthetic local benchmark only. No provider is constructed. " + new string('p', 1024));
var completed = false;
string? error = null;
var runs = new List<object>();
var checks = new List<object>();
var watch = Stopwatch.StartNew();
var sourcePaths = new[] { "src/Localizer.Core/CandidateStore.cs", "src/Localizer.Core/WorkbenchStore.cs",
    "src/Localizer.Core/LanguagePreview.cs", "src/Localizer.Core/PreviewStore.cs", "src/Localizer.Plugin/CampaignStore.cs" };
var hashes = sourcePaths.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, path)))).ToLowerInvariant());
void SaveReport()
{
    var report = new
    {
        stage = "M9 synthetic storage and preview component performance", completed, error,
        generatedUtc = DateTimeOffset.UtcNow.ToString("O"), elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
        productionWrites = 0, cloudRequests = 0, jellyfinStarted = false, productionCampaignLimit = 5000,
        environment = new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
            stopwatchFrequency = Stopwatch.Frequency, buildConfiguration = "Release", sqliteVersion = SQLitePCL.raw.sqlite3_libversion().utf8_to_string() },
        methodology = new
        {
            repetitions = 3, sizes = new[] { 1000, 5000, 10000 },
            cold = "Logical first access through a new store to a fresh copied database/file path, including a separate path to avoid the campaign store's static cache; construction/copy excluded. OS page cache is NOT cleared. Not physical disk cold-cache timing.",
            warm = "After an untimed warmup: three measurements through the same database store; one measurement per each of three independently warmed campaign stores. Product methods still reopen their files/connections.",
            seed = "Real CandidateStore initializes schema and first source/candidate; remaining synthetic rows inserted with prepared SQLite statements in one transaction; integrity and foreign keys checked.",
            composition = "All rows have a source; 90% have candidates in both Chinese and English. Per ten rows: 1 missing, 1 unapproved, 1 approved human edit, 1 display matching/history head, 1 locked, 1 changed observed source, 1 pending Name operation, 3 further approved. No external metadata, people, images or filesystem scans.",
            taskPayload = "Fully populated per-item source/spec/preview with a 1084-character synthetic prompt; task serialization cost depends strongly on actual payload size.",
            writeSamples = "Per size: three independent campaign journals; each gets one ChangeItem followed by thirty ChangeItem calls. Complete N-item execution is NOT measured.",
            projections = "N * observed median per-update cost from a 30-update sample. Legacy JSON payload estimate uses N * full snapshot size; SQLite logical payload estimate uses N * one item plus header JSON size, excluding page/journal/index overhead. These are arithmetic projections, not full-library elapsed time or physical disk I/O measurements.",
            statusSearch = "Synthetic in-memory classification, ordinal name sort and literal search using the actual ReadWorkbench snapshot. This is NOT the real controller including bindings/Jellyfin authorization/people lookups.",
            concurrentWorkload = "This benchmark does not start or contact Jellyfin. Other desktop or CI runner activity remains uncontrolled.",
            limitations = new[] { "Standalone .NET 10 synthetic host. Results do not establish Jellyfin host/API/UI latency.",
                "No Jellyfin process, network, model call, NFO scan, metadata writeback or browser rendering is exercised.",
                "Campaign entries are populated serialization fixtures, not runnable translation/apply jobs; only CampaignStore persistence is exercised.",
                "10000-item campaign bypasses the product entrypoint ONLY to stress internal CampaignStore serialization; the real 5000 limit remains unchanged.",
                "Three observations describe this local machine and fixture, not a stable latency percentile or a capacity guarantee.",
                "Allocated bytes are process-wide managed allocation deltas; they are not peak retained memory or filesystem bytes written." }
        }, sourceHashes = hashes, checks, runs
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
void Check(string name, bool condition)
{
    checks.Add(new { name, passed = condition });
    if (!condition) throw new InvalidOperationException(name);
}
Guid Id(int number, int group = 0) => new(number, (short)group, 0, 0, 0, 0, 0, 0, 0, 0, 1);
SourceSnapshot Source(int number) => new(number, new(scope.ServerId, scope.LibraryId, Id(number).ToString("N")), 1,
    $"合成原題 旅の記録 {number:D6}", $"SYN-{number:D6} · ",
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"合成原題 旅の記録 {number:D6}"))).ToLowerInvariant());
string Title(int number, bool en = false) => en ? $"Synthetic travel {number:D6}" : $"合成旅行记录 {number:D6}";
string DisplayText(int number) => number % 10 == 2 ? $"人工编辑的合成标题 {number:D6}" : Title(number);
ObservedMovie Movie(int number)
{
    var source = Source(number);
    return new(source.Key, true, number % 10 == 4,
        number % 10 == 5 ? source.OriginalTitle + " changed" : source.OriginalTitle, source.DisplayPrefix,
        source.DisplayPrefix + (number % 10 == 3 ? DisplayText(number) : source.OriginalTitle));
}
Measurement Measure(Action action)
{
    var allocated = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew(); action(); sw.Stop();
    return new(Math.Round(sw.Elapsed.TotalMilliseconds, 4), GC.GetTotalAllocatedBytes(true) - allocated);
}
object Metric(IEnumerable<Measurement> sequence)
{
    var samples = sequence.ToArray();
    var sorted = samples.Select(x => x.Milliseconds).Order().ToArray();
    return new { samples, minimumMs = sorted.First(), medianMs = sorted[sorted.Length / 2], maximumMs = sorted.Last() };
}
CandidateStore CopyStore(string sourcePath, string destination)
{
    File.Copy(sourcePath, destination);
    return new CandidateStore(destination);
}
SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params string[] parameters)
{
    var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
    foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter, "");
    command.Prepare(); return command;
}
void Set(SqliteCommand command, params object?[] values)
{
    for (var index = 0; index < values.Length; index++) command.Parameters[index].Value = values[index] ?? DBNull.Value;
    command.ExecuteNonQuery();
}
void Seed(string path, int count)
{
    var store = new CandidateStore(path);
    var first = Source(1);
    var source = store.ObserveSource(first.Key, first.OriginalTitle, first.DisplayPrefix);
    store.SaveGenerated(source.Id, language, Title(1), spec);
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
    db.Open();
    using (var tx = db.BeginTransaction())
    {
        using var sources = Command(db, tx, "INSERT INTO sources VALUES($id,$s,$l,$i,1,$o,$p,$h,$now)", "$id", "$s", "$l", "$i", "$o", "$p", "$h", "$now");
        using var heads = Command(db, tx, "INSERT INTO source_heads VALUES($s,$l,$i,$id)", "$s", "$l", "$i", "$id");
        using var candidates = Command(db, tx, "INSERT INTO candidates VALUES($id,$source,$lang,$fp,$text,$edit,$rev,$approved,$spec,$now,$now)", "$id", "$source", "$lang", "$fp", "$text", "$edit", "$rev", "$approved", "$spec", "$now");
        using var selections = Command(db, tx, "INSERT INTO selections VALUES($source,$lang,$id,1)", "$source", "$lang", "$id");
        using var operations = Command(db, tx, "INSERT INTO name_operations VALUES($id,$s,$l,$i,$state,$json)", "$id", "$s", "$l", "$i", "$state", "$json");
        using var restore = Command(db, tx, "INSERT INTO name_restore_heads VALUES($s,$l,$i,$id,0)", "$s", "$l", "$i", "$id");
        for (var number = 1; number <= count; number++)
        {
            var current = Source(number);
            if (number != 1)
            {
                Set(sources, number, scope.ServerId, scope.LibraryId, current.Key.ItemId, current.OriginalTitle, current.DisplayPrefix, current.Hash, now);
                Set(heads, scope.ServerId, scope.LibraryId, current.Key.ItemId, number);
            }
            if (number % 10 != 0)
            {
                foreach (var english in new[] { false, true })
                {
                    if (number == 1 && !english) continue;
                    var id = Id(number, english ? 2 : 1).ToString("N");
                    var edited = !english && number % 10 == 2;
                    Set(candidates, id, number, english ? "en" : "zh-Hans", "synthetic-fingerprint-" + id,
                        Title(number, english), edited ? DisplayText(number) : null,
                        number % 10 == 1 ? 1 : edited ? 4 : 2, number % 10 == 1 ? 0 : 1, JsonSerializer.Serialize(spec), now);
                    Set(selections, number, english ? "en" : "zh-Hans", id);
                }
            }
            if (number % 10 is 3 or 6)
            {
                var id = Id(number, 3).ToString("N");
                var pending = number % 10 == 6;
                var operation = new NameOperation(id, "synthetic-operation-" + id, current.Key, OperationKind.Apply,
                    pending ? OperationState.Prepared : OperationState.Applied, number, Id(number, 1).ToString("N"), 2, 1,
                    language, current.DisplayPrefix + current.OriginalTitle, current.DisplayPrefix + DisplayText(number),
                    current.OriginalTitle, current.DisplayPrefix, null, false, false, null, now, now);
                Set(operations, id, scope.ServerId, scope.LibraryId, current.Key.ItemId, operation.State.ToString(), JsonSerializer.Serialize(operation));
                if (!pending) Set(restore, scope.ServerId, scope.LibraryId, current.Key.ItemId, id);
            }
        }
        tx.Commit();
    }
    using var integrity = db.CreateCommand(); integrity.CommandText = "PRAGMA integrity_check";
    Check($"seed_{count}_sqlite_integrity", (string?)integrity.ExecuteScalar() == "ok");
    integrity.CommandText = "PRAGMA foreign_key_check";
    using (var reader = integrity.ExecuteReader()) Check($"seed_{count}_foreign_keys", !reader.Read());
    integrity.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; integrity.ExecuteNonQuery();
}
try
{
    foreach (var count in new[] { 1000, 5000, 10000 })
    {
        Console.WriteLine($"BENCH {count}: seed and scoped reads");
        var root = Path.Combine(scratch, count.ToString()); Directory.CreateDirectory(root);
        var database = Path.Combine(root, "candidates.db");
        var seed = Measure(() => Seed(database, count));
        var movies = Enumerable.Range(1, count).Select(Movie).ToArray();
        var coldRead = new List<Measurement>(); var coldPreview = new List<Measurement>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var store = CopyStore(database, Path.Combine(root, $"read-first-{repeat}.db"));
            WorkbenchStoreSnapshot? snapshot = null;
            coldRead.Add(Measure(() => snapshot = store.ReadWorkbench(scope, language, count)));
            Check($"read_{count}_{repeat}_counts", snapshot!.Sources.Count == count && snapshot.Selections.Count == count * 9 / 10
                && snapshot.RestorePoints.Count == count / 10 && snapshot.Pending.Count == count / 10);
            store = CopyStore(database, Path.Combine(root, $"preview-first-{repeat}.db"));
            IReadOnlyList<LanguagePreviewEntry>? preview = null;
            coldPreview.Add(Measure(() => preview = new LanguagePreview(store).Build(scope, language, movies)));
            Check($"preview_{count}_{repeat}_status_mix", preview!.Count == count
                && preview.Count(x => x.Status == PreviewStatus.ReadyForReview) == count / 2
                && preview.Count(x => x.Status == PreviewStatus.MissingCandidate) == count / 10
                && preview.Count(x => x.Status == PreviewStatus.NeedsApproval) == count / 10
                && preview.Count(x => x.Status == PreviewStatus.AlreadyMatches) == count / 10
                && preview.Count(x => x.Status == PreviewStatus.SourceChanged) == count / 10
                && preview.Count(x => x.Status == PreviewStatus.Locked) == count / 10);
            Console.WriteLine($"BENCH {count}: logical cold round {repeat + 1}/3 complete");
        }
        var warmStore = new CandidateStore(database);
        var warmSnapshot = warmStore.ReadWorkbench(scope, language, count);
        var warmRead = Enumerable.Range(0, 3).Select(_ => Measure(() => warmSnapshot = warmStore.ReadWorkbench(scope, language, count))).ToArray();
        _ = new LanguagePreview(warmStore).Build(scope, language, movies);
        var warmPreview = new List<Measurement>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            warmPreview.Add(Measure(() => { var preview = new LanguagePreview(warmStore).Build(scope, language, movies); GC.KeepAlive(preview); }));
            Console.WriteLine($"BENCH {count}: warm preview round {repeat + 1}/3 complete");
        }
        string Status(ObservedMovie movie)
        {
            if (movie.NameLocked || warmSnapshot.Pending.Contains(movie.Key)) return "attention";
            var source = warmSnapshot.Sources[movie.Key];
            if (source.OriginalTitle != movie.OriginalTitle) return "changed";
            if (!warmSnapshot.Selections.TryGetValue(source.Id, out var selection)) return "untranslated";
            if (!selection.Candidate.Approved) return "review";
            return movie.CurrentName == source.DisplayPrefix + selection.Candidate.Text ? "applied" : "ready";
        }
        int matched = 0;
        void Search()
        {
            var rows = movies.Select(movie => new { movie.Key, Name = movie.CurrentName, Status = Status(movie) })
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            var counts = rows.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count());
            var filtered = rows.Where(x => x.Name.Contains("001", StringComparison.OrdinalIgnoreCase)).ToArray();
            matched = filtered.Length; GC.KeepAlive(filtered.Take(50).ToArray()); GC.KeepAlive(counts);
        }
        Search(); var search = Enumerable.Range(0, 3).Select(_ => Measure(Search)).ToArray();
        Check($"search_{count}_has_matches", matched > 0);
        Console.WriteLine($"BENCH {count}: campaign storage reads and 3 x (1 + 30) updates");
        var campaignId = Guid.NewGuid();
        var entries = movies.Select((movie, index) =>
        {
            var number = index + 1; var source = Source(number);
            var candidateId = Id(number, 1).ToString("N");
            return new CampaignItem(Id(number), movie.CurrentName, new string('a', 64), Id(number, 4).ToString("N"),
                Id(number, 5).ToString("N"), Source: source, Spec: spec, CandidateId: candidateId,
                Preview: new(movie.Key, language, PreviewStatus.ReadyForReview, movie.CurrentName,
                    source.DisplayPrefix + DisplayText(number), number, candidateId, 2, 1));
        }).ToArray();
        var record = new CampaignRecord(campaignId, scope, language.Tag(), "auto_apply", "Running", now, now, entries);
        var creates = new List<Measurement>(); var campaignReads = new List<Measurement>();
        var campaignWarm = new List<Measurement>(); var changeOne = new List<Measurement>(); var changeThirty = new List<Measurement>();
        long snapshotJsonBytes = 0, storageBytes = 0, walBytes = 0, journalBytes = 0, itemJsonBytes = 0, headerJsonBytes = 0;
        string storageFormat = "unknown";
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var campaignRoot = Path.Combine(root, $"campaign-{repeat}"); var campaignStore = new CampaignStore(campaignRoot);
            creates.Add(Measure(() => campaignStore.Create(record)));
            // A distinct copied path avoids the shared cache populated by Create.
            var coldRoot = campaignRoot + "-first-read"; Directory.CreateDirectory(coldRoot);
            var sqlite = Path.Combine(campaignRoot, "campaigns.db");
            if (File.Exists(sqlite))
            {
                File.Copy(sqlite, Path.Combine(coldRoot, "campaigns.db"));
                if (File.Exists(sqlite + "-wal")) File.Copy(sqlite + "-wal", Path.Combine(coldRoot, "campaigns.db-wal"));
                storageFormat = "sqlite-row-store";
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(coldRoot, "campaigns"));
                File.Copy(Path.Combine(campaignRoot, "campaigns", campaignId.ToString("N") + ".json"),
                    Path.Combine(coldRoot, "campaigns", campaignId.ToString("N") + ".json"));
                storageFormat = "whole-json-file";
            }
            var fresh = new CampaignStore(coldRoot);
            campaignReads.Add(Measure(() => { var read = fresh.Read(campaignId); GC.KeepAlive(read); }));
            _ = fresh.Read(campaignId);
            campaignWarm.Add(Measure(() => { var read = fresh.Read(campaignId); GC.KeepAlive(read); }));
            changeOne.Add(Measure(() => fresh.ChangeItem(campaignId, 0, item => item with { State = "Translated" })));
            changeThirty.Add(Measure(() =>
            {
                for (var index = 1; index <= 30; index++)
                    fresh.ChangeItem(campaignId, index, item => item with { State = "Applied", Acceptance = "automatic" });
            }));
            var final = fresh.Read(campaignId);
            Check($"campaign_{count}_{repeat}_persisted_31_sample_updates", final.Items.Length == count
                && final.Items[0].State == "Translated" && final.Items.Count(x => x.State == "Applied") == 30
                && final.Items[31].State == "Pending");
            snapshotJsonBytes = JsonSerializer.SerializeToUtf8Bytes(final).LongLength;
            itemJsonBytes = JsonSerializer.SerializeToUtf8Bytes(final.Items[1]).LongLength;
            headerJsonBytes = JsonSerializer.SerializeToUtf8Bytes(final with { Items = [] }).LongLength;
            var storageFile = storageFormat == "sqlite-row-store" ? Path.Combine(coldRoot, "campaigns.db")
                : Path.Combine(coldRoot, "campaigns", campaignId.ToString("N") + ".json");
            storageBytes = new FileInfo(storageFile).Length;
            walBytes = File.Exists(storageFile + "-wal") ? new FileInfo(storageFile + "-wal").Length : 0;
            journalBytes = File.Exists(storageFile + "-journal") ? new FileInfo(storageFile + "-journal").Length : 0;
            Console.WriteLine($"BENCH {count}: campaign update sample {repeat + 1}/3 complete");
        }
        var updateMedian = changeThirty.Select(x => x.Milliseconds / 30).Order().ElementAt(1);
        runs.Add(new
        {
            items = count, databaseBytes = new FileInfo(database).Length,
            campaignJsonBytes = storageFormat == "whole-json-file" ? (long?)snapshotJsonBytes : null,
            campaignStorage = new { format = storageFormat, mainFileBytes = storageBytes, walBytes, rollbackJournalBytes = journalBytes,
                equivalentFullSnapshotJsonBytes = snapshotJsonBytes, itemJsonBytes, headerJsonBytes },
            databaseCandidateRows = count * 18 / 10, seed,
            readWorkbenchFirstAccess = Metric(coldRead), readWorkbenchWarm = Metric(warmRead),
            languagePreviewFirstAccess = Metric(coldPreview), languagePreviewWarm = Metric(warmPreview),
            syntheticStatusSearch = Metric(search), searchMatchedItems = matched,
            campaignCreate = Metric(creates), campaignReadFirstAccess = Metric(campaignReads), campaignReadWarm = Metric(campaignWarm),
            campaignChangeOne = Metric(changeOne), campaignChangeThirty = Metric(changeThirty),
            arithmeticProjectionOnly = new { medianMsPerUpdateFromThirty = Math.Round(updateMedian, 4),
                oneUpdatePerItemSeconds = Math.Round(count * updateMedian / 1000, 3),
                oneUpdatePerItemJsonPayloadBytes = storageFormat == "whole-json-file" ? (long?)count * snapshotJsonBytes : null,
                oneUpdatePerItemLogicalSqlitePayloadBytes = storageFormat == "sqlite-row-store" ? (long?)count * (itemJsonBytes + headerJsonBytes) : null,
                completeCampaignWasMeasured = false, actualWorkerUpdatesPerItemMayExceedOne = true },
            internalTaskAboveProductLimit = count > 5000
        });
        SaveReport(); Console.WriteLine($"BENCH {count}: completed");
    }
    completed = true;
}
catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
finally { SaveReport(); }
Console.WriteLine($"REPORT completed={completed}; checks={checks.Count}; seconds={watch.Elapsed.TotalSeconds:F1}");
return completed ? 0 : 1;

sealed record Measurement(double Milliseconds, long AllocatedBytes);
