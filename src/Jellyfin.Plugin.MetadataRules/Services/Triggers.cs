using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataRules.Services;

public sealed class NightlyTask(RunCoordinator coordinator) : IScheduledTask, IConfigurableScheduledTask
{
    public string Name => "Apply Metadata Rules";
    public string Key => "MetadataRulesNightly";
    public string Description => "Apply ordered language and country rules. Defaults to 03:00 server local time; edit the schedule here.";
    public string Category => "Metadata Rules";
    public bool IsHidden => false;
    public bool IsEnabled => Plugin.Instance?.Settings is { Enabled: true, NightlyEnabled: true };
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;
        var job = coordinator.Enqueue(new(), false, "Nightly");
        using var registration = cancellationToken.Register(() => coordinator.Cancel(job.Id));
        while (!job.Completion.Task.IsCompleted)
        { progress.Report(job.Total == 0 ? 0 : job.Processed * 100d / job.Total); await Task.WhenAny(job.Completion.Task, Task.Delay(500, cancellationToken)).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (job.State is "Cancelled" or "Interrupted") throw new OperationCanceledException("The Metadata Rules run was cancelled or interrupted.", cancellationToken);
        if (job.State == "Failed") throw new InvalidOperationException(job.Error);
        progress.Report(100);
    }
}

public sealed class PostScanTask(RunCoordinator coordinator) : ILibraryPostScanTask
{
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Settings is { Enabled: true, RunAfterScan: true }) coordinator.Enqueue(new(), false, "FullScan");
        // This callback runs while IsScanRunning is true. Never await the scan-waiting worker here.
        progress.Report(100); return Task.CompletedTask;
    }
}

public sealed class InstantService(ILibraryManager library, RunCoordinator coordinator, RunStore store, ILogger<InstantService> logger) : BackgroundService
{
    private sealed class Pending
    {
        public DateTimeOffset Due = DateTimeOffset.UtcNow.AddSeconds(4);
        public int Attempts;
        public string? JobId;
        public bool Dirty;
    }
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly ConcurrentDictionary<Guid, Guid[]> _owners = new();
    private bool Enabled => Plugin.Instance?.Settings is { Enabled: true, InstantEnabled: true, LibraryIds.Length: > 0 };
    private void Added(object? sender, ItemChangeEventArgs e)
    {
        if (!Enabled) return;
        var change = InstantItemEvent.Capture(e.Item);
        RememberAndQueueOwners(change);
        // Alternate editions are evidence for their primary item, rather than additional ordinary candidates.
        if (change.Eligible) Queue(change.PrimaryVersionId ?? change.ItemId, true);
    }
    private void Updated(object? sender, ItemChangeEventArgs e)
    {
        if (!Enabled) return;
        var change = InstantItemEvent.Capture(e.Item);
        Queue(change.ItemId, false);
        RememberAndQueueOwners(change);
        // Coalesce all updates, including those arriving during a plugin action. Keeping one dirty
        // wakeup preserves unrelated core updates and re-evaluates current facts after the action.
        // Existing unrelated item updates do not become new item runs; owners of changed episode
        // and alternate-version evidence are deliberately reconciled, even if never seen at startup.
    }
    private void Removed(object? sender, ItemChangeEventArgs e)
    {
        var change = InstantItemEvent.Capture(e.Item);
        _pending.TryRemove(change.ItemId, out var pending);
        if (pending?.JobId is { } job) coordinator.Cancel(job);
        var previous = _owners.TryRemove(change.ItemId, out var owners) ? owners : [];
        if (Enabled) foreach (var owner in previous.Concat(change.OwnerIds).Distinct()) Queue(owner, true);
    }
    private void RememberAndQueueOwners(InstantItemEvent change)
    {
        var owners = change.OwnerIds.ToArray();
        _owners.TryGetValue(change.ItemId, out var previous);
        // Reparenting invalidates both the old and the new aggregate scopes.
        foreach (var owner in owners.Concat(previous ?? []).Distinct()) Queue(owner, true);
        if (_owners.Count < 100000 || _owners.ContainsKey(change.ItemId)) _owners[change.ItemId] = owners;
    }
    private void Queue(Guid id, bool create)
    {
        if (id == Guid.Empty || _pending.Count >= 5000 && !_pending.ContainsKey(id)) return;
        Pending? pending;
        if (create) pending = _pending.GetOrAdd(id, _ => new());
        else if (!_pending.TryGetValue(id, out pending)) return;
        lock (pending) { pending.Due = DateTimeOffset.UtcNow.AddSeconds(4); pending.Dirty = true; }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        library.ItemAdded += Added; library.ItemUpdated += Updated; library.ItemRemoved += Removed;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!Enabled || library.IsScanRunning) continue;
                foreach (var (id, pending) in _pending.ToArray())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    lock (pending)
                    {
                        if (pending.JobId is { } jobId)
                        {
                            var job = store.Get(jobId);
                            if (job?.FinishedAt is null) continue;
                            var deferred = store.Rows(jobId).Any(r => r.Status is "Deferred" or "Error");
                            if ((deferred || pending.Dirty) && pending.Attempts < 6)
                            { pending.JobId = null; pending.Due = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, pending.Attempts))); pending.Dirty = false; }
                            else { _pending.TryRemove(id, out _); continue; }
                        }
                        if (pending.Due > DateTimeOffset.UtcNow) continue;
                        try
                        {
                            var item = library.GetItemById(id);
                            if (item is null) { _pending.TryRemove(id, out _); continue; }
                            var change = InstantItemEvent.Capture(item);
                            // Initial events may precede parent IDs becoming available. Resolve only here,
                            // outside event handlers, and retain detached IDs for a later removal event.
                            if (change.SeriesId == Guid.Empty && item is Episode or Season)
                            {
                                var series = item.GetParents().OfType<Series>().FirstOrDefault();
                                if (series is not null) change = change with { SeriesId = series.Id };
                            }
                            RememberAndQueueOwners(change);
                            if (change.PrimaryVersionId is not null || !change.Eligible) { _pending.TryRemove(id, out _); continue; }
                            pending.Attempts++; pending.Dirty = false;
                            pending.JobId = coordinator.Enqueue(new() { ItemIds = [id.ToString("N")] }, false, "Instant").Id;
                        }
                        catch (Exception ex) { logger.LogWarning(ex, "Instant Metadata Rules request could not be queued"); pending.Due = DateTimeOffset.UtcNow.AddSeconds(30); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { library.ItemAdded -= Added; library.ItemUpdated -= Updated; library.ItemRemoved -= Removed; }
    }
}

/// <summary>A detached event snapshot. Capturing it reads IDs only and performs no library/provider calls.</summary>
internal readonly record struct InstantItemEvent(Guid ItemId, Guid? PrimaryVersionId, Guid SeriesId, Guid SeasonId, bool Eligible)
{
    public IEnumerable<Guid> OwnerIds
    {
        get
        {
            var itemId = ItemId;
            return new[] { PrimaryVersionId ?? Guid.Empty, SeasonId, SeriesId }.Where(id => id != Guid.Empty && id != itemId).Distinct();
        }
    }
    public static InstantItemEvent Capture(BaseItem item) => new(item.Id,
        item is Video { PrimaryVersionId: { } primary } && primary != Guid.Empty && primary != item.Id ? primary : null,
        item is Episode episode ? episode.SeriesId : item is Season season ? season.SeriesId : Guid.Empty,
        item is Episode episodeItem ? episodeItem.SeasonId : Guid.Empty,
        item.GetType().Name is "Movie" or "Series" or "Season" or "Episode");
}
