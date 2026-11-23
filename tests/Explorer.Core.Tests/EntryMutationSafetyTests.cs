using Explorer.Core.Security;

namespace Explorer.Core.Tests;

public sealed class EntryMutationSafetyTests
{
    [Fact]
    public void ProtectsDriveAndShareRootsAndAncestorsWithoutTouchingAnyFiles()
    {
        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        var share = Path.Combine(drive, "WME-Synthetic", "Shared");
        var privateFolder = Path.Combine(drive, "WME-Synthetic", "Private");
        var policy = new PathAccessPolicy([new ShareRoot(drive), new ShareRoot(share)], [privateFolder]);

        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeEntryMutation(drive));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeEntryMutation(share));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeEntryMutation(Path.GetDirectoryName(share)!));
        Assert.False(policy.IsAllowed(Path.Combine(privateFolder, "secrets.txt")));
        var allowed = Path.Combine(share, "document.txt");
        Assert.Equal(allowed, policy.AuthorizeEntryMutation(allowed));
    }

    [Fact]
    public void AncestorOfPrivateFolderCannotBeRemovedEvenWithoutAnotherShareRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WME-Synthetic-{Guid.NewGuid():N}");
        var parent = Path.Combine(root, "User");
        var protectedChild = Path.Combine(parent, "Private");
        var policy = new PathAccessPolicy([new ShareRoot(root)], [protectedChild]);

        Assert.True(policy.IsAllowed(parent, write: true));
        Assert.Throws<UnauthorizedAccessException>(() => policy.AuthorizeEntryMutation(parent));
        var allowed = Path.Combine(parent, "ordinary.txt");
        Assert.Equal(allowed, policy.AuthorizeEntryMutation(allowed));
    }
}
