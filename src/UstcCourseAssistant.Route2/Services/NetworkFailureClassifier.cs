using System.IO;
using System.Net.Http;

namespace UstcCourseAssistant.Route2.Services;

public static class NetworkFailureClassifier
{
    public static bool IsTransientReadFailure(Exception exception, bool cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AuthenticationRequiredException or System.Text.Json.JsonException)
        {
            return false;
        }

        if (exception is OperationCanceledException)
        {
            return !cancellationRequested || HasNetworkCause(exception);
        }

        return exception is HttpRequestException or TimeoutException
               || (exception is IOException && HasNetworkCause(exception));
    }

    private static bool HasNetworkCause(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or TimeoutException
                or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }
}
