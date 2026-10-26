using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<CategoryAssistanceSettingsEntity> CategoryAssistanceSettings => Set<CategoryAssistanceSettingsEntity>();
	public DbSet<CategoryAssistanceConnectionEntity> CategoryAssistanceConnection => Set<CategoryAssistanceConnectionEntity>();
	public DbSet<CategoryAssistanceSettingsAuditEntity> CategoryAssistanceSettingsAudits => Set<CategoryAssistanceSettingsAuditEntity>();
	public DbSet<CategoryAssistanceObservationEntity> CategoryAssistanceObservations => Set<CategoryAssistanceObservationEntity>();
	public DbSet<CategoryAssistanceAttemptEntity> CategoryAssistanceAttempts => Set<CategoryAssistanceAttemptEntity>();
	public DbSet<CategoryAssistanceDailyUsageEntity> CategoryAssistanceDailyUsage => Set<CategoryAssistanceDailyUsageEntity>();
	public DbSet<CategoryAssistanceReviewPreferenceEntity> CategoryAssistanceReviewPreferences => Set<CategoryAssistanceReviewPreferenceEntity>();
	public DbSet<CategoryAssistanceReviewOperationEntity> CategoryAssistanceReviewOperations => Set<CategoryAssistanceReviewOperationEntity>();
	public DbSet<CategoryAssistanceHistoricalRequestEntity> CategoryAssistanceHistoricalRequests => Set<CategoryAssistanceHistoricalRequestEntity>();
}
