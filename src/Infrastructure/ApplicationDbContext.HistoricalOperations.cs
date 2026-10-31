using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<HistoricalOperationEntity> HistoricalOperations => Set<HistoricalOperationEntity>();
	public DbSet<HistoricalOperationItemEntity> HistoricalOperationItems => Set<HistoricalOperationItemEntity>();
	public DbSet<HistoricalGroupDispositionEntity> HistoricalGroupDispositions => Set<HistoricalGroupDispositionEntity>();
	public DbSet<HistoricalDispositionOperationEntity> HistoricalDispositionOperations => Set<HistoricalDispositionOperationEntity>();
	public DbSet<RestructureApplicationEntity> RestructureApplications => Set<RestructureApplicationEntity>();
	public DbSet<RestructureItemEntity> RestructureItems => Set<RestructureItemEntity>();
	public DbSet<RestructureChangeEntity> RestructureChanges => Set<RestructureChangeEntity>();
}
