namespace Localizer.Core;

/// <summary>The accepted display is independent of translation source text.</summary>
public static class TitleDisplayBaseline
{
    public static string Expected(SourceSnapshot source, string? confirmedDisplayName, RestorePoint? restorePoint)
    {
        // Applications and restores own the latest display expectation. Reconfirming source text
        // must not reset their baseline or accept an external edit over a journaled application.
        if (restorePoint is not null && restorePoint.Application.Key == source.Key
            && restorePoint.Application.SourceId == source.Id)
            return restorePoint.Consumed ? restorePoint.Application.BeforeValue : restorePoint.Application.PlannedValue;

        // Older bindings lack an explicit display observation. Preserve their strict expectation;
        // never adopt an unconfirmed current Name while reading or upgrading local state.
        return confirmedDisplayName ?? source.DisplayPrefix + source.OriginalTitle;
    }
}
