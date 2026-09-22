using Localizer.Core;

namespace Localizer.Plugin;

internal sealed class PreferenceNameTarget(INameTarget inner, DisplayPreferenceStore preferences) : INameTarget
{
    public ValueTask<NameTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => inner.ReadAsync(key, token);
    public async ValueTask<TargetWriteResult> WriteAsync(NameWriteRequest request, CancellationToken token)
    {
        using var lease = preferences.AcquireWriteLease();
        if (preferences.Read(request.Key, DisplayField.Name).Mode == DisplayPreferenceMode.Original
            && request.ProposedName != request.ExpectedDisplayPrefix + request.ExpectedOriginalTitle)
            return TargetWriteResult.Blocked;
        return await inner.WriteAsync(request, token);
    }
}

internal sealed class PreferenceOverviewTarget(IOverviewTarget inner, DisplayPreferenceStore preferences,
    OverviewStore overviews) : IOverviewTarget
{
    public ValueTask<OverviewTargetSnapshot?> ReadAsync(MediaKey key, CancellationToken token) => inner.ReadAsync(key, token);
    public async ValueTask<TargetWriteResult> WriteAsync(OverviewWrite request, CancellationToken token)
    {
        using var lease = preferences.AcquireWriteLease();
        if (preferences.Read(request.Key, DisplayField.Overview).Mode == DisplayPreferenceMode.Original)
        {
            var source = overviews.Read(request.Key).Source;
            if (source is null || source.Hash != request.SourceHash || source.Text != request.Proposed)
                return TargetWriteResult.Blocked;
        }
        return await inner.WriteAsync(request, token);
    }
}
