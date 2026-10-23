namespace OView.Core;

/// <summary>
/// The one folder O-view writes to: <c>%LOCALAPPDATA%\O-view</c> on Windows and
/// <c>~/.local/share/O-view</c> on Linux, which is where .NET maps
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>. Every file O-view keeps lives
/// under it, and it is never among Claude's own files, which O-view only ever reads. Stated
/// once so that the store, the settings, the weekly-reset anchor, the panel position, the log
/// and the usage refresher's working directory cannot drift to different folders.
/// </summary>
public static class OViewDataDirectory
{
    public const string FolderName = "O-view";

    /// <summary>The folder itself.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>A file directly under <see cref="Root"/>.</summary>
    public static string File(string fileName) => Path.Combine(Root, fileName);
}
