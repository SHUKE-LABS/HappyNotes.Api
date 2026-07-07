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
        var existingUser = new User { Id = 7, Username = "existing", Email = "existing@example.com", EmailVerified = 1 };
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

    [Test]
    public async Task ResolveOrCreateUserAsync_UnverifiedEmail_SkipsLookupAndCreatesUserWithEmailUnverified()
    {
        // Distinct Username so it never collides with CreateUserAsync's generated username;
        // matching Email so it would be wrongly returned if the (skipped) email lookup ran.
        var trapUser = new User { Id = 55, Username = "trapuser", Email = "unverified@example.com" };
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ReturnsAsync(MakePayload("unverified-subject", "unverified@example.com", false));
        _mockExternalLoginRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<UserExternalLogin, bool>>>(), null))
            .ReturnsAsync((UserExternalLogin?)null);

        // GetFirstOrDefaultAsync<User> is shared by the (skipped) email lookup and
        // CreateUserAsync's username-uniqueness do-while; a blanket non-null mock would
        // hang that loop, so differentiate by which predicate the trap user satisfies.
        _mockUserRepo.Setup(r => r.GetFirstOrDefaultAsync(
                It.Is<Expression<Func<User, bool>>>(e => e.Compile()(trapUser)), null))
            .ReturnsAsync(trapUser);
        _mockUserRepo.Setup(r => r.GetFirstOrDefaultAsync(
                It.Is<Expression<Func<User, bool>>>(e => !e.Compile()(trapUser)), null))
            .ReturnsAsync((User?)null);

        User? insertedUser = null;
        _mockUserRepo.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<User>()))
            .Callback<User>(u => insertedUser = u)
            .ReturnsAsync(101);

        UserExternalLogin? insertedLink = null;
        _mockExternalLoginRepo.Setup(r => r.InsertAsync(It.IsAny<UserExternalLogin>()))
            .Callback<UserExternalLogin>(l => insertedLink = l)
            .ReturnsAsync(true);

        var result = await _service.ResolveOrCreateUserAsync("valid-token");

        _mockUserRepo.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<User>()), Times.Once);
        Assert.That(insertedUser, Is.Not.Null);
        Assert.That(insertedUser!.Email, Is.EqualTo("unverified@example.com"));
        Assert.That(insertedUser.EmailVerified, Is.EqualTo(0));

        Assert.That(result.Id, Is.EqualTo(101));
        Assert.That(insertedLink, Is.Not.Null);
        Assert.That(insertedLink!.UserId, Is.EqualTo(101));
        Assert.That(insertedLink.ProviderSubject, Is.EqualTo("unverified-subject"));
    }

    [Test]
    public async Task ResolveOrCreateUserAsync_VerifiedEmailMatchesUnverifiedExistingUser_CreatesNewAccountWithoutLinking()
    {
        // Pre-account-takeover guard: attacker pre-registered victim@gmail.com with EmailVerified=0.
        // Google sign-in must NOT link to that account.
        var attackerAccount = new User { Id = 5, Username = "attacker", Email = "victim@gmail.com", EmailVerified = 0 };
        _mockVerifier.Setup(v => v.VerifyAsync(It.IsAny<string>()))
            .ReturnsAsync(MakePayload("google-subject-victim", "victim@gmail.com", true));
        _mockExternalLoginRepo.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<UserExternalLogin, bool>>>(), null))
            .ReturnsAsync((UserExternalLogin?)null);

        // The fixed predicate (w.Email == email && w.EmailVerified == 1) won't match the attacker's
        // account (EmailVerified=0), so GetFirstOrDefaultAsync returns null → CreateUserAsync is called.
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
        Assert.That(insertedUser!.Email, Is.EqualTo("victim@gmail.com"));
        Assert.That(insertedUser.EmailVerified, Is.EqualTo(1));

        Assert.That(result.Id, Is.EqualTo(99));
        Assert.That(result.Id, Is.Not.EqualTo(attackerAccount.Id));
        Assert.That(insertedLink, Is.Not.Null);
        Assert.That(insertedLink!.UserId, Is.EqualTo(99));
        Assert.That(insertedLink.ProviderSubject, Is.EqualTo("google-subject-victim"));
    }
}
