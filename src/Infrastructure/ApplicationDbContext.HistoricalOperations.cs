using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<HistoricalOperationEntity> HistoricalOperations => Set<HistoricalOperationEntity>();
	public DbSet<HistoricalOperationItemEntity> HistoricalOperationItems => Set<HistoricalOperationItemEntity>();
}
