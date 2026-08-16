using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// The webhook URL is a bearer credential shaped like a URL. Every test here
/// backs one of two guarantees: that only a real Discord webhook is accepted
/// (an allowlist with no override), and that the token segment of it can never
/// leak into a description or an error message.
/// </summary>
public class CombatLogUploadTests
{
    // ---- DiscordWebhook.TryParse ----

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://discordapp.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://ptb.discord.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://canary.discord.com/api/webhooks/123456789/abcDEF-token_123")]
    public void AGenuineWebhookUrlOnAnyAllowedHostIsAccepted(string url)
    {
        Assert.True(DiscordWebhook.TryParse(url, out var webhook, out var error));
        Assert.Equal("", error);
        Assert.Equal(url, webhook.AbsoluteUri);
    }

    [Fact]
    public void PlainHttpIsRejected()
    {
        // A webhook URL is a bearer credential; sending it over http would leak
        // it to anything on the network path.
        Assert.False(DiscordWebhook.TryParse(
            "http://discord.com/api/webhooks/123456789/abcDEF-token_123", out _, out var error));
        Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://discord.co/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://discord.com.evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
    public void AnyHostOffTheAllowlistIsRejected(string url)
    {
        // No override toggle exists for a reason -- see the design doc. A host
        // check that could be fooled by a lookalike domain would defeat the
        // whole point of the allowlist.
        Assert.False(DiscordWebhook.TryParse(url, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AMissingTokenSegmentIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/webhooks/123456789", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AMissingIdSegmentIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/webhooks/", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AWrongShapedPathIsRejected()
    {
        // Some other Discord API path -- accepting this would turn the field
        // into a general "any discord.com URL" primitive, not a webhook setting.
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/channels/123456789/messages", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputIsRejectedWithoutThrowing(string? raw)
    {
        Assert.False(DiscordWebhook.TryParse(raw, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void SomethingThatIsNotAUrlAtAllIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse("not a url", out _, out var error));
        Assert.NotEqual("", error);
    }
}
