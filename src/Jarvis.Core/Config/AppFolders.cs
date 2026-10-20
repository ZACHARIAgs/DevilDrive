using System;
using System.IO;

namespace Jarvis.Core.Config;

/// <summary>
/// Where Jarvis keeps its per-user files: %AppData%\Jarvis (settings, shared by the app and the
/// add-in), %LocalAppData%\Jarvis (backups, logs) and %LocalAppData%\Programs\Jarvis (the app).
/// </summary>
public static class AppFolders
{
    public const string Name = "Jarvis";

    /// <summary>%AppData%\Jarvis</summary>
    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    /// <summary>%LocalAppData%\Jarvis</summary>
    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>%LocalAppData%\Programs\Jarvis: where Setup installs the app.</summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
}
