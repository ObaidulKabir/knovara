using System;
using System.IO;
using System.Linq;
using Knovara.Common;
using Xunit;

namespace Knovara.Common.Tests;

public sealed class FolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knovara-scan-" + Guid.NewGuid().ToString("n"));

    public FolderScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Scan_ReadsTextFilesRecursivelyWithRelativePaths()
    {
        Write("a.md", "alpha");
        Write(Path.Combine("sub", "b.txt"), "beta");

        var result = FolderScanner.Scan(_root);

        Assert.Equal(["a.md", Path.Combine("sub", "b.txt")], result.Files.Select(f => f.RelativePath).Order().ToArray());
        Assert.False(result.HitLimit);
    }

    [Fact]
    public void Scan_SkipsNoiseDirectoriesNonTextEmptyAndBinary()
    {
        Write("keep.md", "keep");
        Write(Path.Combine("node_modules", "x.md"), "noise");
        Write(Path.Combine(".git", "y.txt"), "noise");
        Write("image.png", "not text type");
        Write("empty.txt", "   ");
        Write("bin.txt", "ab\0cd");

        var result = FolderScanner.Scan(_root);

        Assert.Single(result.Files);
        Assert.Equal("keep.md", result.Files[0].RelativePath);
        Assert.Equal(1, result.Skipped["not a text file type"]);
        Assert.Equal(1, result.Skipped["empty"]);
        Assert.Equal(1, result.Skipped["binary content"]);
    }

    [Fact]
    public void Scan_EnforcesFileCountAndSizeLimits()
    {
        for (var i = 0; i < 5; i++) Write($"f{i}.txt", "x");
        Write("big.txt", new string('y', 100));

        var result = FolderScanner.Scan(_root, new FolderScanOptions { MaxFiles = 3, MaxFileBytes = 50 });

        Assert.Equal(3, result.Files.Count);
        Assert.True(result.HitLimit);
        Assert.Contains("file too large", result.Skipped.Keys);
    }

    [Fact]
    public void Scan_EnforcesTotalSizeLimit()
    {
        Write("a.txt", new string('a', 30));
        Write("b.txt", new string('b', 30));

        var result = FolderScanner.Scan(_root, new FolderScanOptions { MaxTotalBytes = 40 });

        Assert.Single(result.Files);
        Assert.True(result.HitLimit);
    }
}
