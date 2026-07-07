namespace HappyNotes.Api.Tests.Controllers;

[TestFixture]
public class SyncQueueAdminControllerTests
{
    // Replicates the RequireAssertion predicate registered in Program.cs.
    private static bool IsAdminAllowed(HashSet<string> adminIds, ClaimsPrincipal user)
        => adminIds.Count > 0 &&
           adminIds.Contains(
               user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? "");

    private static ClaimsPrincipal UserWithId(long id)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())],
            authenticationType: "Test"));

    [Test]
    public void EmptyAdminList_AnyAuthenticatedUser_Denied()
    {
        var adminIds = new HashSet<string>();
        Assert.That(IsAdminAllowed(adminIds, UserWithId(1)), Is.False);
    }

    [Test]
    public void AdminUser_IdInList_Allowed()
    {
        var adminIds = new HashSet<string> { "42" };
        Assert.That(IsAdminAllowed(adminIds, UserWithId(42)), Is.True);
    }

    [Test]
    public void NonAdminUser_IdNotInList_Denied()
    {
        var adminIds = new HashSet<string> { "42" };
        Assert.That(IsAdminAllowed(adminIds, UserWithId(99)), Is.False);
    }

    [Test]
    public void NonAdminUser_MultipleAdminIds_Denied()
    {
        var adminIds = new HashSet<string> { "1", "42", "100" };
        Assert.That(IsAdminAllowed(adminIds, UserWithId(7)), Is.False);
    }

    [Test]
    public void AdminUser_LongIdAboveIntMax_Allowed()
    {
        long largeId = (long)int.MaxValue + 1;
        var adminIds = new HashSet<string> { largeId.ToString() };
        Assert.That(IsAdminAllowed(adminIds, UserWithId(largeId)), Is.True);
    }

    [Test]
    public void AnonymousUser_NoIdentifierClaim_Denied()
    {
        var adminIds = new HashSet<string> { "1" };
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.That(IsAdminAllowed(adminIds, anonymous), Is.False);
    }
}
