using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;

namespace Jarvis.Core.Updates;

public sealed class ReleaseAsset
{
    public ReleaseAsset(string name, string apiUrl, long size)
    {
        Name = name;
        ApiUrl = apiUrl;
        Size = size;
    }

    public string Name { get; }
    public string ApiUrl { get; }
    public long Size { get; }
}

public sealed class ReleaseInfo
{
    public ReleaseInfo(Version version, string tag, string notes, IReadOnlyList<ReleaseAsset> assets)
    {
        Version = version;
        Tag = tag;
        Notes = notes;
        Assets = assets;
    }

    public Version Version { get; }
    public string Tag { get; }
    public string Notes { get; }
    public IReadOnlyList<ReleaseAsset> Assets { get; }

    public ReleaseAsset? Find(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Automatic updates from GitHub Releases on the app's code repo. Every release carries SHA256SUMS;
/// a download is used only if its SHA-256 matches, so a corrupted or swapped file is never installed.
/// Works with a private code repo too: requests use the GitHub sign-in Git already has, so teammates
/// need read access to the code repo (or the repo can be public).
/// </summary>
public sealed class Updater
{
    public const string ChecksumsFile = "SHA256SUMS";
    public const string AppAsset = "DevilDrive.exe";
    public const string AddinAsset = "DevilDrive-SolidWorksAddin.zip";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _repo;
    private string? _token;

    /// <param name="repo">owner/name of the code repo.</param>
    public Updater(string repo)
    {
        _repo = repo;
    }

    /// <summary>The code repo this build updates from (set in Directory.Build.props, baked into the build so a vault file can't redirect it).</summary>
    public static string? BuiltInRepo(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { Length: > 0 } repo ? repo : null;

    public static Version CurrentVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseVersion(informational) ?? assembly.GetName().Version ?? new Version(0, 0);
    }

    /// <summary>"v1.2.3", "1.2.3+commit" or "1.2" as a Version; null if it isn't one.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text!.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) core = core.Substring(0, cut);
        if (!Version.TryParse(core, out var version)) return null;
        // Treat missing parts as 0 so 1.2 == 1.2.0.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    /// <summary>The latest release, or null if there is none or GitHub can't be reached.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync($"https://api.github.com/repos/{_repo}/releases/latest", "application/vnd.github+json", ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = json.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version == null) return null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list))
        {
            foreach (var asset in list.EnumerateArray())
            {
                assets.Add(new ReleaseAsset(asset.GetProperty("name").GetString() ?? "", asset.GetProperty("url").GetString() ?? "",
                    asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
            }
        }
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new ReleaseInfo(version, tag, notes, assets);
    }

    /// <summary>
    /// Downloads one release file into <paramref name="folder"/> and checks it against the release's
    /// SHA256SUMS. Throws if the checksum is missing or doesn't match (the bad file is deleted).
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo release, string assetName, string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var asset = release.Find(assetName) ?? throw new PdmException($"Release {release.Tag} has no {assetName}.");
        var sumsAsset = release.Find(ChecksumsFile) ?? throw new PdmException($"Release {release.Tag} has no {ChecksumsFile}, so the download can't be checked. Not installing it.");

        var sums = ParseChecksums(Utf8(await DownloadBytesAsync(sumsAsset, ct).ConfigureAwait(false)));
        if (!sums.TryGetValue(assetName, out var expected))
        {
            throw new PdmException($"{ChecksumsFile} in release {release.Tag} has no entry for {assetName}. Not installing it.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, assetName);
        progress?.Report($"Downloading {assetName} ({asset.Size / 1048576.0:0.#} MB)\u2026");
        File.WriteAllBytes(path, await DownloadBytesAsync(asset, ct).ConfigureAwait(false));

        var actual = Sha256(path);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new PdmException($"The downloaded {assetName} doesn't match its checksum, so it wasn't installed. Try again later, or tell an admin.",
                $"expected {expected}, got {actual}");
        }
        return path;
    }

    /// <summary>
    /// The part of a release's notes worth showing in the update prompt: without the install
    /// instructions at the top (they end with "You don't need the other files") and the checksums at the end.
    /// </summary>
    public static string WhatsNew(string notes)
    {
        var text = (notes ?? "").Replace("\r\n", "\n");
        var checksums = text.IndexOf("**SHA-256 checksums**", StringComparison.Ordinal);
        if (checksums >= 0) text = text.Substring(0, checksums);
        var install = text.IndexOf("You don't need the other files", StringComparison.Ordinal);
        if (install >= 0)
        {
            var lineEnd = text.IndexOf('\n', install);
            text = lineEnd < 0 ? "" : text.Substring(lineEnd + 1);
        }
        text = text.Trim();
        return text.Length > 600 ? text.Substring(0, 600) + "\u2026" : text;
    }

    /// <summary>Lines like "&lt;sha256&gt;  &lt;file name&gt;" (sha256sum / Get-FileHash format).</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64) continue;
            var name = line.Substring(space).Trim().TrimStart('*');
            if (name.Length > 0) result[name] = line.Substring(0, 64);
        }
        return result;
    }

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
    }

    private static string Utf8(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private async Task<byte[]> DownloadBytesAsync(ReleaseAsset asset, CancellationToken ct)
    {
        using var response = await SendAsync(asset.ApiUrl, "application/octet-stream", ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode)
        {
            throw new PdmException($"Couldn't download {asset.Name}{(response != null ? $" (GitHub said {(int)response.StatusCode})" : "")}. Check your internet connection.");
        }
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage?> SendAsync(string url, string accept, CancellationToken ct)
    {
        _token ??= await GitHubCredentials.GetTokenAsync(ct).ConfigureAwait(false) ?? "";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.UserAgent.ParseAdd("DevilDrive");
        request.Headers.Accept.ParseAdd(accept);
        try
        {
            // Asset downloads redirect to a storage URL; HttpClient drops the Authorization header on that hop, as it should.
            return await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
