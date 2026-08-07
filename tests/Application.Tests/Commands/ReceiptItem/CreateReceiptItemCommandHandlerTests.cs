using Application.Commands.ReceiptItem.Create;
using Application.Interfaces.Services;
using FluentAssertions;
using Moq;
using SampleData.Domain.Core;

namespace Application.Tests.Commands.ReceiptItem;

public class CreateReceiptItemCommandHandlerTests
{
	private readonly Mock<IReceiptItemService> _receiptItemService = new();
	private readonly Mock<IReceiptService> _receiptService = new();
	private readonly Mock<IItemTemplateService> _itemTemplateService = new();

	private CreateReceiptItemCommandHandler CreateHandler() =>
		new(_receiptItemService.Object, _receiptService.Object, _itemTemplateService.Object);

	[Fact]
	public async Task CreateReceiptItemCommandHandler_WithValidCommand_ReturnsCreatedReceiptItems()
	{
		Domain.Core.Receipt receipt = ReceiptGenerator.Generate();
		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(2);

		_receiptService.Setup(r => r.ExistsAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_receiptItemService.Setup(r => r
			.CreateAsync(It.IsAny<List<Domain.Core.ReceiptItem>>(), receipt.Id, It.IsAny<CancellationToken>()))
			.ReturnsAsync(input);

		CreateReceiptItemCommand command = new(input, receipt.Id);
		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		result.Should().HaveCount(input.Count);
	}

	[Fact]
	public async Task Handle_MissingReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: a nonexistent receipt must 404 instead of surfacing an FK-violation 500.
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_receiptItemService.Verify(r => r.CreateAsync(
			It.IsAny<List<Domain.Core.ReceiptItem>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task Handle_SoftDeletedReceipt_ThrowsKeyNotFoundExceptionAndNeverCreates()
	{
		// RECEIPTS-763: ExistsAsync respects the soft-delete query filter, so a trashed receipt
		// reads as absent and no active receipt item is orphaned under it.
		Guid receiptId = Guid.NewGuid();
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId);

		Func<Task> act = async () => await CreateHandler().Handle(command, CancellationToken.None);

		await act.Should().ThrowAsync<KeyNotFoundException>();
		_receiptItemService.Verify(r => r.CreateAsync(
			It.IsAny<List<Domain.Core.ReceiptItem>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// ── RECEIPTS-881: template provenance stamps the canonical description ────

	private void ArrangeReceiptAndPassthrough(Guid receiptId)
	{
		_receiptService.Setup(r => r.ExistsAsync(receiptId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
		_receiptItemService
			.Setup(r => r.CreateAsync(It.IsAny<List<Domain.Core.ReceiptItem>>(), receiptId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((List<Domain.Core.ReceiptItem> items, Guid _, CancellationToken _) => items);
	}

	private void ArrangeTemplate(Guid templateId, Guid? canonicalId)
	{
		_itemTemplateService
			.Setup(t => t.GetByIdAsync(templateId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Domain.Core.ItemTemplate(templateId, "Gallon of Milk") { NormalizedDescriptionId = canonicalId });
	}

	[Fact]
	public async Task Handle_ItemFromTemplate_IsStampedWithTheTemplateCanonicalEntry()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		Guid canonicalId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		ArrangeTemplate(templateId, canonicalId);

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(1);
		CreateReceiptItemCommand command = new(input, receiptId, [templateId]);

		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		// The resolver only looks at items WHERE NormalizedDescriptionId IS NULL, so stamping
		// here is precisely what makes it skip them — no second predicate required.
		result.Should().ContainSingle().Which.NormalizedDescriptionId.Should().Be(canonicalId);
	}

	[Fact]
	public async Task Handle_StampedItemCarriesNoMatchScore()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		ArrangeTemplate(templateId, Guid.NewGuid());

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(1);
		input[0].NormalizedDescriptionMatchScore = 0.42;
		CreateReceiptItemCommand command = new(input, receiptId, [templateId]);

		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		// The score column records a cosine similarity and nothing was compared — this is a
		// declaration, not a match. A fabricated 1.0 would be indistinguishable from a perfect ANN
		// hit in PreviewThresholdImpactAsync, which buckets items by exactly this column.
		result.Should().ContainSingle().Which.NormalizedDescriptionMatchScore.Should().BeNull();
	}

	[Fact]
	public async Task Handle_UnknownTemplateId_LeavesTheItemForTheResolver()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		_itemTemplateService
			.Setup(t => t.GetByIdAsync(templateId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((Domain.Core.ItemTemplate?)null);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId, [templateId]);

		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		// A stale template id is a hint that went out of date, not a reason to reject somebody's
		// receipt. The item falls through to the resolver exactly as it did before this existed.
		result.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task Handle_TemplateWithNoCanonicalEntryYet_LeavesTheItemForTheResolver()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		ArrangeTemplate(templateId, canonicalId: null);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(1), receiptId, [templateId]);

		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		// Templates that predate RECEIPTS-881 link lazily rather than by backfill, so an unlinked
		// one is an expected state, not an error.
		result.Should().ContainSingle().Which.NormalizedDescriptionId.Should().BeNull();
	}

	[Fact]
	public async Task Handle_LooksUpEachTemplateOnce_EvenAcrossManyItems()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		ArrangeTemplate(templateId, Guid.NewGuid());

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(3);
		CreateReceiptItemCommand command = new(input, receiptId, [templateId, templateId, templateId]);

		await CreateHandler().Handle(command, CancellationToken.None);

		// A receipt commonly has several lines from one template; one lookup per line would turn
		// a batch create into an N+1.
		_itemTemplateService.Verify(t => t.GetByIdAsync(templateId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task Handle_MixedItems_StampsOnlyTheOnesFromATemplate()
	{
		Guid receiptId = Guid.NewGuid();
		Guid templateId = Guid.NewGuid();
		Guid canonicalId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);
		ArrangeTemplate(templateId, canonicalId);

		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(2);
		CreateReceiptItemCommand command = new(input, receiptId, [null, templateId]);

		List<Domain.Core.ReceiptItem> result = await CreateHandler().Handle(command, CancellationToken.None);

		// The common case: one line picked from a template, the next typed by hand.
		result[0].NormalizedDescriptionId.Should().BeNull();
		result[1].NormalizedDescriptionId.Should().Be(canonicalId);
	}

	[Fact]
	public void Command_TemplateIdsOfTheWrongLength_ThrowsRatherThanMisalign()
	{
		List<Domain.Core.ReceiptItem> input = ReceiptItemGenerator.GenerateList(2);

		// Parallel lists are a footgun: a short list would silently stamp item 0 with item 1's
		// template and leave the rest unstamped, and nothing downstream could detect it.
		Action act = () => _ = new CreateReceiptItemCommand(input, Guid.NewGuid(), [Guid.NewGuid()]);

		act.Should().Throw<ArgumentException>()
			.WithMessage($"{CreateReceiptItemCommand.TemplateIdsMustAlignWithItems}*");
	}

	[Fact]
	public async Task Handle_NoTemplateIdsSupplied_TouchesTheTemplateServiceNotAtAll()
	{
		Guid receiptId = Guid.NewGuid();
		ArrangeReceiptAndPassthrough(receiptId);

		CreateReceiptItemCommand command = new(ReceiptItemGenerator.GenerateList(2), receiptId);

		await CreateHandler().Handle(command, CancellationToken.None);

		// Every pre-existing caller goes through this path; it must cost nothing.
		_itemTemplateService.Verify(
			t => t.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
	}
}
