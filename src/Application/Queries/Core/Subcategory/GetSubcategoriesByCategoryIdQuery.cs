using Application.Interfaces;
using Application.Models;

namespace Application.Queries.Core.Subcategory;

public record GetSubcategoriesByCategoryIdQuery : IQuery<PagedResult<Domain.Core.Subcategory>>
{
	public Guid CategoryId { get; }
	public int Offset { get; }
	public int Limit { get; }
	public SortParams Sort { get; }
	public bool? IsActive { get; }
	public string? Q { get; }
	public const string CategoryIdCannotBeEmptyExceptionMessage = "Category Id cannot be empty.";

	public GetSubcategoriesByCategoryIdQuery(Guid categoryId, int offset, int limit, SortParams sort, bool? isActive = null, string? q = null)
	{
		if (categoryId == Guid.Empty)
		{
			throw new ArgumentException(CategoryIdCannotBeEmptyExceptionMessage, nameof(categoryId));
		}

		CategoryId = categoryId;
		Offset = offset;
		Limit = limit;
		Sort = sort;
		IsActive = isActive;
		Q = q;
	}
}
