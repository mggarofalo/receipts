using Application.Interfaces;

namespace Application.Commands.Reports;

public record UnacceptDuplicateGroupCommand(List<Guid> ReceiptIds) : ICommand<int>;
