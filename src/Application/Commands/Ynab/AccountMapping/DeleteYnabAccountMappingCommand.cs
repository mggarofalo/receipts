using Application.Interfaces;
using MediatR;

namespace Application.Commands.Ynab.AccountMapping;

public record DeleteYnabAccountMappingCommand(Guid Id) : ICommand<Unit>;
