using API.Controllers;
using Application.Classification;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Moq;
namespace Presentation.API.Tests.Controllers;

public class JevControllerTests
{
	[Fact]
	public void BothEndpointsRequireAdmin()
	{
		AuthorizeAttribute authorization = typeof(JevController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
		authorization.Policy.Should().Be("RequireAdmin");
		typeof(JevController).GetMethods().Where(method => method.Name is "Status" or "Check")
			.Should().OnlyContain(method => !method.IsDefined(typeof(AllowAnonymousAttribute), true));
	}
	[Fact]
	public void PassiveStatusDoesNotCheckProvider()
	{
		Mock<IClassificationProviderConnection> connection = new(MockBehavior.Strict);
		connection.Setup(value => value.GetStatus()).Returns(new ProviderConnectionStatus(ProviderConnectionState.Missing, "missingKey", []));
		JevController controller = new(connection.Object);
		controller.Status().Value!.Reason.Should().Be("missingKey");
		connection.Verify(value => value.CheckAsync(It.IsAny<CancellationToken>()), Times.Never);
	}
}
