using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Resources;

namespace Bluggle.Services;

/// <summary>
/// Plays the bundled chime that confirms a connection.
///
/// Two awkward details drive the shape of this class. The mp3 is compiled into the assembly as
/// a WPF resource, because the published build is a single self-contained exe and a loose file
/// beside it would not survive being copied somewhere else. But MediaPlayer.Open takes a Uri to
/// something the media pipeline can open -- it cannot read a Stream, and it cannot read a
/// pack:// resource either -- so the bytes are unpacked once into the temp folder and played
/// from there.
///
/// A failed chime never interrupts anything -- a missing codec or a locked-down temp folder
/// should cost the user a sound, not a connection -- but it does get written to error.log.
/// Note that MediaPlayer.Open does not throw on a file it cannot decode; it reports through the
/// MediaFailed event instead, so a try/catch around Open sees nothing at all. That is how a
/// chime that never once played managed to look like a chime that was merely inaudible.
/// </summary>
public sealed class ConnectSound : IDisposable
{
    // WAV rather than MP3 on purpose: WPF's MediaPlayer goes through the Windows Media pipeline,
    // which handles WAV everywhere and is at the mercy of installed codecs for anything else.
    private const string FileName = "sound.wav";

    private MediaPlayer? _player;
    private string? _extractedPath;
    private bool _unplayable;
    private bool _disposed;

    /// <summary>
    /// Starts the chime and returns immediately; playback runs on the media thread. Must be
    /// called from the UI thread -- MediaPlayer is a DispatcherObject and binds to the thread
    /// that creates it.
    /// </summary>
    public void Play()
    {
        if (_disposed || _unplayable) return;

        try
        {
            string path = _extractedPath ??= Extract();

            // Created on first use rather than in a field initializer, so a user who turned the
            // sound off never pays for the media stack at all.
            if (_player is null)
            {
                _player = new MediaPlayer { Volume = 1.0 };
                _player.MediaFailed += OnMediaFailed;
            }

            // Re-opening rewinds, which is what we want when two connects land close together.
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        }
        catch
        {
            // Whatever went wrong will go wrong again on the next connect too, so stop trying.
            _unplayable = true;
        }
    }

    /// <summary>
    /// The only place a decode failure ever surfaces. Records it and gives up, so a file the
    /// media stack cannot read does not retry on every single connect for the rest of the
    /// session.
    /// </summary>
    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        _unplayable = true;
        Log($"could not play {FileName}: {e.ErrorException?.Message}");
    }

    private static void Log(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ConfigStore.AppName);
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ConnectSound: {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the thing that breaks the chime.
        }
    }

    /// <summary>
    /// Writes the embedded chime to %TEMP%\Bluggle\sound.wav, reusing the copy already
    /// there when it matches. The length check is enough to catch both a truncated write and an
    /// upgrade that shipped a different chime.
    /// </summary>
    private static string Extract()
    {
        StreamResourceInfo? resource =
            Application.GetResourceStream(new Uri($"Assets/{FileName}", UriKind.Relative));

        if (resource is null)
            throw new FileNotFoundException($"{FileName} is missing from the assembly resources.");

        string directory = Path.Combine(Path.GetTempPath(), ConfigStore.AppName);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);

        using Stream source = resource.Stream;

        var existing = new FileInfo(path);
        if (existing.Exists && existing.Length == source.Length) return path;

        // Write to one side and move into place, so a crash mid-copy cannot leave a stub that
        // the length check would then happily accept forever after.
        string temp = path + ".tmp";
        using (FileStream target = File.Create(temp)) source.CopyTo(target);
        File.Move(temp, path, overwrite: true);

        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_player is not null)
        {
            _player.MediaFailed -= OnMediaFailed;
            _player.Close();
            _player = null;
        }
    }
}
