namespace SyntaxCircus.RichContent;

public static class RichContentUrl
{
    /// <summary>
    /// True for <c>https:</c> (host required, no userinfo), <c>mailto:</c>, <c>tel:</c>, and root-relative
    /// paths starting with a single <c>/</c>.
    /// </summary>
    public static bool IsSafe(string? url, int maxLength = 2048)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > maxLength
            || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '<' or '>' or '"' or '\''))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || url[1] != '/';
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0;
        }

        if (uri.Scheme == Uri.UriSchemeMailto)
        {
            var address = url["mailto:".Length..];
            return address.Length > 0 && !address.Contains('?') && !address.Contains('#') && !address.Contains('%')
                && System.Net.Mail.MailAddress.TryCreate(address, out var parsed) && parsed.Address == address;
        }

        if (uri.Scheme == "tel")
        {
            var phone = url["tel:".Length..];
            return phone.Any(char.IsAsciiDigit)
                && phone.Select((c, i) => char.IsAsciiDigit(c) || c is '-' or '(' or ')' || (c == '+' && i == 0)).All(x => x);
        }

        return false;
    }
}
