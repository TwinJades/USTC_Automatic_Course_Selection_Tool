using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>仅访问本项目正式 GitHub 发布的独立客户端，不使用教务 Cookie。</summary>
public sealed class GitHubUpdateService : IDisposable
{
    private const string Repository = "/TwinJades/USTC_Automatic_Course_Selection_Tool";
    private const string FilePrefix = "USTC_Automatic_Course_Selection_Tool_";
    private readonly HttpClient _http;
    public static Uri ReleasePageUrl { get; } = new("https://github.com" + Repository + "/releases/latest");
    public static Version CurrentVersion { get; } = NormalizeVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public GitHubUpdateService(Version? currentVersion = null)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("USTC-Course-Assistant/" + NormalizeVersion(currentVersion ?? CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.GetAsync(
                "https://api.github.com/repos" + Repository + "/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response, "检查 GitHub 更新");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidOperationException("GitHub latest 返回的不是正式发布版本。");
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            ValidateTag(tag);
            var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            if (!Version.TryParse(versionText, out var parsed))
                throw new InvalidOperationException($"GitHub 发布版本号无效：{tag}");
            var version = NormalizeVersion(parsed);
            if (version <= NormalizeVersion(currentVersion)) return null;
            var page = new Uri(root.GetProperty("html_url").GetString()!, UriKind.Absolute);
            ValidateRepositoryUrl(page, "/releases/tag/" + Uri.EscapeDataString(tag));
            var fileName = ExecutableName(version)[..^4] + ".zip";
            var matches = root.GetProperty("assets").EnumerateArray()
                .Where(asset => string.Equals(asset.GetProperty("name").GetString(), fileName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? $"正式发布缺少自动更新附件 {fileName}。请在 {page} 查看。"
                    : $"正式发布包含多个同名附件 {fileName}，无法安全选择。请在 {page} 查看。");
            var asset = matches[0];
            var download = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            ValidateRepositoryUrl(download, "/releases/download/" + Uri.EscapeDataString(tag) + "/" + fileName);
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0) throw new InvalidOperationException("GitHub 附件大小无效。");
            var digest = asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new AppRelease(version, tag,
                root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : string.Empty,
                page, download, size, digest);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("检查 GitHub 更新超时（20 秒）。", ex);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("GitHub 发布元数据无效：" + ex.Message, ex);
        }
    }

    /// <summary>下载并校验 ZIP，仅返回已经验证的解压后程序路径。</summary>
    public async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ValidateTag(release.Tag);
        var versionText = release.Tag.StartsWith('v') || release.Tag.StartsWith('V') ? release.Tag[1..] : release.Tag;
        if (!Version.TryParse(versionText, out var tagVersion) || NormalizeVersion(tagVersion) != NormalizeVersion(release.Version))
            throw new InvalidOperationException("发布标签与目标版本不匹配。");
        var executableName = ExecutableName(release.Version);
        var zipName = executableName[..^4] + ".zip";
        ValidateRepositoryUrl(release.PageUrl, "/releases/tag/" + Uri.EscapeDataString(release.Tag));
        ValidateRepositoryUrl(release.DownloadUrl, "/releases/download/" + Uri.EscapeDataString(release.Tag) + "/" + zipName);
        if (release.Size <= 0) throw new InvalidOperationException("下载附件大小无效。");
        var directory = Path.Combine(AppDataPaths.Root, "updates", release.Tag);
        Directory.CreateDirectory(directory);
        // 每次尝试独立保留，包括未完成或损坏的下载文件，便于核对失败原因。
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, zipName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await GetDownloadResponseAsync(release.DownloadUrl, timeout.Token).ConfigureAwait(false);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    count += read;
                    if (count > release.Size) throw new InvalidDataException("下载大小超出 GitHub 公布的附件大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress?.Report((double)count / release.Size);
                }
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                if (count != release.Size) throw new InvalidDataException($"附件大小不匹配：应为 {release.Size} 字节，实际为 {count} 字节。");
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (release.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true &&
                    !string.Equals(release.Digest[7..], actualHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载附件 SHA256 校验失败。");
            }
            var executablePath = await ValidateArchiveAsync(archivePath, release.Version, directory, timeout.Token).ConfigureAwait(false);
            progress?.Report(1);
            return executablePath;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("下载更新超时（10 分钟）。暂存文件已保留。", ex);
        }
    }

    private async Task<HttpResponseMessage> GetDownloadResponseAsync(Uri initial, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirects = 0; ; redirects++)
        {
            ValidateDownloadHost(current);
            var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (redirects >= 5 || location is null) throw new InvalidOperationException("GitHub 下载重定向次数超限或缺少 Location。");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            try { EnsureSuccess(response, "下载 GitHub 更新"); }
            catch { response.Dispose(); throw; }
            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{operation}失败：HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。" +
                (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests ? "GitHub 请求可能受到限流，请稍后手动重试。" : string.Empty), null, response.StatusCode);
    }

    private static void ValidateDownloadHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidOperationException("更新地址或重定向目标不受信任：" + uri);
    }

    private static void ValidateRepositoryUrl(Uri uri, string suffix)
    {
        ValidateDownloadHost(uri);
        if (uri.Host != "github.com" || !string.Equals(uri.AbsolutePath, Repository + suffix, StringComparison.Ordinal) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("更新地址不属于本项目的正式发布：" + uri);
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag != tag.Trim() || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            tag is "." or ".." || tag.Contains('/') || tag.Contains('\\'))
            throw new InvalidOperationException("发布标签不能作为安全的本地更新目录名。");
    }

    public static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    public static string ExecutableName(Version version) => FilePrefix + NormalizeVersion(version) + ".exe";

    /// <summary>校验真实 ZIP 的安全路径、唯一根程序和版本，只解压该程序。</summary>
    public static async Task<string> ValidateArchiveAsync(string archivePath, Version version, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var executableName = ExecutableName(version);
        var executablePath = Path.Combine(Path.GetFullPath(destinationDirectory), executableName);
        using var archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? executable = null;
        var rootExecutableCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or "..") ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新 ZIP 包含不安全路径或符号链接。");
            if (!name.Contains('/') && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) rootExecutableCount++;
            if (string.Equals(name, executableName, StringComparison.Ordinal))
            {
                if (executable is not null) throw new InvalidDataException("更新 ZIP 根目录包含重复的目标程序。");
                executable = entry;
            }
        }
        if (rootExecutableCount != 1 || executable is null || executable.Length == 0)
            throw new InvalidDataException($"更新 ZIP 根目录必须且只能包含一个程序 {executableName}。");
        Directory.CreateDirectory(destinationDirectory);
        await using (var input = executable.Open())
        await using (var output = new FileStream(executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        ValidateExecutableVersion(executablePath, version);
        return executablePath;
    }

    public static void ValidateExecutableVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != NormalizeVersion(expected))
            throw new InvalidDataException($"更新程序 FileVersion 与发布版本 {NormalizeVersion(expected)} 不一致。");
    }

    public void Dispose() => _http.Dispose();
}
