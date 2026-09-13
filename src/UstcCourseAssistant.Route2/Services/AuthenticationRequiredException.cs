namespace UstcCourseAssistant.Route2.Services;

public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException()
        : base("当前教务会话无效，需要由用户重新完成认证。")
    {
    }
}
