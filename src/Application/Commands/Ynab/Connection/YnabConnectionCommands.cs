using Application.Interfaces.Services;
using Application.Models.Ynab;
using Mediator;

namespace Application.Commands.Ynab.Connection;

/// <summary>Result is the YNAB address to visit, or null when OAuth is not configured.</summary>
public record StartYnabAuthorizationCommand(string UserId) : Interfaces.ICommand<string?>;

public record CompleteYnabAuthorizationCommand(string UserId, string Code, string State) : Interfaces.ICommand<YnabAuthorizationCompletion>
{
	// A record's generated ToString would print the code and state.
	public override string ToString() => nameof(CompleteYnabAuthorizationCommand);
}

public record DisconnectYnabCommand : Interfaces.ICommand<YnabRemoteRevocation>;

public class StartYnabAuthorizationCommandHandler(IYnabConnectionService connectionService)
	: IRequestHandler<StartYnabAuthorizationCommand, string?>
{
	public async ValueTask<string?> Handle(StartYnabAuthorizationCommand request, CancellationToken cancellationToken) =>
		await connectionService.StartAuthorizationAsync(request.UserId, cancellationToken);
}

public class CompleteYnabAuthorizationCommandHandler(IYnabConnectionService connectionService)
	: IRequestHandler<CompleteYnabAuthorizationCommand, YnabAuthorizationCompletion>
{
	public async ValueTask<YnabAuthorizationCompletion> Handle(CompleteYnabAuthorizationCommand request, CancellationToken cancellationToken) =>
		await connectionService.CompleteAuthorizationAsync(request.UserId, request.Code, request.State, cancellationToken);
}

public class DisconnectYnabCommandHandler(IYnabConnectionService connectionService)
	: IRequestHandler<DisconnectYnabCommand, YnabRemoteRevocation>
{
	public async ValueTask<YnabRemoteRevocation> Handle(DisconnectYnabCommand request, CancellationToken cancellationToken) =>
		await connectionService.DisconnectAsync(cancellationToken);
}
