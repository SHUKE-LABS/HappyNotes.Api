using System.Linq.Expressions;
using Api.Framework;
using Google.Apis.Auth;
using HappyNotes.Common;
using HappyNotes.Entities;
using HappyNotes.Services.interfaces;
using Moq;

namespace HappyNotes.Services.Tests;

[TestFixture]
public class GoogleAuthServiceTests
{
    private Mock<IGoogleIdTokenVerifier> _mockVerifier;
    private Mock<IRepositoryBase<User>> _mockUserRepo;
    private Mock<IRepositoryBase<UserExternalLogin>> _mockExternalLoginRepo;
    private GoogleAuthService _service;

    [SetUp]
    public void Setup()
    {
        _mockVerifier = new Mock<IGoogleIdTokenVerifier>();
        _mockUserRepo = new Mock<IRepositoryBase<User>>();
        _mockExternalLoginRepo = new Mock<IRepositoryBase<UserExternalLogin>>();
        _service = new GoogleAuthService(_mockVerifier.Object, _mockUserRepo.Object, _mockExternalLoginRepo.Object);
    }

    private static GoogleJsonWebSignature.Payload MakePayload(string subject, string email, bool emailVerified) =>
        new()
        {
            Subject = subject,
            Email = email,
            EmailVerified = emailVerified,
        };

    [Test]
    public void ResolveOrCreateUserAsync_InvalidToken_PropagatesAndCreatesNothing()
    {
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ThrowsAsync(ExceptionHelper.New(HappyNotes.Common.Enums.EventId._00109_InvalidGoogleIdToken));

        Assert.ThrowsAsync<Api.Framework.Exceptions.CustomException<object>>(
            async () => await _service.ResolveOrCreateUserAsync("bad-token"));

        _mockUserRepo.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<User>()), Times.Never);
        _mockExternalLoginRepo.Verify(r => r.InsertAsync(It.IsAny<UserExternalLogin>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrCreateUserAsync_ExistingExternalLogin_ReturnsLinkedUserWithoutInserts()
    {
        var existingUser = new User { Id = 42, Username = "someone", Email = "someone@example.com" };
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ReturnsAsync(MakePayload("google-subject", "someone@example.com", true));
        _mockExternalLoginRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<UserExternalLogin, bool>>>(), null))
            .ReturnsAsync(new UserExternalLogin { Id = 1, UserId = 42, Provider = "google", ProviderSubject = "google-subject" });
        _mockUserRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<User, bool>>>(), null))
            .ReturnsAsync(existingUser);

        var result = await _service.ResolveOrCreateUserAsync("valid-token");

        Assert.That(result, Is.SameAs(existingUser));
        _mockUserRepo.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<User>()), Times.Never);
        _mockExternalLoginRepo.Verify(r => r.InsertAsync(It.IsAny<UserExternalLogin>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrCreateUserAsync_VerifiedEmailMatchesExistingUser_LinksWithoutCreatingUser()
    {
        var existingUser = new User { Id = 7, Username = "existing", Email = "existing@example.com" };
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ReturnsAsync(MakePayload("google-subject", "existing@example.com", true));
        _mockExternalLoginRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<UserExternalLogin, bool>>>(), null))
            .ReturnsAsync((UserExternalLogin?)null);
        _mockUserRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<User, bool>>>(), null))
            .ReturnsAsync(existingUser);

        UserExternalLogin? insertedLink = null;
        _mockExternalLoginRepo.Setup(r => r.InsertAsync(It.IsAny<UserExternalLogin>()))
            .Callback<UserExternalLogin>(l => insertedLink = l)
            .ReturnsAsync(true);

        var result = await _service.ResolveOrCreateUserAsync("valid-token");

        Assert.That(result, Is.SameAs(existingUser));
        _mockUserRepo.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<User>()), Times.Never);
        _mockExternalLoginRepo.Verify(r => r.InsertAsync(It.IsAny<UserExternalLogin>()), Times.Once);
        Assert.That(insertedLink, Is.Not.Null);
        Assert.That(insertedLink!.UserId, Is.EqualTo(existingUser.Id));
        Assert.That(insertedLink.Provider, Is.EqualTo("google"));
        Assert.That(insertedLink.ProviderSubject, Is.EqualTo("google-subject"));
    }

    [Test]
    public async Task ResolveOrCreateUserAsync_BrandNewIdentity_CreatesUserAndLink()
    {
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ReturnsAsync(MakePayload("new-subject", "new.person@example.com", true));
        _mockExternalLoginRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<UserExternalLogin, bool>>>(), null))
            .ReturnsAsync((UserExternalLogin?)null);
        _mockUserRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<User, bool>>>(), null))
            .ReturnsAsync((User?)null);

        User? insertedUser = null;
        _mockUserRepo.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<User>()))
            .Callback<User>(u => insertedUser = u)
            .ReturnsAsync(99);

        UserExternalLogin? insertedLink = null;
        _mockExternalLoginRepo.Setup(r => r.InsertAsync(It.IsAny<UserExternalLogin>()))
            .Callback<UserExternalLogin>(l => insertedLink = l)
            .ReturnsAsync(true);

        var result = await _service.ResolveOrCreateUserAsync("valid-token");

        _mockUserRepo.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<User>()), Times.Once);
        Assert.That(insertedUser, Is.Not.Null);
        Assert.That(insertedUser!.Email, Is.EqualTo("new.person@example.com"));
        Assert.That(insertedUser.Password, Is.Empty);
        Assert.That(insertedUser.Salt, Is.Empty);
        Assert.That(insertedUser.EmailVerified, Is.EqualTo(1));
        Assert.That(insertedUser.Gravatar, Is.Not.Null.And.Not.Empty);

        Assert.That(result.Id, Is.EqualTo(99));
        Assert.That(insertedLink, Is.Not.Null);
        Assert.That(insertedLink!.UserId, Is.EqualTo(99));
        Assert.That(insertedLink.ProviderSubject, Is.EqualTo("new-subject"));
    }
}
