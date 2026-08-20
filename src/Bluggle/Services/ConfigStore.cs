using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Bluggle.Services;

/// <summary>
/// Loads and saves config.json under %APPDATA%\Bluggle\.
///
/// Two things worth noting: saves are atomic (temp file + replace) so a crash mid-write
/// cannot leave a truncated file that fails to parse on next launch, and position saves are
/// debounced so dragging the widget does not hammer the disk on every mouse-move.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    public const string AppName = "Bluggle";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly DispatcherTimer _debounce;
    private bool _savePending;

    public ConfigStore()
    {
        Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        FilePath = Path.Combine(Directory, "config.json");

        Config = Load();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_savePending) SaveNow();
        };
    }

    public string Directory { get; }

    public string FilePath { get; }

    public AppConfig Config { get; private set; }

    /// <summary>Raised when a save fails, so the widget can flash rather than crash.</summary>
    public event EventHandler<string>? SaveFailed;

    private AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable config must never stop the app from starting. Keep the
            // bad file around under .bak so the user can look at it, then fall back to defaults.
            TryBackupBrokenFile();
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        }
        catch
        {
            // Nothing useful to do here.
        }
    }

    /// <summary>Queues a save a moment from now. Safe to call on every mouse-move.</summary>
    public void SaveDebounced()
    {
        _savePending = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Writes immediately. Used on exit and after menu actions.</summary>
    public void SaveNow()
    {
        _savePending = false;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Config, SerializerOptions));

            // File.Replace preserves the original's attributes and is atomic, but it needs the
            // destination to exist; on first write there is nothing to replace yet.
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, destinationBackupFileName: null);
            else File.Move(temp, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SaveFailed?.Invoke(this, $"Could not save settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        if (_savePending) SaveNow();
    }
}
