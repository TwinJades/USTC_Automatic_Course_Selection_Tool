using UstcCourseAssistant.Core.Update;

string Value(string option)
{
    int i = Array.IndexOf(args, option);
    if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + option);
    return args[i + 1];
}
string root = Value("--package-root");
var transaction = new PackageUpdateTransaction(root);
try
{
if (Array.IndexOf(args, "--prepare") >= 0)
{
    using var updateLock = transaction.AcquireLock();
    var journal = transaction.Prepare(Value("--archive"), Value("--sha256"), "win-x64", "3.0.1");
    transaction.StartHelper(journal);
    await Task.Delay(1500);
    return;
}
if (Array.IndexOf(args, "--fault") >= 0)
{
    using var updateLock = transaction.AcquireLock();
    if (Value("--fault") == "downloading") transaction.BeginDownload("win-x64", "3.0.1");
    else transaction.Prepare(Value("--archive"), Value("--sha256"), "win-x64", "3.0.1");
    return;
}
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(root, "data", "smoke-error.log"), ex.ToString());
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
