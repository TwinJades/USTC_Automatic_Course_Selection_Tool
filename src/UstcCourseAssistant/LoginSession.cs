namespace UstcCourseAssistant;

public static class LoginSession
{
    public static bool IsAuthenticated { get; set; }
    public static Uri? LastPage { get; set; }
    public static LoginWindow? Window { get; set; }
}
