using SyncDrive.Contracts;
using SyncDrive.Server.App.Policies;

namespace SyncDrive.Server.App.Tests;

/// <summary>Höchstdauer von Links in Kalendertagen, unabhängig von Uhrzeit, Zeitzone und Zeitumstellung.</summary>
public sealed class PolicyServiceTests
{
    private sealed class Store(SharingPolicies policies) : IPolicyStore
    {
        public Task<SharingPolicies?> LoadAsync(CancellationToken ct) => Task.FromResult<SharingPolicies?>(policies);

        public Task SaveAsync(SharingPolicies policies, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_last_allowed_day_counts_to_its_end_even_across_the_change_to_winter_time()
    {
        var service = new PolicyService(new Store(new SharingPolicies(LinkMaxDays: 7)));
        // 23.10.2026, 00:02 in Mitteleuropa (Sommerzeit); am 25.10. wird auf Winterzeit umgestellt
        var now = new DateTimeOffset(2026, 10, 23, 0, 2, 0, TimeSpan.FromHours(2));
        var endOfLastDay = new DateTimeOffset(2026, 10, 30, 23, 59, 59, TimeSpan.FromHours(1));

        var lastDay = await service.CheckLinkAsync(LinkKinds.Share, hasPassword: false, endOfLastDay, now, default);
        var dayAfter = await service.CheckLinkAsync(LinkKinds.Share, hasPassword: false, endOfLastDay.AddDays(1), now, default);

        Assert.Null(lastDay);
        Assert.NotNull(dayAfter);
    }
}
