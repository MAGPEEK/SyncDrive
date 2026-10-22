namespace SyncDrive.Server.App.Tests.Fakes;

/// <summary>Uhr, die nur weiterläuft, wenn der Test es sagt.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
