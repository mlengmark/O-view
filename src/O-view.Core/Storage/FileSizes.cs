namespace OView.Core.Storage;

/// <summary>Sizes of files that may be missing, locked or unreadable, for diagnostics that must not throw.</summary>
internal static class FileSizes
{
    /// <summary>
    /// The size of <paramref name="path"/> in bytes, or <paramref name="missing"/> when the
    /// file is absent or cannot be inspected.
    /// </summary>
    public static long Of(string path, long missing = 0)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return missing;
        }
    }
}
