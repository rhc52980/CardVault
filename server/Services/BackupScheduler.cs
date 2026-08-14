namespace CardVault.Services;

/// <summary>
/// Keeps the daily backup happening on a machine that never restarts.
///
/// Backups used to be taken only at startup, so an always-on install — the Windows
/// service, the systemd unit — could run for weeks without one. The check runs
/// hourly rather than daily: the interval that decides whether a backup is owed is
/// measured from the newest file on disk, so a frequent cheap check costs a
/// directory listing and copes with the machine sleeping, hibernating or having its
/// clock moved, where a single 24-hour timer would simply drift or miss.
/// </summary>
public sealed class BackupScheduler(BackupService backups, ILogger<BackupScheduler> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Waits first: startup has already taken whatever backup was owed by the
            // time this runs, so an immediate check would only ever be a no-op.
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (backups.BackupIfDue()) log.LogInformation("Daily backup taken on schedule");
            }
            catch (Exception e)
            {
                // Never let a failed backup take the app down with it; the next hour
                // will try again, and the collection itself is untouched either way.
                log.LogError(e, "Scheduled backup failed; will retry on the next check");
            }
        }
    }
}
