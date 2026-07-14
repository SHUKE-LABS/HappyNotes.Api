using HappyNotes.Services;

namespace HappyNotes.Services.Tests;

public class OAuth1HelperTests
{
    [TestCase("abcXYZ123-._~", "abcXYZ123-._~")] // unreserved chars pass through
    [TestCase(" ", "%20")]
    [TestCase("=", "%3D")]
    [TestCase("&", "%26")]
    [TestCase("@", "%40")]
    [TestCase("+", "%2B")]
    [TestCase("r b", "r%20b")]
    public void PercentEncode_MatchesRfc3986(string input, string expected)
    {
        Assert.That(OAuth1Helper.PercentEncode(input), Is.EqualTo(expected));
    }

    [Test]
    public void PercentEncode_NullOrEmpty_ReturnsEmpty()
    {
        Assert.That(OAuth1Helper.PercentEncode(null), Is.EqualTo(string.Empty));
        Assert.That(OAuth1Helper.PercentEncode(string.Empty), Is.EqualTo(string.Empty));
    }

    [Test]
    public void BuildSignatureBaseString_MatchesRfc5849Example()
    {
        // The canonical example from RFC 5849 section 3.4.1.1 (no oauth_version).
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("b5", "=%3D"),
            new("a3", "a"),
            new("c@", ""),
            new("a2", "r b"),
            new("c2", ""),
            new("a3", "2 q"),
            new("oauth_consumer_key", "9djdj82h48djs9d2"),
            new("oauth_token", "kkk9d7dh3k39sjv7"),
            new("oauth_signature_method", "HMAC-SHA1"),
            new("oauth_timestamp", "137131201"),
            new("oauth_nonce", "7d8f3e4a"),
        };

        var baseString = OAuth1Helper.BuildSignatureBaseString("POST", "http://example.com/request", parameters);

        const string expected =
            "POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da" +
            "%26b5%3D%253D%25253D%26c%2540%3D%26c2%3D%26oauth_consumer_key%3D9djdj82h48djs9d2" +
            "%26oauth_nonce%3D7d8f3e4a%26oauth_signature_method%3DHMAC-SHA1" +
            "%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7";

        Assert.That(baseString, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeSignature_IsDeterministicAndCorrectLength()
    {
        const string baseString = "POST&http%3A%2F%2Fexample.com%2Frequest&status%3Dhello";
        var sig1 = OAuth1Helper.ComputeSignature(baseString, "consumer-secret", "token-secret");
        var sig2 = OAuth1Helper.ComputeSignature(baseString, "consumer-secret", "token-secret");

        Assert.That(sig1, Is.EqualTo(sig2)); // deterministic
        Assert.That(sig1, Is.Not.Empty);
        // HMAC-SHA1 => 20 bytes => 28 base64 chars (with padding)
        Assert.That(sig1.Length, Is.EqualTo(28));
    }

    [Test]
    public void ComputeSignature_DifferentTokenSecret_ProducesDifferentSignature()
    {
        const string baseString = "POST&http%3A%2F%2Fexample.com%2Frequest&status%3Dhello";
        var sig1 = OAuth1Helper.ComputeSignature(baseString, "consumer-secret", "token-secret-a");
        var sig2 = OAuth1Helper.ComputeSignature(baseString, "consumer-secret", "token-secret-b");

        Assert.That(sig1, Is.Not.EqualTo(sig2));
    }

    [Test]
    public void BuildAuthorizationHeader_ContainsRequiredOAuthFields()
    {
        var header = OAuth1Helper.BuildAuthorizationHeader(
            "POST", "https://api.fanfou.com/statuses/update.json",
            "consumer-key", "consumer-secret",
            token: "access-token", tokenSecret: "access-secret",
            nonce: "abc123", timestamp: 1234567890,
            requestParams: new Dictionary<string, string> { ["status"] = "hi" });

        Assert.That(header, Does.StartWith("OAuth "));
        Assert.That(header, Does.Contain("oauth_consumer_key=\"consumer-key\""));
        Assert.That(header, Does.Contain("oauth_token=\"access-token\""));
        Assert.That(header, Does.Contain("oauth_signature_method=\"HMAC-SHA1\""));
        Assert.That(header, Does.Contain("oauth_nonce=\"abc123\""));
        Assert.That(header, Does.Contain("oauth_timestamp=\"1234567890\""));
        Assert.That(header, Does.Contain("oauth_version=\"1.0\""));
        Assert.That(header, Does.Contain("oauth_signature=\""));
    }
}
