using Microsoft.AspNetCore.Identity;

namespace Infrastructure.Entities;

public class ApplicationUser : IdentityUser
{
	public string? FirstName { get; set; }
	public string? LastName { get; set; }
	public string? RefreshToken { get; set; }
	/// <summary>Null only for sessions issued before refresh-family tracking was introduced.</summary>
	public Guid? RefreshSessionId { get; set; }
	public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
	public bool MustResetPassword { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? LastLoginAt { get; set; }
	public virtual ICollection<ApiKeyEntity> ApiKeys { get; set; } = [];
}
