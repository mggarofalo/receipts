using API.Authentication;
using FluentAssertions;
using Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Authentication;

public class JwtSecurityStampValidatorTests
{
	private readonly Mock<UserManager<ApplicationUser>> _userManagerMock;

	public JwtSecurityStampValidatorTests()
	{
		Mock<IUserStore<ApplicationUser>> userStoreMock = new();
		_userManagerMock = new Mock<UserManager<ApplicationUser>>(
			userStoreMock.Object,
			new Mock<IOptions<IdentityOptions>>().Object,
			new Mock<IPasswordHasher<ApplicationUser>>().Object,
			Array.Empty<IUserValidator<ApplicationUser>>(),
			Array.Empty<IPasswordValidator<ApplicationUser>>(),
			new Mock<ILookupNormalizer>().Object,
			new Mock<IdentityErrorDescriber>().Object,
			new Mock<IServiceProvider>().Object,
			new Mock<ILogger<UserManager<ApplicationUser>>>().Object);
	}

	private static ApplicationUser CreateUser(string id = "user-123", string stamp = "stamp-current")
	{
		return new ApplicationUser
		{
			Id = id,
			Email = "test@example.com",
			UserName = "test@example.com",
			SecurityStamp = stamp,
		};
	}

	[Fact]
	public async Task EvaluateAsync_MatchingStamp_ReturnsValid()
	{
		// Arrange — the token's stamp equals the user's live stamp, and the account is not locked.
		ApplicationUser user = CreateUser(stamp: "stamp-current");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);

		// Act
		SecurityStampRevalidationResult result = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", "stamp-current");

		// Assert
		result.IsValid.Should().BeTrue();
		result.FailureReason.Should().BeNull();
	}

	[Fact]
	public async Task EvaluateAsync_StampNoLongerMatches_ReturnsInvalid()
	{
		// Arrange — the user's stamp was rotated (e.g. deactivation/password reset) after the token was issued.
		ApplicationUser user = CreateUser(stamp: "stamp-rotated");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(false);

		// Act — token still carries the old stamp.
		SecurityStampRevalidationResult result = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", "stamp-old");

		// Assert
		result.IsValid.Should().BeFalse();
		result.FailureReason.Should().Contain("Security stamp");
	}

	[Fact]
	public async Task EvaluateAsync_AbsentStampClaim_ReturnsInvalid()
	{
		// Arrange — a token minted before this feature shipped carries no security_stamp claim.
		// It must fail closed; the user re-logs in once after deploy.
		SecurityStampRevalidationResult resultNull = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", null);
		SecurityStampRevalidationResult resultEmpty = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", "");

		// Assert — never hits the database; rejected on the missing claim alone.
		resultNull.IsValid.Should().BeFalse();
		resultEmpty.IsValid.Should().BeFalse();
		_userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
	}

	[Fact]
	public async Task EvaluateAsync_MissingUserId_ReturnsInvalid()
	{
		// Act
		SecurityStampRevalidationResult result = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, null, "stamp-current");

		// Assert
		result.IsValid.Should().BeFalse();
		_userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
	}

	[Fact]
	public async Task EvaluateAsync_UserNoLongerExists_ReturnsInvalid()
	{
		// Arrange — the account was hard-deleted after the token was issued.
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync((ApplicationUser?)null);

		// Act
		SecurityStampRevalidationResult result = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", "stamp-current");

		// Assert
		result.IsValid.Should().BeFalse();
		result.FailureReason.Should().Contain("no longer exists");
	}

	[Fact]
	public async Task EvaluateAsync_LockedOutUser_ReturnsInvalid_EvenWhenStampMatches()
	{
		// Arrange — a deactivated/locked account is rejected regardless of the stamp comparison.
		ApplicationUser user = CreateUser(stamp: "stamp-current");
		_userManagerMock.Setup(m => m.FindByIdAsync("user-123")).ReturnsAsync(user);
		_userManagerMock.Setup(m => m.IsLockedOutAsync(user)).ReturnsAsync(true);

		// Act
		SecurityStampRevalidationResult result = await JwtSecurityStampValidator.EvaluateAsync(
			_userManagerMock.Object, "user-123", "stamp-current");

		// Assert
		result.IsValid.Should().BeFalse();
		result.FailureReason.Should().Contain("disabled");
	}
}
