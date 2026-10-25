using Application.Interfaces;
using Application.Interfaces.Services;

namespace Application.Commands.Trash.Purge;

public record PurgeTrashCommand : ICommand<PurgeTrashResult>;
