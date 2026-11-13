using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Jarvis.Core.Config;
using Jarvis.Core.Errors;
using Jarvis.Core.Updates;

namespace Jarvis.Client.Services;

/// <summary>
/// %LocalAppData%\Jarvis\app.log: what the app did and every error it showed, so a teammate's problem can be
/// diagnosed from one file instead of screenshots (the add-in has addin.log next to it). Never holds file contents or sign-ins.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new object();

    public static string FilePath => Path.Combine(AppFolders.Local, "app.log");

    /// <summary>Writes the start-up line and catches errors nothing else handles (background tasks, other threads).</summary>
    public static void Start()
    {
        Write($"---- DevilDrive {Updater.Format(Updater.CurrentVersion(Assembly.GetExecutingAssembly()))} started ({Environment.OSVersion.VersionString})");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("FATAL " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Background task failed: " + e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // Keep the previous log once, so the lead-up to a problem isn't lost when the file rolls over.
                    var old = Path.ChangeExtension(FilePath, ".old.log");
                    File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }

    /// <summary>An error the user was shown: its title, message and the technical detail behind it.</summary>
    public static void Error(PdmException error) =>
        Write($"ERROR {error.Title}: {error.Message}" + (string.IsNullOrWhiteSpace(error.Detail) ? "" : Environment.NewLine + "    " + error.Detail!.Trim().Replace("\n", "\n    ")));
}
