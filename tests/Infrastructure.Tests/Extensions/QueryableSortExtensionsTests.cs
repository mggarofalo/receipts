using System.Linq.Expressions;
using Application.Models;
using FluentAssertions;
using Infrastructure.Extensions;

namespace Infrastructure.Tests.Extensions;

public class QueryableSortExtensionsTests
{
	private static readonly List<TestEntity> TestData =
	[
		new() { Id = 1, Name = "Charlie", Age = 30 },
		new() { Id = 2, Name = "Alice", Age = 25 },
		new() { Id = 3, Name = "Bob", Age = 35 },
	];

	private static readonly Dictionary<string, Expression<Func<TestEntity, object>>> AllowedColumns = new(StringComparer.OrdinalIgnoreCase)
	{
		["name"] = e => e.Name,
		["age"] = e => e.Age,
		["id"] = e => e.Id,
	};

	private static readonly Expression<Func<TestEntity, object>> DefaultSort = e => e.Name;

	[Fact]
	public void ApplySort_DefaultSortParams_SortsByDefaultColumnAscending()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();

		// Act
		List<TestEntity> result = query.ApplySort(SortParams.Default, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result.Should().HaveCount(3);
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Fact]
	public void ApplySort_ValidColumnAscending_SortsByColumnAscending()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("age", "asc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result.Should().HaveCount(3);
		result[0].Age.Should().Be(25);
		result[1].Age.Should().Be(30);
		result[2].Age.Should().Be(35);
	}

	[Fact]
	public void ApplySort_ValidColumnDescending_SortsByColumnDescending()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("age", "desc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result.Should().HaveCount(3);
		result[0].Age.Should().Be(35);
		result[1].Age.Should().Be(30);
		result[2].Age.Should().Be(25);
	}

	[Fact]
	public void ApplySort_InvalidColumn_FallsBackToDefaultSort()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("nonexistent", "asc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Theory]
	[InlineData("name")]
	[InlineData("Name")]
	[InlineData("NAME")]
	[InlineData("nAmE")]
	public void ApplySort_CaseInsensitiveColumnMatch_SortsByMatchedColumn(string columnName)
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new(columnName, "desc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result[0].Name.Should().Be("Charlie");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Alice");
	}

	[Fact]
	public void ApplySort_NullSortByWithExplicitDirection_UsesDefaultSort()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new(null, "desc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert — default sort is ascending by name regardless of the explicit direction
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Fact]
	public void ApplySort_DefaultDescendingTrue_SortsByDefaultColumnDescending()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();

		// Act
		List<TestEntity> result = query.ApplySort(SortParams.Default, AllowedColumns, DefaultSort, e => e.Id, defaultDescending: true).ToList();

		// Assert
		result[0].Name.Should().Be("Charlie");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Alice");
	}

	[Fact]
	public void ApplySort_ValidColumnOverridesDefaultDescending_UsesExplicitDirection()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("name", "asc");

		// Act — defaultDescending is true but explicit column sort should use sort.IsDescending (false)
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id, defaultDescending: true).ToList();

		// Assert
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Fact]
	public void ApplySort_EmptySortBy_FallsBackToDefaultSort()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("", "desc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert — empty string is treated as no sort column, so default ascending
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Fact]
	public void ApplySort_WhitespaceSortBy_FallsBackToDefaultSort()
	{
		// Arrange
		IQueryable<TestEntity> query = TestData.AsQueryable();
		SortParams sort = new("   ", "desc");

		// Act
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert — whitespace-only is treated as no sort column, so default ascending
		result[0].Name.Should().Be("Alice");
		result[1].Name.Should().Be("Bob");
		result[2].Name.Should().Be("Charlie");
	}

	[Fact]
	public void ApplySort_EqualPrimaryKeys_BreaksTiesByIdAscending()
	{
		// Arrange — all three rows share the primary sort key (Name), so only the
		// tiebreaker determines their relative order (RECEIPTS-767).
		List<TestEntity> data =
		[
			new() { Id = 3, Name = "Same", Age = 1 },
			new() { Id = 1, Name = "Same", Age = 2 },
			new() { Id = 2, Name = "Same", Age = 3 },
		];
		IQueryable<TestEntity> query = data.AsQueryable();

		// Act
		List<TestEntity> result = query.ApplySort(SortParams.Default, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert — deterministic ascending-by-Id order
		result.Select(e => e.Id).Should().Equal(1, 2, 3);
	}

	[Fact]
	public void ApplySort_EqualPrimaryKeysDescendingSort_StillBreaksTiesByIdAscending()
	{
		// Arrange
		List<TestEntity> data =
		[
			new() { Id = 3, Name = "Same", Age = 1 },
			new() { Id = 1, Name = "Same", Age = 2 },
			new() { Id = 2, Name = "Same", Age = 3 },
		];
		IQueryable<TestEntity> query = data.AsQueryable();
		SortParams sort = new("name", "desc");

		// Act — primary sort is descending, but the tiebreaker is always ascending by Id
		List<TestEntity> result = query.ApplySort(sort, AllowedColumns, DefaultSort, e => e.Id).ToList();

		// Assert
		result.Select(e => e.Id).Should().Equal(1, 2, 3);
	}

	[Fact]
	public void ApplySort_PaginationAcrossOffsets_NoDuplicatesOrGapsWhenPrimaryKeysEqual()
	{
		// Arrange — worst case: every row shares the same primary sort key, so without a
		// unique tiebreaker consecutive pages could duplicate one row and skip another.
		List<TestEntity> data = [.. Enumerable.Range(1, 10).Select(i => new TestEntity { Id = i, Name = "Same", Age = i })];
		IQueryable<TestEntity> query = data.AsQueryable();

		const int pageSize = 3;
		List<int> collected = [];

		// Act — walk every page and accumulate the ids returned
		for (int offset = 0; offset < data.Count; offset += pageSize)
		{
			List<int> page = query
				.ApplySort(SortParams.Default, AllowedColumns, DefaultSort, e => e.Id)
				.Skip(offset)
				.Take(pageSize)
				.Select(e => e.Id)
				.ToList();
			collected.AddRange(page);
		}

		// Assert — each id appears exactly once, in a stable total order
		collected.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
	}

	private sealed class TestEntity
	{
		public int Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public int Age { get; set; }
	}
}
