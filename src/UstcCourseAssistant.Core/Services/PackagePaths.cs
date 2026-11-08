namespace UstcCourseAssistant.Core.Services;

/// <summary>显式包根派生的路径；不会读取工作目录或 AppData。</summary>
public sealed class PackagePaths
{
    public PackagePaths(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || !Path.IsPathFullyQualified(packageRoot))
            throw new ArgumentException("必须提供绝对包根路径。", nameof(packageRoot));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        EnsureNoReparsePoints(Root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("包根目录不存在。");
        Data = Path.Combine(Root, "data");
        EnsureNoReparsePoints(Data);
        Directory.CreateDirectory(Data);
        EnsureNoReparsePoints(Data);
    }

    public string Root { get; }
    public string Data { get; }
    public string MigrationState => Path.Combine(Data, "migration-state.json");
    public string MigrationStaging => Path.Combine(Data, "migration");

    internal static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("路径没有根目录。");
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径包含符号链接或重解析点。 ");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
