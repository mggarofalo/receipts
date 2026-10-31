using Application.Interfaces;

namespace Application.Commands.ReceiptItem.BudgetChoices;

/// <summary>Sets an item's envelope override (null is automatic) and occasion (null is none).</summary>
public record SetReceiptItemBudgetChoicesCommand(Guid ItemId, Guid? EnvelopeId, Guid? OccasionId) : ICommand<bool>;
