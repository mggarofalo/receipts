using Infrastructure.Entities.Envelopes;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<EnvelopeEntity> Envelopes => Set<EnvelopeEntity>();
	public DbSet<OccasionEntity> Occasions => Set<OccasionEntity>();
	public DbSet<ItemTypeEnvelopeDefaultEntity> ItemTypeEnvelopeDefaults => Set<ItemTypeEnvelopeDefaultEntity>();
	public DbSet<YnabEnvelopeMappingEntity> YnabEnvelopeMappings => Set<YnabEnvelopeMappingEntity>();
	public DbSet<EnvelopeBackfillEntity> EnvelopeBackfills => Set<EnvelopeBackfillEntity>();
}
