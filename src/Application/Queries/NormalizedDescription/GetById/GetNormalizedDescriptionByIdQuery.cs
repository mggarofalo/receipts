using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Queries.NormalizedDescription.GetById;

public record GetNormalizedDescriptionByIdQuery : IQuery<NormalizedDescriptionDetail?>
{
	public Guid Id { get; }
	public const string IdCannotBeEmptyExceptionMessage = "NormalizedDescription Id cannot be empty.";

	public GetNormalizedDescriptionByIdQuery(Guid id)
	{
		if (id == Guid.Empty)
		{
			throw new ArgumentException(IdCannotBeEmptyExceptionMessage, nameof(id));
		}

		Id = id;
	}
}
