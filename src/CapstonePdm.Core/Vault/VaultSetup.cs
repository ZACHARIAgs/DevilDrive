using System.IO;

namespace CapstonePdm.Core.Vault;

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

    /// <summary>3D previews (STL meshes made by the add-in) go to Git LFS so the vault stays fast.</summary>
    public const string PreviewAttributesRule = ".pdm/preview/** filter=lfs diff=lfs merge=lfs -text";

    public static string GuardWorkflow => ReadResource("CapstonePdm.Core.VaultGuard.yml");
    public static string GuardScript => ReadResource("CapstonePdm.Core.VaultGuard.py");

    // The rest of vault-template, for a brand-new vault.
    public static string TemplateAttributes => ReadResource("CapstonePdm.Core.Template.gitattributes");
    public static string TemplateIgnore => ReadResource("CapstonePdm.Core.Template.gitignore");
    public static string TemplateConfig => ReadResource("CapstonePdm.Core.Template.pdm.json");

    public static bool SameText(string? a, string? b) => Normalize(a) == Normalize(b);

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private static string ReadResource(string name)
    {
        using var stream = typeof(VaultSetup).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
