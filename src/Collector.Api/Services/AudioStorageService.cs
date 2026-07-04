namespace Collector.Api.Services;

/// <summary>
/// Stores uploaded audio files on disk under a base directory, one subfolder per
/// tracked entity. The database only keeps a path relative to that base.
/// </summary>
public class AudioStorageService
{
    private readonly string _baseDir;

    public AudioStorageService(string baseDir)
    {
        _baseDir = baseDir;
    }

    /// <summary>Saves a stream and returns its (relativePath, sizeBytes).</summary>
    public async Task<(string RelativePath, long Size)> SaveAsync(
        long entityId, Stream content, string extension, CancellationToken ct)
    {
        var subDir = entityId.ToString();
        var dir = Path.Combine(_baseDir, subDir);
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(dir, fileName);

        await using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write))
        {
            await content.CopyToAsync(fs, ct);
        }

        var size = new FileInfo(fullPath).Length;
        // Forward-slash relative path for portability.
        return ($"{subDir}/{fileName}", size);
    }

    /// <summary>Resolves a relative path to an absolute one, guarding against traversal.</summary>
    public string GetFullPath(string relativePath)
    {
        var root = Path.GetFullPath(_baseDir);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid storage path.");
        return full;
    }

    public void Delete(string relativePath)
    {
        var full = GetFullPath(relativePath);
        if (File.Exists(full)) File.Delete(full);
    }
}
