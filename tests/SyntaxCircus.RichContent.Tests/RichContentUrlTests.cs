namespace SyntaxCircus.RichContent.Tests;

public class RichContentUrlTests
{
    [Theory]
    [InlineData("https://syntaxcircus.com/x")]
    [InlineData("/blog/post")]
    [InlineData("mailto:a@b.co")]
    [InlineData("tel:+1-555-0100")]
    public void IsSafe_AllowedUrls_ReturnsTrue(string url) => RichContentUrl.IsSafe(url).ShouldBeTrue();

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("//evil.com")]
    [InlineData("http://x.com")]
    [InlineData("https://user@x.com")]
    [InlineData("/a b")]
    [InlineData("https://x.com/\"")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSafe_DisallowedUrls_ReturnsFalse(string? url) => RichContentUrl.IsSafe(url).ShouldBeFalse();

    [Fact]
    public void IsSafe_UrlOverMaxLength_ReturnsFalse() =>
        RichContentUrl.IsSafe("https://x.com/" + new string('a', 2049 - "https://x.com/".Length)).ShouldBeFalse();
}
