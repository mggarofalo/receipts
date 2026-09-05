using System.Security.Claims;
using API.Authentication;
using FluentAssertions;

namespace Presentation.API.Tests.Authentication;

public class RoleChangeActorTests
{
	[Fact]
	public void SameSubjectAcrossAuthenticatedSchemes_UsesItsAdminAuthority()
	{
		ClaimsPrincipal principal = new([Identity("alice", "Admin"), Identity("alice", "User")]);

		RoleChangeActor.GetSubject(principal).Should().Be("alice");
	}

	[Fact]
	public void DifferentSubjects_CannotCombineIdentifierAndAdminAuthority()
	{
		ClaimsPrincipal principal = new([Identity("alice", "User"), Identity("bob", "Admin")]);

		RoleChangeActor.GetSubject(principal).Should().BeNull();
	}

	[Theory]
	[InlineData(null, "Admin", "test")]
	[InlineData("alice", "User", "test")]
	[InlineData("alice", "Admin", null)]
	public void MissingAuthenticatedAdminSubject_IsRejected(string? subject, string role, string? authenticationType)
	{
		ClaimsIdentity identity = new(authenticationType);
		if (subject is not null)
		{
			identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
		}

		identity.AddClaim(new Claim(ClaimTypes.Role, role));

		RoleChangeActor.GetSubject(new ClaimsPrincipal(identity)).Should().BeNull();
	}

	[Fact]
	public void UnauthenticatedIdentity_CannotSupplyMissingAdminAuthority()
	{
		ClaimsIdentity anonymousAdmin = new([new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim(ClaimTypes.Role, "Admin")]);

		RoleChangeActor.GetSubject(new ClaimsPrincipal([Identity("alice", "User"), anonymousAdmin])).Should().BeNull();
	}

	private static ClaimsIdentity Identity(string subject, string role) =>
		new([new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Role, role)], "test");
}
