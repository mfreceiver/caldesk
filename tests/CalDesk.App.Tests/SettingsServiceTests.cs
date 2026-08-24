using System.IO;
using CalDesk.App.Models;
using CalDesk.App.Services;

namespace CalDesk.App.Tests;

/// <summary>CQ-012 App-level regression: <see cref="SettingsService"/> must persist
/// settings atomically (temp sibling + swap — a crash mid-write can never leave a
/// truncated settings.json) and, when it encounters a corrupt file on load, quarantine
/// it as settings.json.corrupt instead of silently wiping the user's settings.</summary>
public sealed class SettingsServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"caldesk-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Save_Then_Load_RoundTrips_And_Leaves_No_TempFile()
    {
        var dir = TempDir();
        try
        {
            var svc = new SettingsService(dir);
            var settings = new AppSettings { DefaultCalendarId = Guid.NewGuid() };
            svc.Save(settings);

            // Atomic swap must consume its temp sibling: only settings.json remains.
            Assert.Equal(
                new[] { "settings.json" },
                Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

            // A fresh service (next app start) reads the values back.
            var reloaded = new SettingsService(dir).Load();
            Assert.Equal(settings.DefaultCalendarId, reloaded.DefaultCalendarId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_File_Is_Quarantined_And_Defaults_Returned()
    {
        var dir = TempDir();
        try
        {
            // Simulate a crash mid-write: half a JSON payload.
            const string corrupt = "{ \"Theme\": \"Da";
            File.WriteAllText(Path.Combine(dir, "settings.json"), corrupt);

            var loaded = new SettingsService(dir).Load();

            // Falls back to defaults instead of throwing…
            Assert.Null(loaded.DefaultCalendarId);
            // …and the damaged payload is kept aside for recovery, not destroyed.
            var quarantine = Path.Combine(dir, "settings.json.corrupt");
            Assert.True(File.Exists(quarantine));
            Assert.Equal(corrupt, File.ReadAllText(quarantine));
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Second_Corrupt_File_Gets_Timestamped_Quarantine()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "settings.json.corrupt"), "previous quarantine");

            var loaded = new SettingsService(dir).Load();
            Assert.Null(loaded.DefaultCalendarId);

            // The previous quarantine survives; the new one lands under a timestamped name.
            Assert.Equal("previous quarantine", File.ReadAllText(Path.Combine(dir, "settings.json.corrupt")));
            var extra = Directory.GetFiles(dir, "settings.json.corrupt-*");
            Assert.Single(extra);
            Assert.Equal("{ not json", File.ReadAllText(extra[0]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Quarantined_File_Is_Byte_Recoverable_After_New_Save()
    {
        var dir = TempDir();
        try
        {
            const string corrupt = "{ \"FontSize\": \"Inf";
            File.WriteAllText(Path.Combine(dir, "settings.json"), corrupt);

            var svc = new SettingsService(dir);
            svc.Load(); // quarantines the corrupt file, returns defaults

            // User changes a setting; the app writes a fresh, valid file.
            svc.Save(new AppSettings { DefaultCalendarId = Guid.NewGuid() });

            // The new settings.json is valid JSON (would not have been, pre-fix, if the
            // corrupt bytes had been left in place or the write had truncated).
            var fresh = new SettingsService(dir).Load();
            Assert.Equal(svc.Current.DefaultCalendarId, fresh.DefaultCalendarId);

            // And the old payload is still recoverable verbatim from quarantine.
            Assert.Equal(corrupt, File.ReadAllText(Path.Combine(dir, "settings.json.corrupt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
