using Application.Interfaces.Services;
using Application.Models;
using Application.Queries.Core.Account;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Queries.Core.Account;

public class GetAllAccountsQueryHandlerTests
{
	[Fact]
	public async Task Handle_ShouldReturnAllAccounts()
	{
		List<Domain.Core.Account> expected = AccountGenerator.GenerateList(2);

		Mock<IAccountService> mockService = new();
		mockService.Setup(r => r.GetAllAsync(0, 50, It.IsAny<SortParams>(), true, " needle ", It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<Domain.Core.Account>(expected, expected.Count, 0, 50));

		GetAllAccountsQueryHandler handler = new(mockService.Object);
		GetAllAccountsQuery query = new(0, 50, SortParams.Default, true, " needle ");

		PagedResult<Domain.Core.Account> result = await handler.Handle(query, CancellationToken.None);

		result.Data.Should().BeSameAs(expected);
		mockService.VerifyAll();
	}
}
