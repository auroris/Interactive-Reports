using System.Security.Claims;
using InteractiveReport.Core.Identity;

namespace InteractiveReport.Core.Tests;

public class ReportIdentityTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void Unauthenticated_resolves_to_null()
    {
        Assert.Null(ReportIdentity.Resolve(new ClaimsPrincipal(new ClaimsIdentity()), null));
        Assert.Null(ReportIdentity.Resolve(null, null));
    }

    [Fact]
    public void Default_chain_prefers_nameidentifier_then_sub_then_name()
    {
        Assert.Equal("nid", ReportIdentity.Resolve(
            User((ClaimTypes.NameIdentifier, "nid"), ("sub", "s"), (ClaimTypes.Name, "n")), null));
        Assert.Equal("s", ReportIdentity.Resolve(
            User(("sub", "s"), (ClaimTypes.Name, "n")), null));
        Assert.Equal("n", ReportIdentity.Resolve(
            User((ClaimTypes.Name, "n")), null));
    }

    [Fact]
    public void Explicit_claim_overrides_the_chain_entirely()
    {
        var user = User((ClaimTypes.NameIdentifier, "nid"), ("email", "a@b.c"));

        Assert.Equal("a@b.c", ReportIdentity.Resolve(user, "email"));
        Assert.Null(ReportIdentity.Resolve(user, "missing-claim"));
    }

    [Fact]
    public void Administrator_match_is_case_insensitive_exact()
    {
        var user = User((ClaimTypes.NameIdentifier, "Steph@Example.com"));

        Assert.True(ReportIdentity.IsAdministrator(user, null, ["steph@example.com"]));
        Assert.False(ReportIdentity.IsAdministrator(user, null, ["steph"]));
        Assert.False(ReportIdentity.IsAdministrator(user, null, []));
        Assert.False(ReportIdentity.IsAdministrator(null, null, ["steph@example.com"]));
    }
}
