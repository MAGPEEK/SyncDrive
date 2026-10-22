using SyncDrive.Client.App.Ports;
using SyncDrive.Client.App.Sync;
using SyncDrive.Core.Paths;
using SyncDrive.Core.Results;

namespace SyncDrive.Client.App.Status;

/// <summary>
/// Statussymbol im Explorer: Nach jeder Bewertung wird der Pfad als abgeglichen markiert oder, nach einem Fehler, als nicht abgeglichen.
/// Den Zustand „in Arbeit“ braucht es nicht eigens: Schreibt jemand in eine Datei, nimmt Windows die Markierung selbst weg, und neue Dateien
/// sind bis zu ihrer Bewertung gar keine Platzhalter. Fehler beim Markieren ändern nichts am Sync; sie gehen an <paramref name="onError"/>.
/// </summary>
public sealed class PlaceholderStatusObserver(IPlaceholders placeholders, Action<RelativePath, Exception>? onError = null) : ISyncObserver
{
    public void PathStarted(RelativePath path)
    {
    }

    public void PathFinished(RelativePath path, Result<PathOutcome> result)
    {
        if (!result.IsSuccess)
        {
            Mark(path, false);
            return;
        }

        var outcome = result.Value!;
        if (outcome.Kind is not (OutcomeKind.DeletedLocal or OutcomeKind.DeletedRemote or OutcomeKind.Skipped))
        {
            Mark(outcome.Path, true); // nach einer Verschiebung ist das der neue Pfad
        }
    }

    public void Connected()
    {
    }

    public void Paused(bool paused)
    {
    }

    public void Stopped(SyncError reason)
    {
    }

    private void Mark(RelativePath path, bool inSync)
    {
        try
        {
            // Die Plattform antwortet sofort (ein Systemaufruf); warten hält die Reihenfolge je Pfad ein.
            placeholders.SetInSyncAsync(path, inSync, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            onError?.Invoke(path, e);
        }
    }
}

/// <summary>Mehrere Beobachter an einer Sitzung (Aufgabenstatus und Explorer-Symbole).</summary>
public sealed class ObserverGroup(params ISyncObserver[] observers) : ISyncObserver
{
    public void PathStarted(RelativePath path) => Each(o => o.PathStarted(path));

    public void PathFinished(RelativePath path, Result<PathOutcome> result) => Each(o => o.PathFinished(path, result));

    public void Connected() => Each(o => o.Connected());

    public void Paused(bool paused) => Each(o => o.Paused(paused));

    public void Stopped(SyncError reason) => Each(o => o.Stopped(reason));

    private void Each(Action<ISyncObserver> call)
    {
        foreach (var observer in observers)
        {
            call(observer);
        }
    }
}
