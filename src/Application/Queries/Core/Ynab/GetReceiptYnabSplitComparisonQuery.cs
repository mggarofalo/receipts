using Application.Models.Ynab;
using Mediator;

namespace Application.Queries.Core.Ynab;

/// <summary>The expected split uses each item's recorded envelope; it never re-derives one.</summary>
public record GetReceiptYnabSplitComparisonQuery(Guid ReceiptId) : IRequest<ReceiptYnabSplitComparisonResult>;
