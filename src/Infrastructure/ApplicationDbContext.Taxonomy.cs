using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<TaxonomyIdentityEntity> TaxonomyIdentities => Set<TaxonomyIdentityEntity>();
	public DbSet<TaxonomyHeadEntity> TaxonomyHeads => Set<TaxonomyHeadEntity>();
	public DbSet<TaxonomyRevisionEntity> TaxonomyRevisions => Set<TaxonomyRevisionEntity>();
	public DbSet<TaxonomyNodeEntity> TaxonomyNodes => Set<TaxonomyNodeEntity>();
	public DbSet<TaxonomyOperationEntity> TaxonomyOperations => Set<TaxonomyOperationEntity>();
}
