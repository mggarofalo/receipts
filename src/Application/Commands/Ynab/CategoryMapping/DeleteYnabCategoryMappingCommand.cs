using Application.Interfaces;
using MediatR;

namespace Application.Commands.Ynab.CategoryMapping;

public record DeleteYnabCategoryMappingCommand(Guid Id) : ICommand<Unit>;
