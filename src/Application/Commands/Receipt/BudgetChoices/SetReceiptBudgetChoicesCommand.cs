using Application.Interfaces;

namespace Application.Commands.Receipt.BudgetChoices;

/// <summary>Sets a receipt's "paid from" envelope (null is automatic) and occasion (null is none).</summary>
public record SetReceiptBudgetChoicesCommand(Guid ReceiptId, Guid? PaidFromEnvelopeId, Guid? OccasionId) : ICommand<bool>;
