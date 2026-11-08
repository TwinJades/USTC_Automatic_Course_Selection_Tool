using System.Text.Json;
using System.Runtime.Versioning;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Cli;

public sealed record LinuxStoredCredential(string UserName, string Password);

/// <summary>Credentials are decrypted by systemd into its service-only credential directory.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCredentialVault(ServiceInstaller installer)
{
    private const string CredentialName = "ustc-account";
    private readonly ServiceInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));

    public LinuxStoredCredential? ReadServiceCredential()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("系统凭据仅支持 Linux。 ");
        var directory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return null;
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("systemd 凭据目录不是绝对路径。 ");
        var path = Path.Combine(directory, CredentialName);
        if (!File.Exists(path)) return null;
        var credential = JsonSerializer.Deserialize<LinuxStoredCredential>(File.ReadAllText(path));
        if (credential is null || string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrEmpty(credential.Password))
            throw new InvalidDataException("系统凭据格式无效。 ");
        return credential;
    }

    public bool Exists()
    {
        ServiceInstaller.RequireRoot();
        if (File.Exists(ServiceInstaller.CredentialPath)) ServiceInstaller.RejectLink(ServiceInstaller.CredentialPath);
        return File.Exists(ServiceInstaller.CredentialPath);
    }

    public async Task SetAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        ServiceInstaller.RequireRoot();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) throw new ArgumentException("账号和密码不能为空。 ");
        await ChangeAsync(new LinuxStoredCredential(userName, password), cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default) => ChangeAsync(null, cancellationToken);

    private async Task ChangeAsync(LinuxStoredCredential? credential, CancellationToken cancellationToken)
    {
        ServiceInstaller.RequireRoot();
        await _installer.ValidateInstalledPackageAsync(cancellationToken);
        var wasRunning = await _installer.StopIfRunningIfInstalledAsync(cancellationToken);
        var directory = Path.GetDirectoryName(ServiceInstaller.CredentialPath)!;
        var temporary = Path.Combine(directory, ".ustc-course-assistant-" + Guid.NewGuid().ToString("N") + ".cred");
        string? original = null;
        var changed = false;
        var failures = new List<Exception>();
        var processUncertain = false;
        try
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ServiceInstaller.RejectLink(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (credential is not null)
            {
                var json = JsonSerializer.Serialize(credential);
                await ServiceInstaller.RunAsync("systemd-creds", ["encrypt", "--with-key=host", "--name=" + CredentialName, "-", temporary], cancellationToken, input: json, sensitive: true);
                ServiceInstaller.RejectLink(temporary);
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            if (File.Exists(ServiceInstaller.CredentialPath)) original = RecycleCredential(ServiceInstaller.CredentialPath);
            changed = true;
            if (credential is not null) File.Move(temporary, ServiceInstaller.CredentialPath);
            await _installer.RefreshUnitAsync(cancellationToken);
            if (wasRunning) await _installer.StartAsync(cancellationToken);
        }
        catch (Exception error)
        {
            failures.Add(error);
            processUncertain = error is MaintenanceProcessRunner.ProcessTerminationUncertainException;
            if (changed)
            {
                try
                {
                    if (File.Exists(ServiceInstaller.CredentialPath)) RecycleCredential(ServiceInstaller.CredentialPath);
                    if (original is not null) File.Copy(original, ServiceInstaller.CredentialPath);
                    await _installer.RefreshUnitAsync(CancellationToken.None);
                }
                catch (Exception recovery) { failures.Add(new IOException("恢复原凭据或服务配置失败。", recovery)); }
            }
            if (wasRunning)
            {
                try { await _installer.StartAsync(CancellationToken.None); }
                catch (Exception recovery) { failures.Add(new IOException("恢复原服务运行失败。", recovery)); }
            }
            // 报告延迟到清理之后，以保留全部恢复和清理错误。
        }
        finally
        {
            if (!processUncertain && File.Exists(temporary))
            {
                try { RecycleCredential(temporary); }
                catch (Exception cleanup) { failures.Add(new IOException("临时凭据回收失败，产物已保留。", cleanup)); }
            }
        }
        if (failures.Count != 0) throw new AggregateException("凭据维护失败；有关恢复和清理情况见内部异常。", failures);
    }

    private static string RecycleCredential(string path)
    {
        var root = Path.Combine(Path.GetDirectoryName(ServiceInstaller.CredentialPath)!, "ustc-course-assistant-recycle");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ServiceInstaller.RejectLink(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RecycleService(root).Recycle(path) ?? throw new IOException("凭据回收失败。 ");
    }
}
