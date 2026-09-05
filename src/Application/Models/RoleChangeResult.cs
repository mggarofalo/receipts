namespace Application.Models;

public enum RoleChangeMode { Add, Remove, Replace }

public enum RoleChangeStatus { Success, NotFound, Invalid, Conflict }

public sealed record UserProfileUpdate(string Email, string? FirstName, string? LastName, bool IsDisabled);

public sealed record RoleChangeResult(RoleChangeStatus Status, IReadOnlyList<string> Errors)
{
	public static RoleChangeResult Success { get; } = new(RoleChangeStatus.Success, []);
	public static RoleChangeResult NotFound { get; } = new(RoleChangeStatus.NotFound, []);
	public static RoleChangeResult Invalid(params string[] errors) => new(RoleChangeStatus.Invalid, errors);
	public static RoleChangeResult Conflict(params string[] errors) => new(RoleChangeStatus.Conflict, errors);
}
