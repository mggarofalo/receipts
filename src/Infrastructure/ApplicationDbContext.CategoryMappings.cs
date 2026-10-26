using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<CategoryMappingHeadEntity> CategoryMappingHeads => Set<CategoryMappingHeadEntity>();
	public DbSet<CategoryMappingRevisionEntity> CategoryMappingRevisions => Set<CategoryMappingRevisionEntity>();
}
