using UstcCourseAssistant.Core.Authentication;

internal static class LoginFieldsChecks
{
    internal static void Run()
    {
        // Synthetic parser fixtures, not recordings of an authentication response.
        var fields = LoginFields.Parse("""
            <div id="login-page-flowkey">  flow<span>&amp;<b>key</b></span>
            </div>
            <span id="login-croypto">first<script>ignored</script><style>ignored</style><i>second</i></span>
            <INPUT TYPE="HIDDEN" NAME="riskSystemSwitch" VALUE="USTC">
            <input type="hidden" id="targetSystem" name="targetSystem" value="one&amp;two">
            <input type="text" id="siteId" value="not-hidden">
            <div id="personal-data">private</div>
            <script>const fake = '<input type="hidden" id="siteId" value="fake">';</script>
            <style>.fake { content: '<p id="siteId">fake</p>'; }</style>
            """);
        Assert(fields["login-page-flowkey"] == "flow&key", "nested text and entities");
        Assert(fields["login-croypto"] == "firstsecond", "script and style text excluded");
        Assert(fields["riskSystemSwitch"] == "USTC", "hidden input name and uppercase attributes");
        Assert(fields["targetSystem"] == "one&two", "input entities and identical id/name");
        Assert(!fields.ContainsKey("siteId") && !fields.ContainsKey("personal-data"), "ignore non-hidden inputs, scripts and unknown fields");
        Assert(LoginFields.Parse("<p id='siteId'>same</p><input type='hidden' name='siteId' value='same'>")["siteId"] == "same", "identical duplicates accepted");
        ExpectConflict("<p id='siteId'></p><p id='siteId'>value</p>");
        ExpectConflict("<input type='hidden' id='siteId' value='first'><input type='hidden' name='siteId' value='second'>");
        Console.WriteLine("[通过] 登录字段 HTML 解析检查");
    }

    private static void ExpectConflict(string html)
    {
        try { LoginFields.Parse(html); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Conflicting login fields must fail.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
