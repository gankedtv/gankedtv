using System.Text;

namespace GankedTV.Api.Auth.Providers;

internal static class OAuthQueryString
{
    public static string Append(string baseUrl, IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new StringBuilder(baseUrl);
        var first = true;
        foreach (var (k, v) in pairs)
        {
            if (v is null) continue;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
