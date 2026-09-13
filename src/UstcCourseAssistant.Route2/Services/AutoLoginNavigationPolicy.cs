namespace UstcCourseAssistant.Route2.Services;

public static class AutoLoginNavigationPolicy
{
    public static int MaximumPageProbeAttempts => 15;
    public static readonly TimeSpan PageProbeDelay = TimeSpan.FromMilliseconds(400);

    public static bool IsTeachingLoginEntry(Uri? uri) =>
        uri is not null
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase);

    public static bool IsIdentityProvider(Uri? uri) =>
        uri is not null
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("id.ustc.edu.cn", StringComparison.OrdinalIgnoreCase);
}
