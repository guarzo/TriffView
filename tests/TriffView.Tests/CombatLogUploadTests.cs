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

    // ---- DiscordWebhook.Describe / Redact ----

    private static readonly Uri SampleWebhook = new(
        "https://discord.com/api/webhooks/123456789/abcDEF-token_123");

    [Fact]
    public void DescribeNamesTheHostAndIdButNeverTheToken()
    {
        var description = DiscordWebhook.Describe(SampleWebhook);

        Assert.Contains("discord.com", description);
        Assert.Contains("123456789", description);
        Assert.DoesNotContain("abcDEF-token_123", description);
    }

    [Fact]
    public void RedactScrubsTheFullUrlFromAnArbitraryMessage()
    {
        var message = $"Could not reach {SampleWebhook.AbsoluteUri}: connection refused";

        var redacted = DiscordWebhook.Redact(message, SampleWebhook);

        Assert.DoesNotContain("abcDEF-token_123", redacted);
        Assert.Contains("connection refused", redacted);
    }

    [Fact]
    public void RedactScrubsALoneTokenEvenWithoutTheFullUrl()
    {
        // HttpRequestException messages vary by platform and .NET version --
        // some carry the full request URI, some just a fragment of it. The
        // token itself must never survive either shape.
        var message = "PostAsync failed for token abcDEF-token_123 after 3 retries";

        var redacted = DiscordWebhook.Redact(message, SampleWebhook);

        Assert.DoesNotContain("abcDEF-token_123", redacted);
        Assert.Contains("after 3 retries", redacted);
    }

    [Fact]
    public void RedactLeavesUnrelatedTextAlone()
    {
        var redacted = DiscordWebhook.Redact("Discord returned 500 Internal Server Error", SampleWebhook);

        Assert.Equal("Discord returned 500 Internal Server Error", redacted);
    }
}
