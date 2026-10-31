using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-02: exercises the PowerShell entry point and Python patcher against repo-local files only.
public class ClientUriPatcherSpecificationTests
{
    private const string Target = "https://localhost:60952";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly (int Index, string Name, string Original, string Category)[] Literals =
    [
        (24853, "PortalBaseUrl", "https://www.darkorbit.com/", "core"),
        (24854, "ExternalDownloadUrl", "https://www.darkorbit.com/index.es?action=externalDownload", "core"),
        (24847, "LocalhostHttpsUrl", "https://localhost/", "loopback"),
        (24838, "AliCdnDevUrl", "https://alicdn-oss-dev.darkorbit.com/", "cdn"),
        (24839, "AliCdnProdUrl", "https://alicdn-oss-prod.darkorbit.com/", "cdn"),
        (24843, "BpSecureCdnOrigin", "https://darkorbit-22.bpsecure.com", "cdn"),
        (24844, "BpSecureClanLogoUrl", "https://darkorbit-22.bpsecure.com/do_img/global/clanLogo/", "cdn"),
        (24851, "SteamPartnerTokenUrl", "https://steam.partner.darkorbit.com/?token=", "cdn"),
    ];

    [TestCase(false)]
    [TestCase(true)]
    public void StatusApplyAndReset_PreserveDescriptorsAndRestoreExactBytes(bool patchCdn)
    {
        using var fixture = new ClientFixture();
        var initial = Run(fixture, "Status");
        Assert.That(initial.ExitCode, Is.Zero, initial.Output);
        using (var status = JsonDocument.Parse(initial.Stdout))
        {
            Assert.That(status.RootElement.GetProperty("il2cpp_header").GetProperty("version").GetInt32(), Is.EqualTo(31));
            Assert.That(status.RootElement.GetProperty("literals").GetArrayLength(), Is.EqualTo(8));
            Assert.That(status.RootElement.GetProperty("files")[0].GetProperty("backup_exists").GetBoolean(), Is.False);
        }

        var applied = Run(fixture, "Apply", patchCdn: patchCdn);
        Assert.That(applied.ExitCode, Is.Zero, applied.Output);
        using (var report = JsonDocument.Parse(applied.Stdout))
        {
            Assert.That(report.RootElement.GetProperty("patch_cdn").GetBoolean(), Is.EqualTo(patchCdn));
            Assert.That(report.RootElement.GetProperty("applied_changes").GetArrayLength(), Is.EqualTo(patchCdn ? 7 : 2));
        }

        var changed = File.ReadAllBytes(fixture.MetadataPath);
        Assert.That(changed.Length, Is.EqualTo(fixture.OriginalMetadata.Length));
        foreach (var literal in Literals)
        {
            var descriptor = 32 + literal.Index * 8;
            var originalOffset = BinaryPrimitives.ReadUInt32LittleEndian(fixture.OriginalMetadata.AsSpan(descriptor + 4, 4));
            var changedOffset = BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(descriptor + 4, 4));
            Assert.That(changedOffset, Is.EqualTo(originalOffset), literal.Name);

            var expected = ExpectedValue(literal.Index, patchCdn, literal.Original);
            var length = BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(descriptor, 4));
            var absoluteOffset = fixture.LiteralDataOffset + (int)changedOffset;
            Assert.That(length, Is.EqualTo(Encoding.UTF8.GetByteCount(expected)), literal.Name);
            Assert.That(Encoding.UTF8.GetString(changed, absoluteOffset, length), Is.EqualTo(expected), literal.Name);
            if (expected != literal.Original)
            {
                var slack = changed.AsSpan(absoluteOffset + length, Encoding.UTF8.GetByteCount(literal.Original) - length);
                Assert.That(slack.ToArray(), Is.All.Zero, literal.Name);
            }
        }

        Assert.That(File.ReadAllText(fixture.ConfigPath), Does.Contain("localHost:localhost:60952\r\n"));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(fixture.ExePath).AsSpan(0xDC, 2)), Is.EqualTo(3));
        Assert.That(File.ReadAllText(fixture.BootPath), Does.Not.Contain("nolog="));
        Assert.That(File.Exists(Path.Combine(fixture.Root, "Start-DarkOrbitConsole.cmd")), Is.True);
        Assert.That(File.ReadAllText(fixture.ConfigPath), Does.Contain("other:keep"));
        Assert.That(File.ReadAllText(fixture.UpdatePath), Does.Contain("HashDomain = https://localhost:60952/"));
        Assert.That(File.ReadAllText(fixture.UpdatePath), Does.Contain("DownloadDomain = https://localhost:60952/"));
        Assert.That(File.ReadAllText(fixture.UpdatePath), Does.Contain("Other = keep"));
        Assert.That(Hash(fixture.MetadataPath + ".orig.bak"), Is.EqualTo(fixture.MetadataHash));
        Assert.That(Hash(fixture.ConfigPath + ".orig.bak"), Is.EqualTo(fixture.ConfigHash));
        Assert.That(Hash(fixture.UpdatePath + ".orig.bak"), Is.EqualTo(fixture.UpdateHash));

        var repeated = Run(fixture, "Apply", patchCdn: patchCdn);
        Assert.That(repeated.ExitCode, Is.Zero, repeated.Output);
        Assert.That(File.ReadAllBytes(fixture.MetadataPath), Is.EqualTo(changed));

        var reset = Run(fixture, "Reset");
        Assert.That(reset.ExitCode, Is.Zero, reset.Output);
        Assert.That(Hash(fixture.MetadataPath), Is.EqualTo(fixture.MetadataHash));
        Assert.That(Hash(fixture.ConfigPath), Is.EqualTo(fixture.ConfigHash));
        Assert.That(Hash(fixture.UpdatePath), Is.EqualTo(fixture.UpdateHash));
        Assert.That(Hash(fixture.ExePath), Is.EqualTo(fixture.ExeHash));
        Assert.That(Hash(fixture.BootPath), Is.EqualTo(fixture.BootHash));
        Assert.That(File.Exists(Path.Combine(fixture.Root, "Start-DarkOrbitConsole.cmd")), Is.False);
        Assert.That(File.ReadAllBytes(fixture.MetadataPath), Is.EqualTo(fixture.OriginalMetadata));
        using var resetReport = JsonDocument.Parse(reset.Stdout);
        Assert.That(resetReport.RootElement.GetProperty("restored_files").GetArrayLength(), Is.EqualTo(5));
    }

    [Test]
    public void ShiftedLiteralIndices_AreLocatedByOriginalValue()
    {
        using var fixture = new ClientFixture(indexShift: -33);
        var result = Run(fixture, "Apply", patchCdn: true);
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var changed = File.ReadAllBytes(fixture.MetadataPath);
        foreach (var literal in Literals)
            Assert.That(fixture.ReadLiteral(changed, literal.Index - 33), Is.EqualTo(ExpectedValue(literal.Index, patchCdn: true, literal.Original)), literal.Name);
        using var report = JsonDocument.Parse(result.Stdout);
        Assert.That(report.RootElement.GetProperty("literals")[0].GetProperty("index").GetInt32(), Is.EqualTo(24820));

        Assert.That(Run(fixture, "Reset").ExitCode, Is.Zero);
        fixture.AssertOriginalFiles();
    }

    [Test]
    public void DuplicateTargetLiteral_IsRejectedBeforeBackup()
    {
        using var fixture = new ClientFixture();
        var duplicated = (byte[])fixture.OriginalMetadata.Clone();
        // Point an unused descriptor at the PortalBaseUrl bytes so the value occurs twice.
        duplicated.AsSpan(32 + 24853 * 8, 8).CopyTo(duplicated.AsSpan(32 + 1 * 8, 8));
        File.WriteAllBytes(fixture.MetadataPath, duplicated);
        var result = Run(fixture, "Apply", expectedHash: Convert.ToHexString(SHA256.HashData(duplicated)).ToLowerInvariant());
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("PortalBaseUrl (ambiguous"));
        fixture.AssertNoBackups();
    }

    [Test]
    public void OverlongUrl_IsRejectedWithoutChangingAnyClientFile()
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Apply", target: "https://" + new string('a', 70));
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("in-place slot capacity"));
        fixture.AssertOriginalFiles();
    }

    [Test]
    public void CorruptMagicAndUnsupportedVersion_AreRejectedBeforeBackup()
    {
        using var fixture = new ClientFixture();
        var corrupted = (byte[])fixture.OriginalMetadata.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupted.AsSpan(0, 4), 0);
        File.WriteAllBytes(fixture.MetadataPath, corrupted);
        var magic = Run(fixture, "Apply");
        Assert.That(magic.ExitCode, Is.Not.Zero);
        Assert.That(magic.Output, Does.Contain("Invalid IL2CPP metadata magic"));
        Assert.That(File.Exists(fixture.MetadataPath + ".orig.bak"), Is.False);
        Assert.That(File.ReadAllBytes(fixture.MetadataPath), Is.EqualTo(corrupted));

        BinaryPrimitives.WriteUInt32LittleEndian(corrupted.AsSpan(0, 4), 0xFAB11BAF);
        BinaryPrimitives.WriteUInt32LittleEndian(corrupted.AsSpan(4, 4), 30);
        File.WriteAllBytes(fixture.MetadataPath, corrupted);
        var version = Run(fixture, "Apply");
        Assert.That(version.ExitCode, Is.Not.Zero);
        Assert.That(version.Output, Does.Contain("expected 31"));
        Assert.That(File.Exists(fixture.MetadataPath + ".orig.bak"), Is.False);
    }

    [Test]
    public void IncorrectOriginalHash_RejectsApplyAndReset()
    {
        using var fixture = new ClientFixture();
        var badHash = new string('0', 64);
        var apply = Run(fixture, "Apply", expectedHash: badHash);
        Assert.That(apply.ExitCode, Is.Not.Zero);
        Assert.That(apply.Output, Does.Contain("does not match expected original SHA-256"));
        Assert.That(File.Exists(fixture.MetadataPath + ".orig.bak"), Is.False);
        fixture.AssertOriginalFiles();

        Assert.That(Run(fixture, "Apply").ExitCode, Is.Zero);
        var reset = Run(fixture, "Reset", expectedHash: badHash);
        Assert.That(reset.ExitCode, Is.Not.Zero);
        Assert.That(reset.Output, Does.Contain("SHA-256 verification failed"));
    }

    [TestCase("ftp://localhost:60952")]
    [TestCase("localhost:60952")]
    [TestCase("https://")]
    [TestCase("not a url")]
    public void InvalidTargetBaseUrl_IsRejectedBeforeAnyBackup(string target)
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Apply", target: target);
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("Invalid --target-base-url"));
        fixture.AssertNoBackups();
        fixture.AssertOriginalFiles();
    }

    [Test]
    public void TargetPathAndQuery_AreIgnoredAndAuthoritySuppliesLocalHost()
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Apply", target: "https://127.0.0.1:60952/ignored/path?q=1");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var changed = File.ReadAllBytes(fixture.MetadataPath);
        Assert.That(fixture.ReadLiteral(changed, 24853), Is.EqualTo("https://127.0.0.1:60952/"));
        Assert.That(fixture.ReadLiteral(changed, 24854), Is.EqualTo("https://127.0.0.1:60952/index.es?action=externalDownload"));
        Assert.That(fixture.ReadLiteral(changed, 24847), Is.EqualTo("https://localhost/"));
        Assert.That(File.ReadAllText(fixture.ConfigPath), Is.EqualTo("localHost:127.0.0.1:60952\r\nother:keep\r\n"));
        Assert.That(File.ReadAllText(fixture.UpdatePath), Does.StartWith("HashDomain = https://127.0.0.1:60952/\r\nDownloadDomain = https://127.0.0.1:60952/\r\n"));
    }

    [TestCase("https://abcdefghi", true)]
    [TestCase("https://abcdefghij", false)]
    public void LoopbackLiteral_IsPatchedOnlyWhenBaseWithSlashFitsEighteenBytes(string target, bool patched)
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Apply", target: target);
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var changed = File.ReadAllBytes(fixture.MetadataPath);
        Assert.That(fixture.ReadLiteral(changed, 24847), Is.EqualTo(patched ? target + "/" : "https://localhost/"));
        Assert.That(fixture.ReadLiteral(changed, 24853), Is.EqualTo(target + "/"));
        Assert.That(changed.Length, Is.EqualTo(fixture.OriginalMetadata.Length));
        using var report = JsonDocument.Parse(result.Stdout);
        Assert.That(report.RootElement.GetProperty("applied_changes").GetArrayLength(), Is.EqualTo(patched ? 3 : 2));
    }

    [TestCase("config")]
    [TestCase("update")]
    [TestCase("metadata")]
    public void MissingRequiredClientFile_RejectsApplyWithoutCreatingBackups(string missing)
    {
        using var fixture = new ClientFixture();
        File.Delete(missing switch { "config" => fixture.ConfigPath, "update" => fixture.UpdatePath, _ => fixture.MetadataPath });
        var result = Run(fixture, "Apply");
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("Required client file not found"));
        fixture.AssertNoBackups();
    }

    [Test]
    public void ConfigTxt_AppendsMissingLocalHostAndReplacesOnlyExactPrefixLines()
    {
        using var fixture = new ClientFixture();
        File.WriteAllBytes(fixture.ConfigPath, Encoding.UTF8.GetBytes("a:1\nb:2\n"));
        Assert.That(Run(fixture, "Apply").ExitCode, Is.Zero);
        // Python universal newlines: every line ending is written as CRLF on Windows.
        Assert.That(File.ReadAllText(fixture.ConfigPath), Is.EqualTo("a:1\r\nb:2\r\nlocalHost:localhost:60952\r\n"));

        File.Delete(fixture.ConfigPath + ".orig.bak");
        File.WriteAllBytes(fixture.ConfigPath, Encoding.UTF8.GetBytes("localHost:one\n localHost:indented\nlocalHost:two\r\nlocalhost:case\n"));
        Assert.That(Run(fixture, "Apply").ExitCode, Is.Zero);
        Assert.That(File.ReadAllText(fixture.ConfigPath), Is.EqualTo("localHost:localhost:60952\r\n localHost:indented\r\nlocalHost:localhost:60952\r\nlocalhost:case\r\n"));
    }

    [Test]
    public void UpdateIni_ReplacesBomPrefixedHashDomainWithoutAddingMissingKeys()
    {
        using var fixture = new ClientFixture();
        File.WriteAllBytes(fixture.UpdatePath, Encoding.UTF8.GetBytes("﻿HashDomain=https://old.example/\nOther = keep\n"));
        Assert.That(Run(fixture, "Apply").ExitCode, Is.Zero);
        var bytes = File.ReadAllBytes(fixture.UpdatePath);
        Assert.That(bytes[0], Is.Not.EqualTo(0xEF), "output is UTF-8 without BOM");
        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("HashDomain = https://localhost:60952/\r\nOther = keep\r\n"));
    }

    [Test]
    public void Status_IsReadOnlyAndDoesNotValidateTheOriginalHash()
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Status", expectedHash: new string('0', 64));
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        using var status = JsonDocument.Parse(result.Stdout);
        Assert.That(status.RootElement.GetProperty("config_txt").GetProperty("localHost").GetString(), Is.EqualTo("original.example"));
        Assert.That(status.RootElement.GetProperty("update_ini").GetProperty("HashDomain").GetString(), Is.EqualTo("https://original.example/"));
        Assert.That(status.RootElement.GetProperty("literals").EnumerateArray().Select(x => x.GetProperty("status").GetString()), Is.All.EqualTo("ORIGINAL"));
        fixture.AssertNoBackups();
        fixture.AssertOriginalFiles();
    }

    [Test]
    public void SwitchingPatchCdnOff_StartsFromBackupAndDoesNotCompound()
    {
        using var fixture = new ClientFixture();
        Assert.That(Run(fixture, "Apply", patchCdn: true).ExitCode, Is.Zero);
        Assert.That(Run(fixture, "Apply").ExitCode, Is.Zero);
        var changed = File.ReadAllBytes(fixture.MetadataPath);
        foreach (var literal in Literals)
            Assert.That(fixture.ReadLiteral(changed, literal.Index), Is.EqualTo(ExpectedValue(literal.Index, patchCdn: false, literal.Original)), literal.Name);
    }

    [Test]
    public void Reset_WithoutBackups_RestoresNothingAndLeavesFilesUnchanged()
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Reset");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        using var report = JsonDocument.Parse(result.Stdout);
        Assert.That(report.RootElement.GetProperty("restored_files").GetArrayLength(), Is.Zero);
        fixture.AssertNoBackups();
        fixture.AssertOriginalFiles();
    }

    [TestCase(-8, "incomplete")]
    [TestCase(-4, "incomplete")]
    [TestCase(1 << 24, "exceeds file bounds")]
    public void DamagedDescriptorTable_IsRejectedBeforeBackup(int sizeDelta, string message)
    {
        using var fixture = new ClientFixture();
        var damaged = (byte[])fixture.OriginalMetadata.Clone();
        var size = BinaryPrimitives.ReadInt32LittleEndian(damaged.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(12, 4), size + sizeDelta);
        File.WriteAllBytes(fixture.MetadataPath, damaged);
        var result = Run(fixture, "Apply", expectedHash: Convert.ToHexString(SHA256.HashData(damaged)).ToLowerInvariant());
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain(message));
        fixture.AssertNoBackups();
    }

    [TestCase("abc")]
    [TestCase("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void FixtureHash_MustBeSixtyFourHexDigits(string digest)
    {
        using var fixture = new ClientFixture();
        var result = Run(fixture, "Apply", expectedHash: digest);
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("64-character hexadecimal"));
        fixture.AssertNoBackups();
        fixture.AssertOriginalFiles();
    }

    [Test]
    public void FixtureHash_RequiresClientRootStrictlyInsideRepository()
    {
        using var fixture = new ClientFixture();
        var outside = Path.Combine(Path.GetTempPath(), "spec02-missing-" + Guid.NewGuid().ToString("N"));
        foreach (var root in new[] { RepoRoot, outside })
        {
            var result = Run(fixture, "Status", clientRoot: root);
            Assert.That(result.ExitCode, Is.Not.Zero, root);
            Assert.That(result.Output, Does.Contain("strictly inside the repository"), root);
        }
        Assert.That(Directory.Exists(outside), Is.False);
    }

    private static string ExpectedValue(int index, bool patchCdn, string original) => index switch
    {
        24853 => Target + "/",
        24854 => Target + "/index.es?action=externalDownload",
        24838 or 24839 when patchCdn => Target + "/",
        24843 when patchCdn => Target,
        24844 when patchCdn => Target + "/do_img/global/clanLogo/",
        24851 when patchCdn => Target + "/?token=",
        _ => original,
    };

    private static (int ExitCode, string Stdout, string Output) Run(ClientFixture fixture, string action, bool patchCdn = false, string? target = null, string? expectedHash = null, string? clientRoot = null)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(RepoRoot, "scripts", "client-testing", "Patch-ClientUris.ps1"),
            "-Action", action, "-ClientRoot", clientRoot ?? fixture.Root, "-TargetBaseUrl", target ?? Target,
            "-FixtureOriginalSha256", expectedHash ?? fixture.MetadataHash, "-AsJson",
        })
        {
            info.ArgumentList.Add(argument);
        }
        if (patchCdn) info.ArgumentList.Add("-PatchCdn");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("URI patcher exceeded 30 seconds.");
        }
        var output = stdout.GetAwaiter().GetResult();
        return (process.ExitCode, output, output + stderr.GetAwaiter().GetResult());
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class ClientFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(RepoRoot, "_reversing", "Requests", "sessions", "spec02-" + Guid.NewGuid().ToString("N"));
        public string MetadataPath => Path.Combine(Root, "DarkOrbit_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        public string ConfigPath => Path.Combine(Root, "DarkOrbit_Data", "StreamingAssets", "config.txt");
        public string UpdatePath => Path.Combine(Root, "update.ini");
        public string ExePath => Path.Combine(Root, "DarkOrbit.exe");
        public string BootPath => Path.Combine(Root, "DarkOrbit_Data", "boot.config");
        public byte[] OriginalMetadata { get; }
        public int LiteralDataOffset { get; }
        public string MetadataHash { get; }
        public string ConfigHash { get; }
        public string UpdateHash { get; }
        public string ExeHash { get; }
        public string BootHash { get; }

        public int IndexShift { get; }

        // indexShift moves every target literal to model client builds whose literal table differs.
        public ClientFixture(int indexShift = 0)
        {
            IndexShift = indexShift;
            Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            const int literalOffset = 32;
            const int literalCount = 24855;
            const int literalSize = literalCount * 8;
            LiteralDataOffset = literalOffset + literalSize;
            var literalDataSize = Literals.Sum(x => Encoding.UTF8.GetByteCount(x.Original));
            OriginalMetadata = new byte[LiteralDataOffset + literalDataSize];
            BinaryPrimitives.WriteUInt32LittleEndian(OriginalMetadata.AsSpan(0, 4), 0xFAB11BAF);
            BinaryPrimitives.WriteUInt32LittleEndian(OriginalMetadata.AsSpan(4, 4), 31);
            BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(8, 4), literalOffset);
            BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(12, 4), literalSize);
            BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(16, 4), LiteralDataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(20, 4), literalDataSize);
            var relativeOffset = 0;
            foreach (var literal in Literals)
            {
                var encoded = Encoding.UTF8.GetBytes(literal.Original);
                var descriptor = literalOffset + (literal.Index + indexShift) * 8;
                BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(descriptor, 4), encoded.Length);
                BinaryPrimitives.WriteInt32LittleEndian(OriginalMetadata.AsSpan(descriptor + 4, 4), relativeOffset);
                encoded.CopyTo(OriginalMetadata.AsSpan(LiteralDataOffset + relativeOffset));
                relativeOffset += encoded.Length;
            }
            File.WriteAllBytes(MetadataPath, OriginalMetadata);
            File.WriteAllBytes(ConfigPath, Encoding.UTF8.GetBytes("localHost:original.example\r\nother:keep\r\n"));
            File.WriteAllBytes(UpdatePath, Encoding.UTF8.GetBytes("HashDomain = https://original.example/\r\nDownloadDomain = https://original.example/\r\nOther = keep\r\n"));
            var exe = new byte[512];
            exe[0] = (byte)'M'; exe[1] = (byte)'Z';
            BinaryPrimitives.WriteInt32LittleEndian(exe.AsSpan(0x3C, 4), 0x80);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(exe.AsSpan(0x80));
            BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(0x98, 2), 0x10B);
            BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(0xDC, 2), 2);
            File.WriteAllBytes(ExePath, exe);
            File.WriteAllText(BootPath, "nolog=\r\nbuild-guid=test\r\n");
            MetadataHash = Hash(MetadataPath);
            ConfigHash = Hash(ConfigPath);
            UpdateHash = Hash(UpdatePath);
            ExeHash = Hash(ExePath);
            BootHash = Hash(BootPath);
        }

        public void AssertOriginalFiles()
        {
            Assert.That(Hash(MetadataPath), Is.EqualTo(MetadataHash));
            Assert.That(Hash(ConfigPath), Is.EqualTo(ConfigHash));
            Assert.That(Hash(UpdatePath), Is.EqualTo(UpdateHash));
            Assert.That(Hash(ExePath), Is.EqualTo(ExeHash));
            Assert.That(Hash(BootPath), Is.EqualTo(BootHash));
        }

        public void AssertNoBackups()
        {
            foreach (var path in new[] { MetadataPath, ConfigPath, UpdatePath, ExePath, BootPath })
                Assert.That(File.Exists(path + ".orig.bak"), Is.False, path);
        }

        public string ReadLiteral(byte[] metadata, int index)
        {
            var descriptor = 32 + index * 8;
            var length = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(descriptor, 4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(descriptor + 4, 4));
            return Encoding.UTF8.GetString(metadata, LiteralDataOffset + offset, length);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
