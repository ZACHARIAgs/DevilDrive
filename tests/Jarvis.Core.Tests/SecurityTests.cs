using System;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Core.Config;
using Jarvis.Core.Updates;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

public class ReleaseSignatureTests
{
    private static (string PublicKey, RSA Key) NewKey()
    {
        var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        return (Convert.ToBase64String(p.Modulus!) + "." + Convert.ToBase64String(p.Exponent!), rsa);
    }

    private static string Sign(RSA key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    [Fact]
    public void VerifySignature_AcceptsTheReleaseKeysSignature()
    {
        var (publicKey, key) = NewKey();
        var sums = Encoding.UTF8.GetBytes(new string('a', 64) + "  Jarvis.exe\n");
        Assert.True(Updater.VerifySignature(publicKey, sums, Sign(key, sums) + "\r\n"));
    }

    [Fact]
    public void VerifySignature_RejectsChangedChecksums()
    {
        var (publicKey, key) = NewKey();
        var sums = Encoding.UTF8.GetBytes(new string('a', 64) + "  Jarvis.exe\n");
        var signature = Sign(key, sums);
        sums[0] = (byte)'b';
        Assert.False(Updater.VerifySignature(publicKey, sums, signature));
    }

    [Fact]
    public void VerifySignature_RejectsAnotherKeyAndGarbage()
    {
        var (publicKey, _) = NewKey();
        var (_, other) = NewKey();
        var sums = Encoding.UTF8.GetBytes("x");
        Assert.False(Updater.VerifySignature(publicKey, sums, Sign(other, sums)));
        Assert.False(Updater.VerifySignature(publicKey, sums, "not base64!"));
        Assert.False(Updater.VerifySignature("nonsense", sums, Sign(other, sums)));
    }
}

public class GitNameTests
{
    [Theory]
    [InlineData("origin", false, true)]
    [InlineData("main", true, true)]
    [InlineData("release/2026", true, true)]
    [InlineData("release/2026", false, false)]
    [InlineData("--upload-pack=calc", false, false)]
    [InlineData("-x", true, false)]
    [InlineData("https://example.com/repo.git", false, false)]
    [InlineData(@"\\server\share\repo", false, false)]
    [InlineData("ext::cmd /c calc", false, false)]
    [InlineData("a..b", true, false)]
    [InlineData("", false, false)]
    public void IsPlainGitName_OnlyAcceptsPlainNames(string name, bool allowSlash, bool expected) =>
        Assert.Equal(expected, VaultConfig.IsPlainGitName(name, allowSlash));
}

public class BackupsTests
{
    [Fact]
    public void PruneBackups_RemovesOnlyOldDatedFolders()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-backups-" + Guid.NewGuid().ToString("N"));
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        try
        {
            foreach (var name in new[] { "2026-08-01_090000", "2026-11-09_090000", "my own folder" })
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, name, "sub"));
                var file = System.IO.Path.Combine(root, name, "sub", "part.sldprt");
                System.IO.File.WriteAllText(file, "x");
                System.IO.File.SetAttributes(file, System.IO.FileAttributes.ReadOnly);
            }

            Assert.Equal(1, FileUtil.PruneBackups(TimeSpan.FromDays(60), root, now));

            Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(root, "2026-08-01_090000")));
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(root, "2026-11-09_090000")));
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(root, "my own folder")));
        }
        finally
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
            System.IO.Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("Downloading LFS objects:  45% (5/11), 12 MB | 3.4 MB/s", "Downloading file 5 of 11 (45%, 12 MB)\u2026")]
    [InlineData("Downloading LFS objects: 100% (2/2)", "Downloading file 2 of 2 (100%)\u2026")]
    [InlineData("fetch: Fetching reference refs/remotes/origin/main", null)]
    public void DescribeLfsProgress_TurnsGitsLineIntoPlainWords(string line, string? expected) =>
        Assert.Equal(expected, VaultService.DescribeLfsProgress(line));

    [Fact]
    public void StorageUsage_SaysHowMuchOfTheAllowanceIsUsed()
    {
        Assert.Equal("101 MB of 10 GB", new StorageUsage(101L * 1048576, 300, 10).Text);
        var nearlyFull = new StorageUsage((long)(8.5 * 1073741824), 9000, 10);
        Assert.Equal("8.5 GB of 10 GB", nearlyFull.Text);
        Assert.True(nearlyFull.IsNearlyFull);
        Assert.False(new StorageUsage(1073741824, 10, 10).IsNearlyFull);
    }
}

public class FileSafetyTests
{
    [Theory]
    [InlineData("TLT-001.SLDASM", OpenRisk.Safe)]
    [InlineData("drawings/TLT-004.pdf", OpenRisk.Safe)]
    [InlineData("TLT-004.SLDPRT.exe", OpenRisk.Blocked)]
    [InlineData("readme.lnk", OpenRisk.Blocked)]
    [InlineData("setup.BAT", OpenRisk.Blocked)]
    [InlineData("part.exe.", OpenRisk.Blocked)]
    [InlineData("macro.swp", OpenRisk.Ask)]
    [InlineData("costs.xlsm", OpenRisk.Ask)]
    [InlineData("noextension", OpenRisk.Ask)]
    public void RiskOf_OnlyTreatsDataFilesAsSafe(string path, OpenRisk expected) =>
        Assert.Equal(expected, FileSafety.RiskOf(path));
}
