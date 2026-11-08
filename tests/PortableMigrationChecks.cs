using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Core.Services;

public static class PortableMigrationChecks
{
    // 调用方传入隔离测试目录；本检查不会接触真实用户数据或永久删除文件。
    public static void Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "package");
        var source = Path.Combine(root, "old");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "automation-settings.json"), "{");
        File.WriteAllText(Path.Combine(source, "automation-settings.json.bak"), "{}");
        File.WriteAllText(Path.Combine(source, "operation-journal.json"), JsonSerializer.Serialize(new OperationJournal { Active = true, Owner = "fixture" }));
        Directory.CreateDirectory(Path.Combine(source, "browser-profile"));
        var paths = new PackagePaths(package);
        var calls = 0;
        void Recycle(string path)
        {
            Assert(Path.GetFullPath(path) == Path.GetFullPath(source), "只应将旧源目录交给注入的源回收器");
            var committed = JsonSerializer.Deserialize<PortableMigrationState>(File.ReadAllText(paths.MigrationState));
            Assert(committed?.Phase is PortableMigrationPhase.Committed or PortableMigrationPhase.CleanupPending, "源回收必须发生于验证提交之后");
            calls++;
            if (calls == 1) throw new IOException("模拟占用");
            Directory.Move(path, Path.Combine(root, "recycled-old"));
        }
        var migration = new PortableDataMigration(paths, source, Recycle);
        Assert(migration.Run().Phase == PortableMigrationPhase.CleanupPending, "回收失败应保留清理状态");
        Assert(Directory.Exists(source), "失败时源数据必须保留");
        Assert(File.ReadAllText(Path.Combine(paths.Data, "automation-settings.json")) == "{", "损坏主文件原样保留");
        Assert(File.Exists(Path.Combine(paths.Data, "automation-settings.json.bak")), "恢复副本必须保留");
        Assert(new OperationJournalStore(Path.Combine(paths.Data, "operation-journal.json")).Load().ActiveJournal is not null, "未决事务必须保留");
        Assert(!Directory.Exists(Path.Combine(paths.Data, "browser-profile")), "不得复制浏览器资料");
        Assert(migration.Run().Phase == PortableMigrationPhase.Recycled, "下次启动应重试回收");
        Assert(!Directory.Exists(source), "成功回收后源目录应消失");
        Assert(migration.Run().Phase == PortableMigrationPhase.Recycled && calls == 2, "完成后不得重复回收");

        var conflictPackage = Path.Combine(root, "conflict-package");
        var conflictSource = Path.Combine(root, "conflict-old");
        Directory.CreateDirectory(conflictPackage);
        Directory.CreateDirectory(conflictSource);
        var conflictPaths = new PackagePaths(conflictPackage);
        File.WriteAllText(Path.Combine(conflictSource, "preferences.json"), "{}");
        File.WriteAllText(Path.Combine(conflictPaths.Data, "preferences.json"), "{\"SavedAccount\":\"other\"}");
        var rejected = false;
        try { new PortableDataMigration(conflictPaths, conflictSource, _ => throw new InvalidOperationException()).Run(); }
        catch (IOException) { rejected = true; }
        Assert(rejected && Directory.Exists(conflictSource), "不同目标必须中止且保留源");

        foreach (var suffix in new[] { ".tmp-interrupted", ".corrupt-test", ".pre-2.2.1-test.json" })
        {
            var caseRoot = Path.Combine(root, "interrupted-" + suffix[1..]);
            var casePackage = Path.Combine(caseRoot, "package");
            var caseSource = Path.Combine(caseRoot, "old");
            Directory.CreateDirectory(casePackage);
            Directory.CreateDirectory(caseSource);
            var name = "automation-settings.json" + suffix;
            var sourceFile = Path.Combine(caseSource, name);
            File.WriteAllText(sourceFile, "{}");
            var casePaths = new PackagePaths(casePackage);
            var info = new FileInfo(sourceFile);
            var entry = new PortableMigrationFile(name, info.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))), info.LastWriteTimeUtc);
            File.WriteAllText(casePaths.MigrationState, JsonSerializer.Serialize(new PortableMigrationState(caseSource, PortableMigrationPhase.Verified, [entry])));
            var copying = Path.Combine(casePaths.MigrationStaging, "copying");
            Directory.CreateDirectory(copying);
            var destination = Path.Combine(casePaths.Data, name);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(destination))));
            File.WriteAllText(Path.Combine(copying, key), "partial");
            var recycled = 0;
            void RecycleFixture(string path)
            {
                var target = Path.Combine(caseRoot, "recycled-" + recycled++);
                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
            }
            Assert(new PortableDataMigration(casePaths, caseSource, RecycleFixture).Run().Phase == PortableMigrationPhase.Recycled,
                "中断复制的半成品不能导致目标冲突");
            Assert(File.ReadAllText(destination) == "{}" && recycled == 2, "应回收半成品并完整提交恢复副本");
        }

        foreach (var suffix in new[] { ".bak", ".tmp-broken" })
        {
            var caseRoot = Path.Combine(root, "invalid-" + suffix[1..]);
            var casePackage = Path.Combine(caseRoot, "package");
            var caseSource = Path.Combine(caseRoot, "old");
            Directory.CreateDirectory(casePackage);
            Directory.CreateDirectory(caseSource);
            File.WriteAllText(Path.Combine(caseSource, "automation-settings.json" + suffix), "{");
            var recycleCalled = false;
            var invalidRejected = false;
            try { new PortableDataMigration(new PackagePaths(casePackage), caseSource, _ => recycleCalled = true).Run(); }
            catch (InvalidDataException) { invalidRejected = true; }
            Assert(invalidRejected && !recycleCalled && Directory.Exists(caseSource), "缺少主文件且恢复副本均损坏时禁止回收");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
