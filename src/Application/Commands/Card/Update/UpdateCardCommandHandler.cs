using Application.Interfaces.Services;
using Mediator;

namespace Application.Commands.Card.Update;

public class UpdateCardCommandHandler(ICardService cardService) : IRequestHandler<UpdateCardCommand, bool>
{
	public async ValueTask<bool> Handle(UpdateCardCommand request, CancellationToken cancellationToken)
	{
		foreach (Domain.Core.Card card in request.Cards)
		{
			bool exists = await cardService.ExistsAsync(card.Id, cancellationToken);
			if (!exists)
			{
				return false;
			}
		}

		await cardService.UpdateAsync([.. request.Cards], cancellationToken);
		return true;
	}
}
