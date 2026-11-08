using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UstcCourseAssistant.Core.Update;

public sealed record PackageFile(string Path, long Length, string Sha256, bool Executable);
public sealed record PackageManifest(int FormatVersion, string Version, string Rid, string EntryPoint, IReadOnlyList<PackageFile> Files)
{
    public const string FileName = "package-manifest.json";
    private const long MaxManifestLength = 16 * 1024 * 1024;

    /// <summary>验证可信发布源的 ZIP 摘要、清单与二进制元数据，再解压到全新目录。不执行任何包内代码。</summary>
    public static PackageManifest ExtractVerified(string archive, string expectedSha256, string destination, string rid, string version)
    {
        if (!Regex.IsMatch(expectedSha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("缺少可信发布源的 SHA256 摘要。");
        if (rid is not ("win-x64" or "linux-x64") || !Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("无效的平台或版本。");
        destination = Path.GetFullPath(destination);
        RejectReparseAncestors(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("展开目录必须不存在。");
        using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("下载文件摘要不匹配。");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            bool isDirectory = entry.FullName.EndsWith('/');
            string name = isDirectory ? entry.FullName[..^1] : entry.FullName;
            ValidatePath(name);
            if (!all.Add(name)) throw new InvalidDataException("ZIP 包含重复路径。");
            int mode = (entry.ExternalAttributes >> 16) & 0xffff;
            int kind = mode & 0xf000;
            if (kind != 0 && kind != (isDirectory ? 0x4000 : 0x8000)) throw new InvalidDataException("ZIP 包含链接或特殊文件。");
            if (isDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("ZIP 目录不为空。");
                directories.Add(name);
            }
            else entries.Add(name, entry);
        }
        if (!directories.Contains("data") || !entries.TryGetValue(FileName, out var manifestEntry) || manifestEntry.Length > MaxManifestLength)
            throw new InvalidDataException("ZIP 缺少 data 目录或清单。");
        PackageManifest manifest;
        using (var stream = manifestEntry.Open())
        using (var document = JsonDocument.Parse(stream)) manifest = Parse(document.RootElement, rid, version);
        var files = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        if (entries.Count != files.Count + 1 || entries.Keys.Any(x => x != FileName && !files.ContainsKey(x)))
            throw new InvalidDataException("ZIP 文件与清单不一致。");
        var allowedDirectories = new HashSet<string>(StringComparer.Ordinal) { "data" };
        foreach (var path in files.Keys)
        {
            var parts = path.Split('/');
            for (int i = 1; i < parts.Length; i++) allowedDirectories.Add(string.Join('/', parts.Take(i)));
        }
        if (directories.Any(x => !allowedDirectories.Contains(x))) throw new InvalidDataException("ZIP 包含未知目录。");
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            var entry = entries[file.Path];
            if (entry.Length != file.Length || (((entry.ExternalAttributes >> 16) & 0x49) != 0) != file.Executable)
                throw new InvalidDataException("文件长度或可执行权限不匹配。");
            string target = Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RejectReparseAncestors(target);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                total = checked(total + count);
                if (total > file.Length) throw new InvalidDataException("解压数据超出清单大小。");
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
            }
            if (total != file.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("文件摘要不匹配。");
            output.Flush(true);
            if (!OperatingSystem.IsWindows() && file.Executable)
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        using (var source = manifestEntry.Open())
        using (var target = new FileStream(Path.Combine(destination, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { source.CopyTo(target); target.Flush(true); }
        Directory.CreateDirectory(Path.Combine(destination, "data"));
        ValidateBinary(destination, manifest);
        return manifest;
    }

    /// <summary>核验当前安装的不可变文件；data 是用户数据，不参与包完整性比较。</summary>
    public static PackageManifest ValidateInstalled(string root, string rid)
    {
        root = Path.GetFullPath(root);
        RejectReparseAncestors(root);
        string manifestPath = Path.Combine(root, FileName);
        RejectReparseAncestors(manifestPath);
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxManifestLength) throw new InvalidDataException("安装清单过大。");
        using var document = JsonDocument.Parse(stream);
        string version = document.RootElement.GetProperty("Version").GetString() ?? "";
        if (!Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)) throw new InvalidDataException("安装版本无效。");
        var manifest = Parse(document.RootElement, rid, version);
        var expected = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            RejectReparseAncestors(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative == ".update-maintenance" && OperatingSystem.IsLinux()) { RejectReparseAncestors(path); continue; }
                if (relative == "data") { RejectReparseAncestors(path); continue; }
                RejectReparseAncestors(path);
                if (Directory.Exists(path)) { Visit(path); continue; }
                if (relative == FileName) continue;
                if (!expected.TryGetValue(relative, out var item) || !actual.Add(relative)) throw new InvalidDataException("安装包含未知文件：" + relative);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != item.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(item.Sha256))) throw new InvalidDataException("安装文件不完整：" + relative);
                if (OperatingSystem.IsLinux() && ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) != item.Executable) throw new InvalidDataException("安装文件权限不匹配：" + relative);
            }
        }
        Visit(root);
        if (actual.Count != expected.Count) throw new InvalidDataException("安装缺少清单文件。");
        ValidateBinary(root, manifest);
        return manifest;
    }

    private static PackageManifest Parse(JsonElement root, string rid, string version)
    {
        Fields(root, "FormatVersion", "Version", "Rid", "EntryPoint", "Files");
        string entry = rid == "win-x64" ? "app/UstcCourseAssistant.Route2.exe" : "app/UstcCourseAssistant.Cli";
        if (root.GetProperty("FormatVersion").GetInt32() != 1 || root.GetProperty("Version").GetString() != version || root.GetProperty("Rid").GetString() != rid || root.GetProperty("EntryPoint").GetString() != entry)
            throw new InvalidDataException("清单平台、版本或入口不匹配。");
        var files = new List<PackageFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.GetProperty("Files").EnumerateArray())
        {
            Fields(item, "Path", "Length", "Sha256", "Executable");
            string path = item.GetProperty("Path").GetString() ?? throw new InvalidDataException("无效路径。");
            ValidatePath(path);
            long length = item.GetProperty("Length").GetInt64();
            string sha = item.GetProperty("Sha256").GetString() ?? "";
            bool executable = item.GetProperty("Executable").GetBoolean();
            if (length < 0 || !Regex.IsMatch(sha, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) || path.Equals(FileName, StringComparison.OrdinalIgnoreCase) || path.Split('/')[0].Equals("data", StringComparison.OrdinalIgnoreCase) || !names.Add(path))
                throw new InvalidDataException("无效或重复的清单项。");
            files.Add(new(path, length, sha, executable));
        }
        foreach (var path in names)
        {
            var parts = path.Split('/');
            for (int i = 1; i < parts.Length; i++) if (names.Contains(string.Join('/', parts.Take(i)))) throw new InvalidDataException("文件与目录冲突。");
        }
        bool Has(string path) => files.Any(x => x.Path == path);
        if (!Has(entry) || (rid == "win-x64" && !Has("USTC选课助手.cmd")) || (rid == "linux-x64" && (!Has("ustc-assistant") || !Has("service/ustc-course-assistant.service") || !Has("app/UstcCourseAssistant.Cli.dll") || !files.Single(x => x.Path == entry).Executable || !files.Single(x => x.Path == "ustc-assistant").Executable)))
            throw new InvalidDataException("清单缺少必需文件。");
        return new(1, version, rid, entry, files);
    }

    private static void Fields(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("无效 JSON 对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException("重复 JSON 属性。");
        if (!names.SetEquals(required)) throw new InvalidDataException("无效 JSON 字段。");
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Any(c => c < 32) || path.StartsWith('/')) throw new InvalidDataException("不安全的包路径。");
        foreach (string part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException("不安全的包路径。");
    }

    internal static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("路径包含符号链接或重解析点。");
    }

    private static void ValidateBinary(string root, PackageManifest manifest)
    {
        string path = Path.Combine(root, manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        var expected = System.Version.Parse(manifest.Version + ".0");
        if (manifest.Rid == "win-x64")
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Subsystem != Subsystem.WindowsGui) throw new InvalidDataException("Windows 入口架构或子系统不匹配。");
            var info = FileVersionInfo.GetVersionInfo(path);
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) != expected || new Version(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart) != expected) throw new InvalidDataException("Windows 入口版本不匹配。");
        }
        else
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[20];
            stream.ReadExactly(header);
            if (!header[..6].SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46, 2, 1 }) || header[18] != 62 || header[19] != 0) throw new InvalidDataException("Linux 入口不是 x86_64 ELF。");
            using var assembly = File.OpenRead(Path.Combine(root, "app", "UstcCourseAssistant.Cli.dll"));
            using var pe = new PEReader(assembly);
            if (!pe.HasMetadata || pe.GetMetadataReader().GetAssemblyDefinition().Version != expected) throw new InvalidDataException("Linux 程序集版本不匹配。");
        }
    }
}
