using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Infrastructure.Ynab.OAuth;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.Tests.Ynab;

public class YnabAuthorizationAttemptStoreTests
{
	private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

	private readonly FakeTimeProvider _time = new(Start);
	private readonly YnabAuthorizationAttemptStore _store;

	public YnabAuthorizationAttemptStoreTests()
	{
		_store = new YnabAuthorizationAttemptStore(_time);
	}

	[Fact]
	public void Create_BindsTheAttemptToTheUserForTenMinutes()
	{
		// Act
		YnabAuthorizationAttempt attempt = _store.Create("admin-1");

		// Assert
		attempt.UserId.Should().Be("admin-1");
		attempt.ExpiresAtUtc.Should().Be(Start.AddMinutes(10));
		attempt.State.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void Create_CalledTwice_GivesEachAttemptItsOwnStateAndVerifier()
	{
		// Act
		YnabAuthorizationAttempt first = _store.Create("admin-1");
		YnabAuthorizationAttempt second = _store.Create("admin-1");

		// Assert
		second.State.Should().NotBe(first.State);
		second.CodeVerifier.Should().NotBe(first.CodeVerifier);
	}

	[Fact]
	public void Create_Challenge_IsTheBase64UrlSha256OfTheVerifier()
	{
		// Act
		YnabAuthorizationAttempt attempt = _store.Create("admin-1");

		// Assert — RFC 7636 section 4.2, computed here without the production helper.
		string expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.CodeVerifier)))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');
		attempt.CodeChallenge.Should().Be(expected);
	}

	[Fact]
	public void Create_Verifier_IsWithinThePkceLengthAndAlphabet()
	{
		// Act
		YnabAuthorizationAttempt attempt = _store.Create("admin-1");

		// Assert — RFC 7636 section 4.1: 43 to 128 unreserved characters.
		attempt.CodeVerifier.Length.Should().BeInRange(43, 128);
		attempt.CodeVerifier.Should().MatchRegex("^[A-Za-z0-9._~-]+$");
	}

	[Fact]
	public void Take_UnknownState_ReturnsNull()
	{
		// Arrange
		_store.Create("admin-1");

		// Act
		YnabAuthorizationAttempt? taken = _store.Take("never-issued-state", "admin-1");

		// Assert
		taken.Should().BeNull();
	}

	[Fact]
	public void Take_ByTheUserWhoStartedIt_ReturnsTheAttempt()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");

		// Act
		YnabAuthorizationAttempt? taken = _store.Take(created.State, "admin-1");

		// Assert
		taken.Should().Be(created);
	}

	[Fact]
	public void Take_SecondTime_ReturnsNull()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		_store.Take(created.State, "admin-1");

		// Act
		YnabAuthorizationAttempt? again = _store.Take(created.State, "admin-1");

		// Assert
		again.Should().BeNull();
	}

	[Fact]
	public void Take_JustBeforeExpiry_ReturnsTheAttempt()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		_time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

		// Act
		YnabAuthorizationAttempt? taken = _store.Take(created.State, "admin-1");

		// Assert
		taken.Should().Be(created);
	}

	[Theory]
	[InlineData(600)]
	[InlineData(601)]
	[InlineData(86400)]
	public void Take_AtOrAfterExpiry_ReturnsNull(int secondsLater)
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		_time.Advance(TimeSpan.FromSeconds(secondsLater));

		// Act
		YnabAuthorizationAttempt? taken = _store.Take(created.State, "admin-1");

		// Assert
		taken.Should().BeNull();
	}

	[Fact]
	public void Take_ByADifferentUser_ReturnsNullAndLeavesTheAttemptForItsOwner()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");

		// Act
		YnabAuthorizationAttempt? takenByOther = _store.Take(created.State, "admin-2");
		YnabAuthorizationAttempt? takenByOwner = _store.Take(created.State, "admin-1");

		// Assert
		takenByOther.Should().BeNull();
		takenByOwner.Should().Be(created);
	}

	[Fact]
	public void Restore_TakenAttempt_CanBeTakenAgainUntilItExpires()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		YnabAuthorizationAttempt taken = _store.Take(created.State, "admin-1")!;

		// Act
		_store.Restore(taken);
		_time.Advance(TimeSpan.FromMinutes(9));
		YnabAuthorizationAttempt? retaken = _store.Take(created.State, "admin-1");

		// Assert
		retaken.Should().Be(created);
	}

	[Fact]
	public void Restore_DoesNotExtendTheOriginalExpiry()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		_time.Advance(TimeSpan.FromMinutes(9));
		YnabAuthorizationAttempt taken = _store.Take(created.State, "admin-1")!;
		_store.Restore(taken);

		// Act
		_time.Advance(TimeSpan.FromMinutes(1));
		YnabAuthorizationAttempt? retaken = _store.Take(created.State, "admin-1");

		// Assert
		retaken.Should().BeNull();
	}

	[Fact]
	public void Restore_AfterExpiry_DoesNotBringTheAttemptBack()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		YnabAuthorizationAttempt taken = _store.Take(created.State, "admin-1")!;
		_time.Advance(TimeSpan.FromMinutes(10));

		// Act
		_store.Restore(taken);
		YnabAuthorizationAttempt? retaken = _store.Take(created.State, "admin-1");

		// Assert
		retaken.Should().BeNull();
	}

	[Fact]
	public void Restore_StillBoundToTheUserWhoStartedIt()
	{
		// Arrange
		YnabAuthorizationAttempt created = _store.Create("admin-1");
		_store.Restore(_store.Take(created.State, "admin-1")!);

		// Act
		YnabAuthorizationAttempt? takenByOther = _store.Take(created.State, "admin-2");

		// Assert
		takenByOther.Should().BeNull();
	}

	[Fact]
	public void Create_ManyAbandonedAttempts_KeepsTheNewestUsable()
	{
		// Arrange — far more starts than the store keeps; memory must stay bounded without
		// costing the Admin the attempt they just started.
		for (int i = 0; i < 100; i++)
		{
			_store.Create("admin-1");
			_time.Advance(TimeSpan.FromMilliseconds(1));
		}

		// Act
		YnabAuthorizationAttempt latest = _store.Create("admin-1");
		YnabAuthorizationAttempt? taken = _store.Take(latest.State, "admin-1");

		// Assert
		taken.Should().Be(latest);
	}

	[Fact]
	public void Attempt_ToString_ContainsNeitherStateNorVerifier()
	{
		// Arrange
		YnabAuthorizationAttempt attempt = _store.Create("admin-1");

		// Act
		string text = attempt.ToString();

		// Assert
		text.Should().NotContain(attempt.State).And.NotContain(attempt.CodeVerifier).And.NotContain(attempt.CodeChallenge);
	}
}
