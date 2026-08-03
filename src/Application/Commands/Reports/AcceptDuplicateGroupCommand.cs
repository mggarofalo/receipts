using Application.Interfaces;

namespace Application.Commands.Reports;

public record AcceptDuplicateGroupCommand(List<Guid> ReceiptIds) : ICommand<int>;
