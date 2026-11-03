using Localizer.Core;
using Localizer.Unified;
using static Localizer.Unified.UnifiedDatabase;

internal static class PerformanceCleanupChecks
{
    internal static async Task Run(string root, Action<string, bool> check)
    {
        using (var f = new ExecutorFixture(Path.Combine(root, "performance-name-fast-paths")))
        {
            var config = new ConfigurationStore(f.Database);
            for (var i = 0; i < 30; i++) config.SavePerson(Id(), 0, "Unrelated " + i, "人名" + i, null, []);
            config.SeedBuiltin();
            var term = config.Terms(limit: 1).Single();
            var media = f.Add("ordinary", "A traveler returns.", term.Original);
            using (var timing = new OperationTimings())
            {
                f.Submit([media], ["Genres"]); await f.Drain();
                check("builtin term display does not load unrelated person rules", timing.Counts().GetValueOrDefault("person_rules_load") == 0 && f.Transport.Calls == 0 && f.Host.Writes == 1);
            }
            using (var timing = new OperationTimings())
            {
                f.Submit([media], ["Name"]); await f.Drain();
                check("legacy model with empty frozen names avoids rule loading", timing.Counts().GetValueOrDefault("person_rules_load") == 0 && f.Transport.Calls == 1);
            }
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("A traveler returns.", PersonTemplate: new(1, "A traveler returns.", []))); };
            using (var timing = new OperationTimings())
            {
                f.Submit([media], ["Overview"]); await f.Drain();
                check("validated empty-slot template avoids rule loading", timing.Counts().GetValueOrDefault("person_rules_load") == 0 && f.Host.Get(f.Key(media), "Overview").Display == "A traveler returns.");
            }
            var alice = Id(); config.SavePerson(alice, 0, "Alice", "阿莉", null, []); var named = f.Add("Alice");
            f.Transport.Handler = (_, send, _) => { send(); return Task.FromResult(new GenerationReply("阿莉归来", PersonTemplate: new(1, "__JML_PERSON_A__归来", [new("__JML_PERSON_A__", "Alice", "阿莉", alice)]))); };
            f.Submit([named], ["Name"]); await f.Drain(); var calls = f.Transport.Calls;
            config.SavePerson(alice, 1, "Alice", "阿力", null, []);
            using (var timing = new OperationTimings())
            {
                f.Submit([named], ["Name"]); await f.Drain();
                check("named template loads fresh operation-local rules after an edit", timing.Counts().GetValueOrDefault("person_rules_load") == 1 && f.Transport.Calls == calls && f.Host.Get(f.Key(named), "Name").Display == "LAB · 阿力归来");
            }
        }
        using (var f = new ExecutorFixture(Path.Combine(root, "performance-inventory-scope")))
        {
            var media = f.Add("scope"); var wrapped = new ScopedInventory(f.Host); var observer = new SourceObserver(f.Work, wrapped, wrapped);
            await observer.ObserveAllAsync();
            check("one inventory scope surrounds listing and all media reads", wrapped.Scopes == 1 && !wrapped.Active && wrapped.InsideReads == 3);
            await observer.ObserveMediaAsync(new(f.Key(media), f.Host.Get(f.Key(media), "Name").MediaPath));
            check("individual observations do not retain a previous enumeration", wrapped.Scopes == 1 && wrapped.OutsideReads == 3);
            wrapped.FailListing = true; var failed = false;
            try { await observer.ObserveAllAsync(); } catch (IOException) { failed = true; }
            check("failed enumeration releases its inventory scope", failed && !wrapped.Active && wrapped.Scopes == 2);
            wrapped.FailListing = false; f.Executor.Dispose();
            using var executor = new UnifiedExecutor(f.Work, wrapped, f.Transport);
            f.Submit([media], ["Name"]); while (await executor.RunNextAsync()) { }
            check("execution and host writes never inherit an inventory snapshot", wrapped.Writes == 1 && wrapped.OutsideReads > 3 && !wrapped.Active);
        }
    }
    private sealed class ScopedInventory(FakeUnifiedHost inner) : IUnifiedHost, IUnifiedInventory, IUnifiedInventoryReadScope
    {
        internal bool Active, FailListing;
        internal int Scopes, InsideReads, OutsideReads, Writes;
        public IDisposable BeginInventoryReadScope() { Active = true; Scopes++; return new Scope(() => Active = false); }
        public Task<HostMedia[]> ListAsync(string[] libraries, CancellationToken token)
        { if (!Active) throw new InvalidOperationException("InventoryScopeRequired"); if (FailListing) throw new IOException("FixtureUnavailable"); return inner.ListAsync(libraries, token); }
        public Task<HostFieldObservation> ReadAsync(MediaIdentity key, string field, CancellationToken token)
        { if (Active) InsideReads++; else OutsideReads++; return inner.ReadAsync(key, field, token); }
        public Task<HostWriteOutcome> WriteAsync(HostWriteCommand command, CancellationToken token)
        { if (Active) throw new InvalidOperationException("InventoryScopeLeaked"); Writes++; return inner.WriteAsync(command, token); }
        private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    }
}
