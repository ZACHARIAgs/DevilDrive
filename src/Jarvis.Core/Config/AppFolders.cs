using System;
using System.IO;

namespace Jarvis.Core.Config;

/// <summary>
/// Where DevilDrive keeps its per-user files: %AppData%\DevilDrive (settings, shared by the app and the
/// add-in), %LocalAppData%\DevilDrive (backups, logs) and %LocalAppData%\Programs\DevilDrive (the app).
/// </summary>
public static class AppFolders
{
    public const string Name = "DevilDrive";

    /// <summary>%AppData%\DevilDrive</summary>
    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>%LocalAppData%\DevilDrive</summary>
    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>%LocalAppData%\Programs\DevilDrive: where Setup installs the app.</summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
}
