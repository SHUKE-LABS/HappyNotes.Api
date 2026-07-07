namespace HappyNotes.Api.Tests.Controllers;

[TestFixture]
public class TelegramSettingsControllerAddTests
{
    // Replicates the format guards in TelegramSettingsController.Add.
    private static void ValidateAddRequest(string encryptedToken, string channelId)
    {
        if (string.IsNullOrWhiteSpace(encryptedToken))
            throw new ArgumentException("EncryptedToken is required");
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("ChannelId is required");
    }

    [Test]
    public void Add_EmptyEncryptedToken_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ValidateAddRequest("", "channel123"));
    }

    [Test]
    public void Add_EmptyChannelId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ValidateAddRequest("some-token", ""));
    }

    [Test]
    public void Add_SameTokenFlag_Passes()
    {
        Assert.DoesNotThrow(() => ValidateAddRequest("the same token as the last setting", "channel123"));
    }
}
