namespace Knovara.Common;

/// <summary>A readable text file found in a scanned folder.</summary>
public sealed record ScannedFile(string FullPath, string RelativePath, string Content);

/// <summary>Result of scanning a folder: what was read, and why anything else was left out.</summary>
public sealed record FolderScanResult(IReadOnlyList<ScannedFile> Files, IReadOnlyDictionary<string, int> Skipped, bool HitLimit);

public sealed class FolderScanOptions
{
    public int MaxFiles { get; init; } = 200;
    public long MaxFileBytes { get; init; } = 1_000_000;
    public long MaxTotalBytes { get; init; } = 8_000_000;
}

/// <summary>
/// Finds the text files under a folder for attaching to an Ask context. Bounded on purpose: the files are
/// sent in one request and every one is chunked and scored, so an unbounded folder (a repo, node_modules)
/// would time out or exhaust memory rather than help.
/// </summary>
public static class FolderScanner
{
    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".cs", ".py", ".js", ".ts", ".java", ".go",
        ".yaml", ".yml", ".xml", ".html", ".htm", ".toml", ".ini", ".sql", ".log", ".rst"
    };

    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__", ".venv", "venv", "dist"
    };

    public static FolderScanResult Scan(string root, FolderScanOptions? options = null)
    {
        options ??= new FolderScanOptions();
        var files = new List<ScannedFile>();
        var skipped = new Dictionary<string, int>();
        long total = 0;
        var hitLimit = false;

        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        foreach (var path in EnumerateFiles(root))
        {
            if (!TextExtensions.Contains(Path.GetExtension(path)))
            {
                Skip("not a text file type");
                continue;
            }

            if (files.Count >= options.MaxFiles)
            {
                hitLimit = true;
                Skip($"over the {options.MaxFiles}-file limit");
                continue;
            }

            try
            {
                var size = new FileInfo(path).Length;
                if (size > options.MaxFileBytes) { Skip("file too large"); continue; }
                if (total + size > options.MaxTotalBytes) { hitLimit = true; Skip("over the total size limit"); continue; }

                var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content)) { Skip("empty"); continue; }
                if (content.Contains('\0')) { Skip("binary content"); continue; }

                total += size;
                files.Add(new ScannedFile(path, Path.GetRelativePath(root, path), content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip("unreadable");
            }
        }

        return new FolderScanResult(files, skipped, hitLimit);
    }

    // Manual walk (not SearchOption.AllDirectories) so noise directories are never descended into and one
    // unreadable subfolder does not abort the whole scan.
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) yield return file;

            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            for (var i = subdirs.Length - 1; i >= 0; i--)
            {
                if (!NoiseDirectories.Contains(Path.GetFileName(subdirs[i]))) pending.Push(subdirs[i]);
            }
        }
    }
}
