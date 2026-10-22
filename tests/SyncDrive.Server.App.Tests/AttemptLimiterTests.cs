using SyncDrive.Server.App.Accounts;
using SyncDrive.Server.App.Tests.Fakes;

namespace SyncDrive.Server.App.Tests;

/// <summary>Bremse für Passwortversuche (M7): Grenze je Schlüssel, festes Zeitfenster, Erfolg setzt zurück.</summary>
public sealed class AttemptLimiterTests
{
    private static readonly AttemptKey Narrow = new("user+address", 3);
    private static readonly AttemptKey Wide = new("address", 5);

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    private AttemptLimiter Limiter() => new(_clock, TimeSpan.FromMinutes(15));

    [Fact]
    public void Attempts_are_allowed_until_a_limit_is_reached_and_then_until_the_window_ends()
    {
        var limiter = Limiter();

        limiter.Failed([Narrow, Wide]);
        limiter.Failed([Narrow, Wide]);
        var beforeLimit = limiter.RetryAfter([Narrow, Wide]);
        _clock.Advance(TimeSpan.FromMinutes(5));
        limiter.Failed([Narrow, Wide]);
        var atLimit = limiter.RetryAfter([Narrow, Wide]);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var afterWindow = limiter.RetryAfter([Narrow, Wide]);

        Assert.Null(beforeLimit);
        Assert.Equal(TimeSpan.FromMinutes(10), atLimit); // das Fenster beginnt mit dem ersten Fehlversuch
        Assert.Null(afterWindow);
    }

    [Fact]
    public void Each_key_has_its_own_limit_and_the_longest_wait_counts()
    {
        var limiter = Limiter();
        var other = new AttemptKey("user+other-address", 3);

        for (var i = 0; i < 5; i++)
        {
            limiter.Failed([i < 3 ? Narrow : other, Wide]);
        }

        Assert.NotNull(limiter.RetryAfter([new AttemptKey("third-address", 3), Wide])); // die Adresse ist ausgeschöpft
        Assert.Null(limiter.RetryAfter([new AttemptKey("anything", 3)]));
    }

    [Fact]
    public void Success_resets_the_named_keys_only()
    {
        var limiter = Limiter();
        for (var i = 0; i < 3; i++)
        {
            limiter.Failed([Narrow, new AttemptKey("address", 3)]);
        }

        limiter.Succeeded([Narrow]);

        Assert.Null(limiter.RetryAfter([Narrow]));
        Assert.NotNull(limiter.RetryAfter([new AttemptKey("address", 3)]));
    }
}
