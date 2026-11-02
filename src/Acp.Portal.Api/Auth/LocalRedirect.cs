using System.Diagnostics.CodeAnalysis;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// Keeps a <c>returnUrl</c> pointing at this site.
/// </summary>
/// <remarks>
/// Sign-in endpoints take a destination from the query string, which is the classic shape of an
/// open redirect: a link to the portal's own trusted domain that lands the reader somewhere
/// else, just after they typed a password. Anything that is not plainly a path on this site is
/// replaced by the home page rather than rejected, because a bad return URL is not worth an
/// error page.
/// </remarks>
internal static class LocalRedirect
{
    public static string Sanitize(string? returnUrl) => IsLocal(returnUrl) ? returnUrl : "/";

    private static bool IsLocal([NotNullWhen(true)] string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        // "//host" and "/\host" are both protocol-relative and leave the site.
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
