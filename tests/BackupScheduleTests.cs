using CardVault.Services;

namespace CardVault.Tests;

/// <summary>
/// When a backup is owed. This used to be decided once, at startup, which meant an
/// always-on server never took one; it now also runs on a timer, so the rule itself
/// is worth pinning down.
/// </summary>
public class BackupScheduleTests
{
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void With_nothing_backed_up_yet_a_backup_is_owed()
        => Assert.True(BackupService.IsDue(null, Now, Daily));

    [Fact]
    public void A_backup_from_an_hour_ago_is_not_owed_again()
        => Assert.False(BackupService.IsDue(Now.AddHours(-1), Now, Daily));

    [Fact]
    public void A_backup_just_short_of_the_interval_still_waits()
        => Assert.False(BackupService.IsDue(Now.AddHours(-23).AddMinutes(-59), Now, Daily));

    [Fact]
    public void A_backup_exactly_at_the_interval_is_owed()
        => Assert.True(BackupService.IsDue(Now.AddHours(-24), Now, Daily));

    [Fact]
    public void A_backup_from_last_week_is_owed()
        => Assert.True(BackupService.IsDue(Now.AddDays(-7), Now, Daily));

    [Fact]
    public void A_timestamp_in_the_future_is_treated_as_owed()
        // The clock went backwards. Backing up is the safe reading — waiting would
        // mean no backups at all until real time caught up with the bad stamp.
        => Assert.True(BackupService.IsDue(Now.AddDays(3), Now, Daily));
}
