using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UstcCourseAssistant.Core.Services;

/// <summary>只回收指定对象，不递归清空回收站。调用者负责证明该对象已不再使用。</summary>
public sealed class RecycleService(string dataRoot)
{
    private readonly string _dataRoot = Path.GetFullPath(dataRoot);

    public string? Recycle(string path)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(source) && !Directory.Exists(source)) return null;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝回收符号链接或重解析点。");
        if (OperatingSystem.IsWindows())
        {
            RecycleWindows(source);
            return null;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("仅支持Windows与Linux回收。");

        var trash = Path.Combine(_dataRoot, "trash");
        if (source == _dataRoot || IsBelow(source, trash) || IsBelow(trash, source))
            throw new IOException("拒绝回收数据根或回收区及其父目录。");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        EnsurePrivateDirectory(trash);
        EnsurePrivateDirectory(files);
        EnsurePrivateDirectory(info);
        var id = Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source);
        var destination = Path.Combine(files, id);
        var infoPath = Path.Combine(info, id + ".trashinfo");
        var original = string.Join('/', source.Split('/').Select(Uri.EscapeDataString));
        var metadata = "[Trash Info]\nPath=" + original + "\nDeletionDate="
            + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n";
        using (var stream = new FileStream(infoPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            Options = FileOptions.WriteThrough
        }))
        {
            stream.Write(Encoding.UTF8.GetBytes(metadata));
            stream.Flush(true);
        }
        // 同卷移动；跨卷明确失败，不复制后永久删除源对象。
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
        return destination;
    }

    [SupportedOSPlatform("windows")]
    private static void RecycleWindows(string source)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RecycleWindowsSta(source);
            return;
        }
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { RecycleWindowsSta(source); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { Name = "Windows recycle operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [SupportedOSPlatform("windows")]
    private static void RecycleWindowsSta(string source)
    {
        const uint silent = 0x00000004;
        const uint noConfirmation = 0x00000010;
        const uint noConfirmMakeDirectory = 0x00000200;
        const uint noErrorUi = 0x00000400;
        const uint recycleOnDelete = 0x00080000;
        const uint earlyFailure = 0x00100000;
        IFileOperation? operation = null;
        nint item = 0;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            CheckShellResult(operation.SetOperationFlags(silent | noConfirmation | noConfirmMakeDirectory | noErrorUi | recycleOnDelete | earlyFailure));
            var shellItemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            CheckShellResult(SHCreateItemFromParsingName(source, 0, ref shellItemId, out item));
            CheckShellResult(operation.DeleteItem(item, 0));
            CheckShellResult(operation.PerformOperations());
            CheckShellResult(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted || Path.Exists(source))
                throw new IOException("回收未完成，原对象未确认移入回收站。");
        }
        catch (COMException ex)
        {
            throw new IOException($"Windows 回收失败（HRESULT 0x{ex.HResult:X8}），未执行永久删除回退。", ex);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void CheckShellResult(int result)
    {
        if (result < 0)
            throw new IOException($"Windows 回收失败（HRESULT 0x{result:X8}），未执行永久删除回退。");
    }

    private static bool IsBelow(string candidate, string parent) => candidate == parent
        || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void EnsurePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("回收区不允许符号链接。");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // 完整保留 Windows SDK 的 IFileOperation vtable 顺序。
    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint sink);
        [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(nint item, nint destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, nint sink);
        [PreserveSig] int MoveItems(nint items, nint destinationFolder);
        [PreserveSig] int CopyItem(nint item, nint destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, nint sink);
        [PreserveSig] int CopyItems(nint items, nint destinationFolder);
        [PreserveSig] int DeleteItem(nint item, nint sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, nint bindContext, ref Guid interfaceId, out nint item);
}
