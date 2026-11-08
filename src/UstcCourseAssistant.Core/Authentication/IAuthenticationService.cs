namespace UstcCourseAssistant.Core.Authentication;

public enum AuthenticationState { SignedOut, Authenticating, AwaitingSms, Authenticated, Failed }
public sealed record AuthenticationSnapshot(AuthenticationState State, Guid? ChallengeId, string Message, DateTimeOffset? ExpiresAt);

public interface IAuthenticationService : IDisposable
{
    AuthenticationSnapshot Snapshot { get; }
    Task<AuthenticationSnapshot> LoginAsync(string userName, string password, CancellationToken cancellationToken);
    Task<AuthenticationSnapshot> RequestSmsCodeAsync(Guid challengeId, CancellationToken cancellationToken);
    Task<AuthenticationSnapshot> SubmitSmsCodeAsync(Guid challengeId, string code, CancellationToken cancellationToken);
    Task<string> ScreenshotChallengeAsync(Guid challengeId, CancellationToken cancellationToken);
    Task<AuthenticationSnapshot> SubmitCaptchaAsync(Guid challengeId, string code, CancellationToken cancellationToken);
    void Logout();
    EamsSession GetAuthenticatedSession();
}
