namespace UstcCourseAssistant.Core.Services;

internal static class EamsHostPolicy
{
    internal static bool IsEamsHost(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase);
}
