using Infrastructure.Entities.NormalizationAi;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<NormalizationAssistancePreferenceEntity> NormalizationAssistancePreferences { get; set; } = null!;
	public DbSet<NormalizationAssistanceAttemptEntity> NormalizationAssistanceAttempts { get; set; } = null!;
}
