using Storiva.Core.Activity;

namespace Storiva.Tests;

public class ActivityLogTests
{
    [Fact]
    public void Newest_entries_come_first_and_capacity_is_enforced()
    {
        var tick = 0;
        var log = new ActivityLog(capacity: 3, clock: () => new DateTimeOffset(2026, 1, 1, 0, 0, tick++, TimeSpan.Zero));
        for (var i = 1; i <= 5; i++)
        {
            log.Write($"m{i}");
        }

        Assert.Equal(["m5", "m4", "m3"], log.Entries.Select(e => e.Message));
        Assert.Equal(4, log.Entries.First().Time.Second);
    }
}
