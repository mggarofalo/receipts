using Application.NormalizationAi;
using Common;
using FluentAssertions;
using Infrastructure.Entities.Core;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Infrastructure.IntegrationTests.Services;

public sealed partial class NormalizationAiWorkflowTests
{
	[Fact]
	public async Task SourceRetry_CannotReserveSameScopeThroughAnOlderRepresentativeAfterAnotherRunClaimsNewRepresentative()
	{
		await SeedAsync();
		runner.Setup(x => x.InferAsync(It.IsAny<ChatGptInferenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptInferenceResult("{}"));
		var service = Service();
		NormalizationAiSourceSelection selection = new(new("Synthetic store", "Milk", null), false);
		var original = await service.StartSourcesAsync(Owner, Guid.NewGuid(), [selection], "test-model", CancellationToken.None);
		await service.ProcessNextAsync(CancellationToken.None);
		(await service.GetRunAsync(Owner, original.Id, CancellationToken.None))!.State.Should().Be("failed");
		Guid earlier = Guid.Parse("00000000-0000-0000-0000-000000000001");
		await using (var db = fixture.CreateDbContext())
		{
			var item = await db.ReceiptItems.SingleAsync(x => x.Id == original.Proposals.Single().ReceiptItemId);
			db.ReceiptItems.Add(new ReceiptItemEntity { Id = earlier, ReceiptId = item.ReceiptId, Description = item.Description, ReceiptItemCode = item.ReceiptItemCode, Category = "Food", Quantity = 1, UnitPrice = 2, UnitPriceCurrency = Currency.USD, TotalAmount = 2, TotalAmountCurrency = Currency.USD });
			await db.SaveChangesAsync();
		}
		var competing = await service.StartSourcesAsync(Owner, Guid.NewGuid(), [selection], "test-model", CancellationToken.None);
		competing.Proposals.Single().ReceiptItemId.Should().Be(earlier, "the reservation counterexample must use different physical representatives of the same scope");
		Func<Task> retry = () => service.RetryAsync(Owner, original.Id, Guid.NewGuid(), CancellationToken.None);
		(await retry.Should().ThrowAsync<NormalizationAiException>()).Which.StatusCode.Should().Be(409);
		(await service.ListRunsAsync(Owner, CancellationToken.None)).Should().HaveCount(2);
		(await service.GetRunAsync(Owner, competing.Id, CancellationToken.None))!.State.Should().Be("queued");
	}
}
