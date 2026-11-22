using Explorer.Core.Security;
using System.Runtime.InteropServices;
using System.Text;

namespace Explorer.Core.Tests;

public sealed class PathAccessPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"WME-Test-Ärger mit Leerzeichen-{Guid.NewGuid():N}");

    public PathAccessPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AllowsPathInsideSharedRootWithUnicodeAndSpaces()
    {
        var target = Path.Combine(_root, "Ordner für Grüße", "Datei äöü.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "ok");
        var policy = new PathAccessPolicy([new ShareRoot(_root)]);

        Assert.Equal(target, policy.Authorize(target));
        Assert.True(policy.IsAllowed(target));
    }

    [Fact]
    public void RejectsTraversalOutsideSharedRoot()
    {
        var policy = new PathAccessPolicy([new ShareRoot(_root)]);
        var outside = Path.Combine(_root, "..", "outside.txt");

        Assert.Throws<UnauthorizedAccessException>(() => policy.Authorize(outside));
        Assert.False(policy.IsAllowed(outside));
    }

    [Fact]
    public void UsesTheMostSpecificRootForReadOnlySubfolders()
    {
        var protectedFolder = Path.Combine(_root, "Windows");
        Directory.CreateDirectory(protectedFolder);
        var policy = new PathAccessPolicy([
            new ShareRoot(_root, ReadOnly: false),
            new ShareRoot(protectedFolder, ReadOnly: true)
        ]);

        Assert.Equal(Path.Combine(_root, "ordinary.txt"), policy.Authorize(Path.Combine(_root, "ordinary.txt"), write: true));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Authorize(Path.Combine(protectedFolder, "system.ini"), write: true));
        Assert.True(policy.IsAllowed(Path.Combine(protectedFolder, "system.ini")));
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("nul")]
    [InlineData("LPT1.log")]
    public void RejectsReservedWindowsDeviceNames(string name)
    {
        var policy = new PathAccessPolicy([new ShareRoot(_root)]);

        Assert.Throws<ArgumentException>(() => policy.Authorize(Path.Combine(_root, name)));
    }

    [Fact]
    public void RejectsSiblingThatOnlySharesTheRootPrefix()
    {
        var sibling = _root + "-outside";
        var policy = new PathAccessPolicy([new ShareRoot(_root)]);

        Assert.Throws<UnauthorizedAccessException>(() => policy.Authorize(sibling));
    }

    [Fact]
    public void AddsTheWindowsLongPathPrefixWhenNeeded()
    {
        var longPath = Path.Combine(_root, new string('x', 260), "item.txt");
        var policy = new PathAccessPolicy([new ShareRoot(_root)]);

        Assert.StartsWith("\\\\?\\", policy.ToLongPath(longPath), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizesExistingShortPathAliasesToTheLongPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longPath = Path.GetFullPath(_root);
        var buffer = new StringBuilder(32768);
        var length = GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) return;
        var shortPath = buffer.ToString();
        if (string.Equals(shortPath, longPath, StringComparison.OrdinalIgnoreCase)) return;

        Assert.Equal(PathAccessPolicy.NormalizeAbsolute(longPath), PathAccessPolicy.NormalizeAbsolute(shortPath));
    }

    [Fact]
    public void AllowsCloudReparseFoldersWhenConfiguredForIntegrationTesting()
    {
        // Opt in against a real provider without creating/hydrating cloud files.
        // Cloud placeholders cannot be made with CreateSymbolicLink.
        var cloudFolder = Environment.GetEnvironmentVariable("WME_TEST_CLOUD_FOLDER");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(cloudFolder)) return;

        var normalized = PathAccessPolicy.NormalizeAbsolute(cloudFolder);
        Assert.True(Directory.Exists(normalized));
        Assert.True((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0);
        Assert.Null(new DirectoryInfo(normalized).ResolveLinkTarget(returnFinalTarget: true));
        var policy = new PathAccessPolicy([new ShareRoot(Path.GetPathRoot(normalized)!)]);

        Assert.Equal(normalized, policy.Authorize(normalized));
        Assert.Equal(normalized, policy.Authorize(normalized, write: true));
        Assert.Equal(Path.Combine(normalized, "WME-cloud-policy-probe.txt"),
            policy.Authorize(Path.Combine(normalized, "WME-cloud-policy-probe.txt"), write: true));
    }

    [Fact]
    public void StillRejectsSymbolicLinksOutsideTheSharedRoots()
    {
        var target = Path.Combine(Path.GetTempPath(), $"WME-Link-Outside-{Guid.NewGuid():N}");
        var link = Path.Combine(_root, "outside-link");
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (UnauthorizedAccessException) { return; } // Windows needs Developer Mode or link privilege.
            var policy = new PathAccessPolicy([new ShareRoot(_root)]);

            Assert.Throws<UnauthorizedAccessException>(() => policy.Authorize(link));
            Assert.False(policy.IsAllowed(Path.Combine(link, "child.txt"), write: true));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(target);
        }
    }

    [Fact]
    public void StillHonorsTheReadOnlyTargetOfASymbolicLink()
    {
        var target = Path.Combine(_root, "protected");
        var link = Path.Combine(_root, "protected-link");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (UnauthorizedAccessException) { return; }
        var policy = new PathAccessPolicy([new ShareRoot(_root), new ShareRoot(target, ReadOnly: true)]);

        Assert.Equal(target, policy.Authorize(link));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Authorize(link, write: true));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
