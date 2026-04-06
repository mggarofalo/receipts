using Application.Interfaces;
using Application.Models.Ynab;

namespace Application.Commands.Ynab.StaleMappings;

public record ClearStaleMappingsCommand : ICommand<ClearStaleMappingsResult>;
