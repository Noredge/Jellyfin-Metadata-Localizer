using Localizer.Core;
using Microsoft.Data.Sqlite;

internal static class PreviewChecks
{
    internal static void Run(string root, Action<string, Action> check)
    {
        var scope = new LibraryKey("preview-server", "preview-library");
        var language = TargetLanguage.English;
        var spec = new GenerationSpec("https://provider.example/v1", "preview", "p1", "r1", "{}", "{}", "Translate.");
        (CandidateStore Store, string Path) Fresh()
        {
            var path = Path.Combine(root, "preview-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            return (new CandidateStore(path), path);
        }
        MediaKey Key(string item) => new(scope.ServerId, scope.LibraryId, item);
        ObservedMovie Movie(string item) => new(Key(item), true, false, "原文", "CODE · ", "CODE · 原文");
        SourceSnapshot Source(CandidateStore store, string item) => store.ObserveSource(Key(item), "原文", "CODE · ");
        Candidate Accepted(CandidateStore store, SourceSnapshot source, string text)
        {
            var candidate = store.SaveGenerated(source.Id, language, text, spec);
            return store.Approve(candidate.Id, candidate.Revision);
        }

        check("preview_batch_preserves_status_order_and_version_fields", () =>
        {
            var (store, _) = Fresh();
            var readySource = Source(store, "ready"); var ready = Accepted(store, readySource, "Ready title");
            var sameSource = Source(store, "same"); Accepted(store, sameSource, "Same title");
            var reviewSource = Source(store, "review"); store.SaveGenerated(reviewSource.Id, language, "Review title", spec);
            var changedSource = Source(store, "changed"); Source(store, "missing-candidate");
            var movies = new[]
            {
                Movie("review") with { CurrentName = "CODE · Review title" },
                Movie("outside") with { Key = Key("outside") with { LibraryId = "outside" }, NameLocked = true },
                Movie("ready"), Movie("missing-source"),
                Movie("changed") with { OriginalTitle = "已变化" },
                Movie("same") with { CurrentName = "CODE · Same title" },
                Movie("locked") with { NameLocked = true }, Movie("missing-candidate"),
                Movie("not-movie") with { IsMovie = false, NameLocked = true }
            };
            var previews = new LanguagePreview(store).Build(scope, language, movies);
            Expect(previews.Select(x => x.Key).SequenceEqual(movies.Select(x => x.Key)));
            Expect(previews.Select(x => x.ExpectedCurrentName).SequenceEqual(movies.Select(x => x.CurrentName)));
            Expect(previews.Select(x => x.Status).SequenceEqual(new[] { PreviewStatus.NeedsApproval,
                PreviewStatus.OutOfScope, PreviewStatus.ReadyForReview, PreviewStatus.MissingSource,
                PreviewStatus.SourceChanged, PreviewStatus.AlreadyMatches, PreviewStatus.Locked,
                PreviewStatus.MissingCandidate, PreviewStatus.OutOfScope }));
            Expect(previews[2].SourceId == readySource.Id && previews[2].CandidateId == ready.Id
                && previews[2].CandidateRevision == ready.Revision && previews[2].SelectionRevision == 1
                && previews[2].ProposedName == "CODE · Ready title");
            Expect(previews[4].SourceId == changedSource.Id && previews[4].CandidateId is null && previews[4].ProposedName is null);
            Expect(previews[6].SourceId is null && previews[6].CandidateId is null);
        });

        check("preview_single_key_ignores_other_keys_languages_and_history", () =>
        {
            var (store, path) = Fresh(); var poison = new List<string>();
            var historic = Source(store, "requested"); poison.Add(store.SaveGenerated(historic.Id, language, "Historical", spec).Id);
            var current = store.ObserveSource(Key("requested"), "更新原文", "CODE · ");
            var expected = Accepted(store, current, "Current title");
            poison.Add(store.SaveGenerated(current.Id, TargetLanguage.SimplifiedChinese, "另一语言", spec).Id);
            foreach (var key in new[] { Key("unrelated"), Key("requested") with { LibraryId = "other-library" },
                Key("requested") with { ServerId = "other-server" } })
            {
                var other = store.ObserveSource(key, "原文", "CODE · ");
                poison.Add(store.SaveGenerated(other.Id, language, "Unrelated", spec).Id);
            }
            PoisonCandidateLanguages(path, poison);
            var result = new LanguagePreview(store).Build(scope, language,
                [Movie("requested") with { OriginalTitle = "更新原文" }]).Single();
            Expect(result.Status == PreviewStatus.ReadyForReview && result.SourceId == current.Id
                && result.CandidateId == expected.Id && result.ProposedName == "CODE · Current title");
        });

        check("preview_source_change_does_not_read_its_selected_candidate", () =>
        {
            var (store, path) = Fresh(); var source = Source(store, "changed");
            var candidate = store.SaveGenerated(source.Id, language, "Unreadable candidate", spec);
            PoisonCandidateLanguages(path, [candidate.Id]);
            var result = new LanguagePreview(store).Build(scope, language,
                [Movie("changed") with { DisplayPrefix = "DIFFERENT · " }]).Single();
            Expect(result.Status == PreviewStatus.SourceChanged && result.SourceId == source.Id && result.CandidateId is null);
        });

        check("preview_empty_locked_outside_and_duplicates_do_not_open_database", () =>
        {
            var (store, path) = Fresh(); File.Move(path, path + ".held");
            var preview = new LanguagePreview(store);
            Expect(preview.Build(scope, language, []).Count == 0);
            var locked = Movie("") with { NameLocked = true };
            var outside = Movie("outside") with { Key = Key("outside") with { ServerId = "" } };
            var result = preview.Build(scope, language, [locked, outside]);
            Expect(result[0].Status == PreviewStatus.Locked && result[1].Status == PreviewStatus.OutOfScope);
            Throws<ArgumentException>(() => preview.Build(scope, language, [outside, outside]));
            Throws<ArgumentException>(() => preview.Build(scope, language, [Movie("")]));
            Throws<ArgumentOutOfRangeException>(() => preview.Build(scope, (TargetLanguage)99, []));
            Throws<ArgumentException>(() => preview.Build(scope with { LibraryId = "" }, language, []));
            Expect(!File.Exists(path));
        });

        check("preview_large_sparse_key_batch_and_quoted_identity_are_complete", () =>
        {
            var (store, _) = Fresh();
            var movies = Enumerable.Range(0, 1501).Select(index => Movie("item-" + index)).ToArray();
            movies[750] = Movie("中文 '); DROP TABLE sources; -- 🐈");
            foreach (var index in new[] { 0, 750, 1500 })
                Accepted(store, store.ObserveSource(movies[index].Key, "原文", "CODE · "), "Title " + index);
            var result = new LanguagePreview(store).Build(scope, language, movies);
            Expect(result.Count == movies.Length && result.Select(x => x.Key).SequenceEqual(movies.Select(x => x.Key)));
            Expect(result.Count(x => x.Status == PreviewStatus.ReadyForReview) == 3
                && result.Count(x => x.Status == PreviewStatus.MissingSource) == 1498);
            Expect(result[750].ProposedName == "CODE · Title 750" && store.GetCurrentSource(movies[750].Key) is not null);
        });

        check("preview_sources_and_selections_share_one_read_snapshot", () =>
        {
            var (store, path) = Fresh(); var movies = Enumerable.Range(0, 12).Select(index => Movie("concurrent-" + index)).ToArray();
            foreach (var movie in movies)
            {
                var first = store.ObserveSource(movie.Key, movie.OriginalTitle, movie.DisplayPrefix);
                Accepted(store, first, "Snapshot title");
                store.ObserveSource(movie.Key, "不同原文", movie.DisplayPrefix);
            }
            var stop = 0;
            using var started = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                using var db = OpenRaw(path); var state = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    state = 1 - state; using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE source_heads SET source_id=(SELECT s.id FROM sources s
                            WHERE s.server_id=source_heads.server_id AND s.library_id=source_heads.library_id
                                AND s.item_id=source_heads.item_id AND s.version=$version);
                        UPDATE candidates SET approved=$approved,revision=$revision;
                        """;
                    cmd.Parameters.AddWithValue("$version", state == 0 ? 1 : 2);
                    cmd.Parameters.AddWithValue("$approved", state == 0 ? 1 : 0);
                    cmd.Parameters.AddWithValue("$revision", state == 0 ? 2 : 1);
                    cmd.ExecuteNonQuery(); tx.Commit(); started.Set();
                }
            });
            try
            {
                if (!started.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Snapshot writer did not start.");
                var preview = new LanguagePreview(store);
                for (var index = 0; index < 30; index++)
                {
                    var result = preview.Build(scope, language, movies);
                    Expect(result.All(x => x.Status == PreviewStatus.SourceChanged)
                        || result.All(x => x.Status == PreviewStatus.ReadyForReview && x.CandidateRevision == 2));
                }
            }
            finally { Volatile.Write(ref stop, 1); writer.GetAwaiter().GetResult(); }
        });
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, ForeignKeys = false, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); return db;
    }
    private static void PoisonCandidateLanguages(string path, IEnumerable<string> ids)
    {
        // Deliberately unreadable rows in this private fixture detect accidental whole-library reads.
        using var db = OpenRaw(path); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA ignore_check_constraints=ON;"; cmd.ExecuteNonQuery();
        foreach (var id in ids)
        {
            cmd.CommandText = "UPDATE candidates SET language='unsupported-fixture-language' WHERE id=$id;";
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        }
    }
    private static void Expect(bool value) { if (!value) throw new InvalidOperationException("Preview assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
