using System.IO;
using System.Text;

namespace UstcCourseAssistant.Route2.Services;

public static class AtomicFileStore
{
    public static void WriteText(string path, string content, bool preserveBackup = true)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = preserveBackup ? path + ".bak" : null;

        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
                // 某些文件系统不支持 Replace；先保留旧文件副本，再执行同卷覆盖移动。
            }

            if (backup is not null)
            {
                File.Copy(path, backup, overwrite: true);
            }

            File.Move(temporary, path, overwrite: true);
            return;
        }

        File.Move(temporary, path);
    }

    public static string ArchiveCorruptFile(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var archived = path + ".corrupt-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
        File.Move(path, archived);
        return archived;
    }
}
