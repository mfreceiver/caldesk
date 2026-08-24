using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalDesk.App.Models;

namespace CalDesk.App.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in %AppData%\CalDesk.</summary>
public sealed class SettingsService
{
    // AppSettings uses double.NaN as "unset" for window position/size (8 fields). Without
    // AllowNamedFloatingPointLiterals every Serialize of a default-ish AppSettings throws
    // ArgumentException ("infinity cannot be written as valid JSON") — Save's catch swallowed
    // it, so settings.json was silently never written at all. Load and Save must share this
    // option for the NaN literals to round-trip.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _directory;
    private readonly string _filePath;

    public SettingsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalDesk"))
    {
    }

    /// <summary>Test seam: explicit settings directory so tests can run against a real
    /// temp folder instead of the machine's %AppData%.</summary>
    public SettingsService(string directory)
    {
        _directory = directory;
        _filePath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            // CQ-012: a corrupt file (crash mid-write in the pre-atomic era, disk issues)
            // used to be silently replaced by defaults — the user's settings vanished
            // without a trace. Keep the damaged file aside so it can be recovered, then
            // fall back to defaults.
            QuarantineCorruptFile();
            Current = new AppSettings();
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            WriteAtomically(JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Non-fatal: settings are kept in-memory.
        }
    }

    /// <summary>CQ-012: write to a temp sibling then swap. A crash mid-write can only lose
    /// the temp file — settings.json is either fully old or fully new, never truncated.</summary>
    private void WriteAtomically(string json)
    {
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json);
        try
        {
            if (File.Exists(_filePath))
            {
                // File.Replace keeps the swap atomic on the same volume (and POSIX rename
                // semantics cover it on other platforms).
                File.Replace(temp, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _filePath);
            }
        }
        catch
        {
            // The swap failed (e.g. target locked): drop the orphaned temp copy so no stale
            // payload lingers next to settings.json, then rethrow into Save's non-fatal catch.
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Best effort cleanup only.
            }

            throw;
        }
    }

    /// <summary>Rename a corrupt settings.json to <c>settings.json.corrupt</c> (timestamped
    /// suffix when a previous quarantine still exists) so the user's data is recoverable.</summary>
    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var target = Path.Combine(_directory, "settings.json.corrupt");
            if (File.Exists(target))
            {
                target = Path.Combine(_directory, $"settings.json.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
            }

            File.Move(_filePath, target);
        }
        catch
        {
            // Quarantine is best effort — it must never turn the default-fallback into a crash.
        }
    }
}
