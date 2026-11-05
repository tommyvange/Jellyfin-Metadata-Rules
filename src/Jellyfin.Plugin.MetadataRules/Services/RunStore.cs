using System.Text.Json;
namespace Jellyfin.Plugin.MetadataRules.Services;

/// <summary>Plugin-owned storage: bounded reports and an atomic per-item journal, separate from Jellyfin's database.</summary>
public sealed class RunStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, RunJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    public RunStore() : this(Plugin.Instance?.StoragePath ?? throw new InvalidOperationException("Plugin not initialized.")) { }
    public RunStore(string path)
    {
        _path = path; Directory.CreateDirectory(Path.Combine(path, "jobs")); Directory.CreateDirectory(Path.Combine(path, "journal"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(path, "jobs"), "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<RunJob>(File.ReadAllText(file), JsonOptions);
                if (job is null) continue;
                if (job.State is "Running" or "Queued") { job.State = "Interrupted"; job.FinishedAt = DateTimeOffset.UtcNow; job.Error = "Server stopped before this run completed. Re-run to reconcile current facts."; Atomic(file, job); }
                _jobs[job.Id] = job;
            }
            catch (JsonException) { /* One corrupt report must not prevent startup. */ }
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(path, "journal"), "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file), JsonOptions);
                if (entry is not null && entry.Stage is "Running" or "PreferencesApplied") { entry.Stage = "Interrupted"; entry.UncertainAttempts++; entry.Message = "The previous action has an uncertain refresh outcome after restart."; Atomic(file, entry); }
            }
            catch (JsonException) { }
        }
        // Decision journals are retained until an administrator removes this plugin data.
        // Evicting live item keys would cause destructive replacement actions to repeat after restart.
    }
    private string JobFile(string id) => Path.Combine(_path, "jobs", Safe(id) + ".json");
    private string RowsFile(string id) => Path.Combine(_path, "jobs", Safe(id) + ".jsonl");
    private static string Safe(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : throw new ArgumentException("Invalid ID.");
    private static void Atomic<T>(string path, T value)
    {
        var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true);
    }
    public RunJob? Get(string id) { lock (_gate) return _jobs.GetValueOrDefault(id); }
    public IReadOnlyList<RunJob> Jobs() { lock (_gate) return _jobs.Values.OrderByDescending(x => x.StartedAt).ToArray(); }
    public void Save(RunJob job)
    {
        lock (_gate)
        {
            _jobs[job.Id] = job; Atomic(JobFile(job.Id), job);
            foreach (var stale in _jobs.Values.Where(x => x.FinishedAt is not null).OrderByDescending(x => x.StartedAt).Skip(job.Settings.HistoryLimit).ToArray())
            { _jobs.Remove(stale.Id); File.Delete(JobFile(stale.Id)); File.Delete(RowsFile(stale.Id)); }
        }
    }
    public void AddRow(RunJob job, RunRow row)
    {
        lock (_gate)
        {
            if (job.Processed <= job.Settings.ResultsLimit) File.AppendAllText(RowsFile(job.Id), JsonSerializer.Serialize(row, JsonOptions) + "\n");
            else job.ResultsTruncated = true;
        }
    }
    public IReadOnlyList<RunRow> Rows(string id)
    {
        lock (_gate)
        {
            if (!File.Exists(RowsFile(id))) return [];
            var rows = new List<RunRow>();
            foreach (var line in File.ReadLines(RowsFile(id)))
            {
                try { if (JsonSerializer.Deserialize<RunRow>(line, JsonOptions) is { } row) rows.Add(row); } catch (JsonException) { }
            }
            return rows;
        }
    }
    public JournalEntry? Journal(string id)
    {
        lock (_gate)
        {
            var path = Path.Combine(_path, "journal", Safe(id) + ".json");
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(path), JsonOptions); }
            catch (JsonException) { return new JournalEntry { ItemId = id, Stage = "Interrupted", Paused = true, Message = "Journal is unreadable; review this item before forcing another replacement." }; }
        }
    }
    public void SaveJournal(JournalEntry entry)
    {
        lock (_gate) { entry.UpdatedAt = DateTimeOffset.UtcNow; Atomic(Path.Combine(_path, "journal", Safe(entry.ItemId) + ".json"), entry); }
    }
}
