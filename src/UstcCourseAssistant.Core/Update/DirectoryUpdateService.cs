using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using UstcCourseAssistant.Core.Models;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Core.Update;

/// <summary>目录包专用 GitHub 更新源；不影响旧单文件发布客户端。</summary>
public sealed class DirectoryUpdateService : IDisposable
{
    private const string Repository = "/TwinJades/USTC_Automatic_Course_Selection_Tool";
    public sealed class CleanupPendingException(Exception inner) : IOException("更新产物回收失败，需清理后再维护。", inner);
    private const string Prefix = "USTC_Automatic_Course_Selection_Tool_";
    private readonly string root, rid;
    private readonly HttpClient http;
    public DirectoryUpdateService(string packageRoot, string runtime)
    {
        root = Path.GetFullPath(packageRoot);
        if (runtime is not ("win-x64" or "linux-x64")) throw new ArgumentException("不支持的更新平台。", nameof(runtime));
        rid = runtime;
        PackageManifest.RejectReparseAncestors(root);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("USTC-Course-Assistant-Directory-Updater/2.2.1");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync("https://api.github.com/repos" + Repository + "/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var value = document.RootElement;
        if (value.GetProperty("draft").GetBoolean() || value.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("GitHub latest 不是正式发布。");
        string tag = value.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)) throw new InvalidDataException("发布标签无效。");
        var version = Version.Parse(tag.TrimStart('v', 'V'));
        if (version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0))) return null;
        var page = new Uri(value.GetProperty("html_url").GetString()!, UriKind.Absolute);
        ValidateRepositoryUrl(page, "/releases/tag/" + Uri.EscapeDataString(tag));
        string fileName = Prefix + version + "_" + rid + ".zip";
        var matches = value.GetProperty("assets").EnumerateArray().Where(x => x.GetProperty("name").GetString() == fileName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("目录包附件缺失或不唯一，请手动下载：" + page);
        var asset = matches[0];
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
        ValidateRepositoryUrl(url, "/releases/download/" + Uri.EscapeDataString(tag) + "/" + fileName);
        long size = asset.GetProperty("size").GetInt64();
        string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (size <= 0 || !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("GitHub 未提供有效的附件大小和 SHA256 摘要，请手动下载。");
        return new AppRelease(version, tag, value.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "", page, url, size, digest);
    }

    /// <summary>调用方必须先停止产生写操作、等待已发操作、保存状态并冻结业务写入。返回后持锁直到启动 helper 并退出。</summary>
    public async Task<(PackageUpdateTransaction Transaction, PackageUpdateTransaction.Journal Journal, FileStream Lock)> DownloadAndPrepareAsync(AppRelease release, CancellationToken cancellationToken)
    {
        string tag = release.Tag;
        string version = release.Version.ToString(3);
        if (!Regex.IsMatch(tag, @"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) || tag.TrimStart('v', 'V') != version || release.Size <= 0 || release.Digest is null || !Regex.IsMatch(release.Digest, @"^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("发布元数据无效。");
        string name = Prefix + version + "_" + rid + ".zip";
        ValidateRepositoryUrl(release.PageUrl, "/releases/tag/" + Uri.EscapeDataString(tag));
        ValidateRepositoryUrl(release.DownloadUrl, "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name);
        var transaction = new PackageUpdateTransaction(root);
        var held = transaction.AcquireLock();
        string temp = transaction.ActivePath;
        bool created = false;
        try
        {
            if (Directory.Exists(temp)) throw new InvalidOperationException("更新事务尚未恢复或清理。");
            transaction.BeginDownload(rid, version);
            created = true;
            string part = Path.Combine(temp, "download.part");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await DownloadResponseAsync(release.DownloadUrl, timeout.Token);
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[65536]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total = checked(total + count);
                    if (total > release.Size) throw new InvalidDataException("下载超出附件大小。");
                    await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                if (total != release.Size) throw new InvalidDataException("下载附件大小不匹配。");
                await target.FlushAsync(timeout.Token);
            }
            string archive = Path.Combine(temp, "download.zip");
            File.Move(part, archive);
            var journal = transaction.Prepare(archive, release.Digest[7..], rid, version, downloadInActive: true);
            return (transaction, journal, held);
        }
        catch (Exception failure)
        {
            try
            {
                if (created && Directory.Exists(temp)) transaction.RecycleActive();
            }
            catch (Exception cleanupFailure) { throw new CleanupPendingException(new AggregateException(failure, cleanupFailure)); }
            finally { held.Dispose(); }
            throw;
        }
    }
    private async Task<HttpResponseMessage> DownloadResponseAsync(Uri uri, CancellationToken token)
    {
        for (int i = 0; i <= 5; i++)
        {
            ValidateHost(uri);
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (next is null || i == 5) throw new InvalidDataException("更新重定向无效。");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("更新重定向超限。");
    }
    private static void ValidateHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("更新下载地址不可信。");
    }
    private static void ValidateRepositoryUrl(Uri uri, string suffix)
    {
        ValidateHost(uri);
        if (uri.Host != "github.com" || uri.AbsolutePath != Repository + suffix || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new InvalidDataException("更新地址不属于正式发布。");
    }
    public void Dispose() => http.Dispose();
}
