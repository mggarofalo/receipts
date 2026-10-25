using Infrastructure.Entities.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<PurchaseClassificationEntity> Classifications => Set<PurchaseClassificationEntity>();
	public DbSet<CurrentClassificationEntity> CurrentClassifications => Set<CurrentClassificationEntity>();
	public DbSet<ClassificationOperationEntity> ClassificationOperations => Set<ClassificationOperationEntity>();
}
