namespace UstcCourseAssistant.Route2.Models;

public enum AppUpdateState
{
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Failed
}

public sealed record AppRelease(
    Version Version,
    string Tag,
    string Notes,
    Uri PageUrl,
    Uri DownloadUrl,
    long Size,
    string? Digest);

public sealed record UpdateInstallRequest(
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    string OriginalPath,
    string StagedExecutablePath,
    string DestinationPath,
    bool RunAtStartup,
    string ResultPath);

public sealed record UpdateReady(int ProcessId, string ExecutablePath, string Version);

public sealed record UpdateInstallResult(bool Success, string Message, string InstalledPath);
