using Infrastructure.Entities.CategoryAssistance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class ApplicationDbContext
{
	public DbSet<CategoryAssistanceSettingsEntity> CategoryAssistanceSettings => Set<CategoryAssistanceSettingsEntity>();
	public DbSet<CategoryAssistanceSettingsAuditEntity> CategoryAssistanceSettingsAudits => Set<CategoryAssistanceSettingsAuditEntity>();
	public DbSet<CategoryAssistanceObservationEntity> CategoryAssistanceObservations => Set<CategoryAssistanceObservationEntity>();
}
