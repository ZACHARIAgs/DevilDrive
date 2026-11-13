using System.IO;

namespace Jarvis.Core.Vault;

/// <summary>
/// Files Jarvis keeps in every vault besides the user's own: the GitHub vault guard and the
/// .gitattributes rule that stores 3D previews with Git LFS. Admins install/repair them with
/// Update Vault Setup; the app checks they're intact on every refresh.
/// </summary>
internal static class VaultSetup
{
    public const string GuardWorkflowPath = ".github/workflows/vault-guard.yml";
    public const string GuardScriptPath = ".github/vault-guard/guard.py";
    public const string AttributesPath = ".gitattributes";
    /// <summary>The vault's GitHub page: how new teammates get Jarvis and join (GitHub shows a README in .github/ too, and Jarvis hides that folder).</summary>
    public const string ReadmePath = ".github/README.md";

    /// <summary>The GitHub page's text for a vault, with the download link of the Jarvis this is.</summary>
    public static string Readme(string vaultName, string? localPath) =>
        VaultInvites.Readme(vaultName, localPath, VaultInvites.InstallLink(Updates.Updater.BuiltInRepo(typeof(VaultSetup).Assembly)));

    /// <summary>3D previews (STL meshes made by the add-in) go to Git LFS so the vault stays fast.</summary>
    public const string PreviewAttributesRule = ".pdm/preview/** filter=lfs diff=lfs merge=lfs -text";

    public static string GuardWorkflow => ReadResource("Jarvis.Core.VaultGuard.yml");
    public static string GuardScript => ReadResource("Jarvis.Core.VaultGuard.py");

    // The rest of vault-template, for a brand-new vault.
    public static string TemplateAttributes => ReadResource("Jarvis.Core.Template.gitattributes");
    public static string TemplateIgnore => ReadResource("Jarvis.Core.Template.gitignore");
    public static string TemplateConfig => ReadResource("Jarvis.Core.Template.pdm.json");

    public static bool SameText(string? a, string? b) => Normalize(a) == Normalize(b);

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static string ReadResource(string name)
    {
        using var stream = typeof(VaultSetup).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
