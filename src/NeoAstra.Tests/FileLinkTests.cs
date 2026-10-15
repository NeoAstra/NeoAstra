// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

namespace NeoAstra.Tests;

[TestClass]
public sealed class FileLinkTests
{
    [TestMethod]
    public void IsLink_ReportsLinksOfEveryKindAndNothingElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "neoastra-links-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "directory");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "content");
            Assert.IsFalse(NeoFileLinks.IsLink(file));
            Assert.IsFalse(NeoFileLinks.IsLink(directory));
            Assert.IsFalse(NeoFileLinks.IsLink(root));
            Assert.IsFalse(NeoFileLinks.IsLink(Path.GetPathRoot(root)!));
            Assert.IsFalse(NeoFileLinks.IsLink(Path.GetDirectoryName(root)!));

            var fileLink = Path.Combine(root, "file-link");
            var directoryLink = Path.Combine(root, "directory-link");
            var danglingLink = Path.Combine(root, "dangling-link");
            try
            {
                File.CreateSymbolicLink(fileLink, file);
                Directory.CreateSymbolicLink(directoryLink, directory);
                File.CreateSymbolicLink(danglingLink, Path.Combine(root, "missing"));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
            }

            Assert.IsTrue(NeoFileLinks.IsLink(fileLink));
            Assert.IsTrue(NeoFileLinks.IsLink(directoryLink));
            Assert.IsTrue(NeoFileLinks.IsLink(danglingLink));
            Assert.IsFalse(NeoFileLinks.IsLink(Path.Combine(directoryLink, "..")), "A path that merely contains a segment is not itself a link.");

            // A trailing separator names the same entry; File.GetAttributes ignores it, so the link must still be seen.
            var separator = Path.DirectorySeparatorChar.ToString();
            Assert.IsTrue(NeoFileLinks.IsLink(directoryLink + separator));
            Assert.IsTrue(NeoFileLinks.IsLink(directoryLink + separator + separator));
            Assert.IsFalse(NeoFileLinks.IsLink(directory + separator));
            Assert.IsFalse(NeoFileLinks.IsLink(Path.GetPathRoot(root)!));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DirectoryProvider_RejectsALinkedRootWhateverTheTrailingSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), "neoastra-link-root-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
            }

            foreach (var candidate in new[] { link, link + Path.DirectorySeparatorChar })
            {
                Assert.Throws<ArgumentException>(() => new NeoDirectoryResourceProvider(candidate, followSymbolicLinks: false));
                Assert.IsTrue(new NeoDirectoryResourceProvider(candidate, followSymbolicLinks: true).FollowSymbolicLinks);
            }

            Assert.IsFalse(new NeoDirectoryResourceProvider(target + Path.DirectorySeparatorChar, followSymbolicLinks: false).FollowSymbolicLinks);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void IsLink_ThrowsForAPathThatDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "neoastra-missing-" + Guid.NewGuid().ToString("N"));
        var exception = Assert.Throws<FileNotFoundException>(() => NeoFileLinks.IsLink(missing));
        Assert.AreEqual(missing, exception.FileName);
    }

    [TestMethod]
    public void IsLink_WalksFromTheFileSystemRootLikeTheAssetProviders()
    {
        // The packaged-asset check walks every segment from the root; entries that the process does not own must not throw.
        var current = Path.GetPathRoot(AppContext.BaseDirectory)!;
        Assert.IsFalse(NeoFileLinks.IsLink(current));
        foreach (var segment in AppContext.BaseDirectory[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            _ = NeoFileLinks.IsLink(current);
        }
    }
}
