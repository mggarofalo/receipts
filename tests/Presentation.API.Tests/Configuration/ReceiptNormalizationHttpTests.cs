using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using API.Configuration;
using API.Controllers.Core;
using API.Middleware;
using API.Services;
using Application.Commands.ReceiptItem.Create;
using Application.Commands.ReceiptItem.Update;
using Application.Normalization;
using Domain.NormalizedDescriptions;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Presentation.API.Tests.Configuration;

public sealed class ReceiptNormalizationHttpTests
{
	[Theory]
	[InlineData(false, "omitted")]
	[InlineData(false, "null")]
	[InlineData(false, "clear")]
	[InlineData(true, "omitted")]
	[InlineData(true, "null")]
	[InlineData(true, "clear")]
	public async Task ReceiptItemWritePreservesOptionalClassificationAndExplicitClear(bool update, string classificationKind)
	{
		Guid itemId = Guid.NewGuid(), receiptId = Guid.NewGuid();
		Domain.Taxonomy.ClassificationIntent? captured = null;
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		if (update)
		{
			mediator.Setup(x => x.Send(It.IsAny<UpdateReceiptItemCommand>(), It.IsAny<CancellationToken>()))
				.Callback<IRequest<bool>, CancellationToken>((command, _) => captured = ((UpdateReceiptItemCommand)command).ReceiptItems.Single().Classification)
				.ReturnsAsync(true);
		}
		else
		{
			mediator.Setup(x => x.Send(It.IsAny<CreateReceiptItemCommand>(), It.IsAny<CancellationToken>()))
				.Callback<IRequest<List<Domain.Core.ReceiptItem>>, CancellationToken>((command, _) => captured = ((CreateReceiptItemCommand)command).ReceiptItems.Single().Classification)
				.ReturnsAsync((IRequest<List<Domain.Core.ReceiptItem>> command, CancellationToken _) => ((CreateReceiptItemCommand)command).ReceiptItems.ToList());
		}
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		var body = new Dictionary<string, object?>
		{
			["id"] = itemId,
			["description"] = "FRANKS",
			["quantity"] = 1,
			["unitPrice"] = 2,
			["category"] = "Food"
		};
		if (classificationKind == "null") { body["classification"] = null; }
		Guid revisionId = Guid.NewGuid(), operationId = Guid.NewGuid();
		if (classificationKind == "clear")
		{
			body["classification"] = new { categoryId = (Guid?)null, expectedAssignmentId = (Guid?)null, taxonomyRevisionId = revisionId, operationId, reason = "Explicit clear" };
		}

		using var response = update
			? await client.PutAsJsonAsync($"/api/receipt-items/{itemId}", body)
			: await client.PostAsJsonAsync($"/api/receipts/{receiptId}/receipt-items", body);

		response.StatusCode.Should().Be(update ? HttpStatusCode.NoContent : HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
		if (classificationKind == "clear")
		{
			captured.Should().Be(new Domain.Taxonomy.ClassificationIntent(null, revisionId, null, operationId, "Explicit clear"));
		}
		else { captured.Should().BeNull(); }
		mediator.VerifyAll();
	}

	[Theory]
	[InlineData(false, "categoryId")]
	[InlineData(false, "expectedAssignmentId")]
	[InlineData(true, "categoryId")]
	[InlineData(true, "expectedAssignmentId")]
	public async Task PresentClassificationRequiresNullableConcurrencyFields(bool update, string omittedField)
	{
		Guid itemId = Guid.NewGuid();
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		var classification = new Dictionary<string, object?>
		{
			["categoryId"] = null,
			["expectedAssignmentId"] = null,
			["taxonomyRevisionId"] = Guid.NewGuid(),
			["operationId"] = Guid.NewGuid(),
			["reason"] = "Explicit choice"
		};
		classification.Remove(omittedField);
		var body = new { id = itemId, description = "FRANKS", quantity = 1, unitPrice = 2, category = "Food", classification };

		using var response = update
			? await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={Guid.NewGuid()}", body)
			: await client.PostAsJsonAsync($"/api/receipts/{Guid.NewGuid()}/receipt-items?clientRequestId={Guid.NewGuid()}", body);

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
		(await response.Content.ReadAsStringAsync()).Should().Contain(omittedField);
		mediator.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task ReviewEditForwardsReplayKeyAndObservedItemRevision()
	{
		Guid itemId = Guid.NewGuid(), receiptId = Guid.NewGuid(), requestId = Guid.NewGuid(), itemRevision = Guid.NewGuid(), rowId = Guid.NewGuid();
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		mediator.Setup(x => x.Send(It.Is<UpdateReceiptItemCommand>(command => command.ClientRequestId == requestId
			&& command.ReceiptItems.Count == 1 && command.ReceiptItems[0].Id == itemId
			&& command.ReceiptItems[0].NormalizationIntent!.Kind == ReceiptNormalizationIntentKind.Defer
			&& command.ReceiptItems[0].NormalizationIntent!.ExpectedItemRevision == itemRevision
			&& command.ReceiptItems[0].NormalizationIntent!.ClientRowId == rowId), It.IsAny<CancellationToken>())).ReturnsAsync(true);
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();

		using var response = await client.PutAsJsonAsync($"/api/receipt-items/{itemId}?clientRequestId={requestId}", new
		{
			id = itemId,
			receiptId,
			description = "FRANKS",
			quantity = 1,
			unitPrice = 2,
			category = "Food",
			normalizationIntent = new { kind = "defer", clientRowId = rowId, sourceFingerprint = new string('a', 64), expectedItemRevision = itemRevision }
		});

		response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
		mediator.VerifyAll();
	}

	[Fact]
	public async Task PreviewRequiresAuthenticationButNotCatalogAdministratorRole()
	{
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		var preview = new Mock<IReceiptNormalizationPreview>(MockBehavior.Strict);
		var source = new NormalizationSourceKey("Store A", "FRANKS", null);
		preview.Setup(x => x.PreviewAsync(source, "franks", 0, 10, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ReceiptNormalizationPreview("observed-source", null, null, null, null, null, false, [], 0, 0, 10));
		await using var app = await Start(mediator, preview: preview, enforceAuthentication: true);
		using var client = app.GetTestClient();
		var request = new { source, query = "franks", offset = 0, limit = 10 };

		using var anonymous = await client.PostAsJsonAsync("/api/receipt-items/normalization-preview", request);
		anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		preview.Verify(x => x.PreviewAsync(It.IsAny<NormalizationSourceKey>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
		client.DefaultRequestHeaders.Add("Test-User", "receipt-writer");
		using var ordinary = await client.PostAsJsonAsync("/api/receipt-items/normalization-preview", request);
		ordinary.StatusCode.Should().Be(HttpStatusCode.OK);
		using var json = JsonDocument.Parse(await ordinary.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("sourceFingerprint").GetString().Should().Be("observed-source");
		preview.VerifyAll();
		mediator.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task ReceiptItemCreatePreservesOptionalIntentAndStableRequestIdentity(bool explicitReview, bool explicitNull)
	{
		Guid receiptId = Guid.NewGuid(), requestId = Guid.NewGuid(), rowId = Guid.NewGuid(), target = Guid.NewGuid(), revision = Guid.NewGuid();
		CreateReceiptItemCommand? captured = null;
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		mediator.Setup(x => x.Send(It.IsAny<CreateReceiptItemCommand>(), It.IsAny<CancellationToken>()))
			.Callback<IRequest<List<Domain.Core.ReceiptItem>>, CancellationToken>((command, _) => captured = (CreateReceiptItemCommand)command)
			.ReturnsAsync((IRequest<List<Domain.Core.ReceiptItem>> command, CancellationToken _) => ((CreateReceiptItemCommand)command).ReceiptItems.ToList());
		await using var app = await Start(mediator);
		using var client = app.GetTestClient();
		var body = new Dictionary<string, object?>
		{
			["description"] = "BF FRANKS",
			["receiptItemCode"] = "123",
			["quantity"] = 2,
			["unitPrice"] = 3.125,
			["category"] = "Food",
			["normalizationReviewState"] = "verified",
			["reviewedBy"] = "forged-admin",
			["normalizedDescriptionId"] = target
		};
		if (explicitNull) { body["normalizationIntent"] = null; }
		if (explicitReview)
		{
			body["normalizationIntent"] = new { kind = "confirmExisting", clientRowId = rowId, sourceFingerprint = new string('a', 64), targetId = target, expectedTargetRevision = revision, rememberSource = true };
		}

		using var response = await client.PostAsJsonAsync($"/api/receipts/{receiptId}/receipt-items?clientRequestId={requestId}", body);

		response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
		captured.Should().NotBeNull();
		captured!.ReceiptId.Should().Be(receiptId);
		captured.ClientRequestId.Should().Be(requestId);
		var item = captured.ReceiptItems.Should().ContainSingle().Subject;
		item.Description.Should().Be("BF FRANKS");
		item.Quantity.Should().Be(2);
		item.NormalizationReviewState.Should().Be(NormalizationReviewState.NeedsReview, "transport must not manufacture persisted proof from client-supplied state");
		item.NormalizedDescriptionId.Should().BeNull();
		if (explicitReview)
		{
			item.NormalizationIntent.Should().Be(new ReceiptNormalizationIntent(ReceiptNormalizationIntentKind.ConfirmExisting, rowId, new string('a', 64), target, revision, RememberSource: true));
		}
		else { item.NormalizationIntent.Should().BeNull("older clients must remain able to save without approving an identity"); }
		mediator.VerifyAll();
	}

	[Theory]
	[InlineData(400, "invalid_normalization_intent")]
	[InlineData(403, "review_actor_required")]
	[InlineData(409, "normalization_target_changed")]
	public async Task EntryDecisionFailureUsesReasonedProblemWithoutPublishingCreatedEvent(int status, string code)
	{
		Guid target = Guid.NewGuid();
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		mediator.Setup(x => x.Send(It.IsAny<CreateReceiptItemCommand>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationCatalogException(status, code, "Review this receipt choice again.", new Dictionary<string, object?> { ["normalizedDescriptionId"] = target }));
		var notifier = new Mock<IEntityChangeNotifier>(MockBehavior.Strict);
		await using var app = await Start(mediator, notifier);
		using var client = app.GetTestClient();

		using var response = await client.PostAsJsonAsync($"/api/receipts/{Guid.NewGuid()}/receipt-items", new { description = "FRANKS", quantity = 1, unitPrice = 2, category = "Food" });

		((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		json.RootElement.GetProperty("status").GetInt32().Should().Be(status);
		json.RootElement.GetProperty("detail").GetString().Should().Be("Review this receipt choice again.");
		json.RootElement.GetProperty("code").GetString().Should().Be(code);
		json.RootElement.GetProperty("normalizedDescriptionId").GetGuid().Should().Be(target);
		notifier.VerifyNoOtherCalls();
		mediator.VerifyAll();
	}

	[Theory]
	[InlineData("User")]
	[InlineData("Admin")]
	public async Task EntryCandidatesRequireAuthenticationAndReturnOnlyAdvisoryIdentity(string role)
	{
		var source = new NormalizationSourceKey("Store A", "FRANKS", "001");
		Guid target = Guid.NewGuid(), revision = Guid.NewGuid();
		var candidates = new Mock<IReceiptNormalizationCandidates>(MockBehavior.Strict);
		candidates.Setup(x => x.GetCandidatesAsync("receipt-writer", source, It.IsAny<CancellationToken>()))
			.ReturnsAsync([new ReceiptNormalizationSuggestion(target, revision, "Franks", "Beef franks", ["lexical"])]);
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		await using var app = await Start(mediator, enforceAuthentication: true, candidates: candidates);
		using var client = app.GetTestClient();
		const string url = "/api/receipt-items/normalization-candidates?location=Store%20A&description=FRANKS&itemCode=001";
		using var anonymous = await client.GetAsync(url);
		anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		candidates.VerifyNoOtherCalls();
		client.DefaultRequestHeaders.Add("Test-User", "receipt-writer");
		client.DefaultRequestHeaders.Add("Test-Role", role);
		using var response = await client.GetAsync(url);
		response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var item = json.RootElement.EnumerateArray().Should().ContainSingle().Subject;
		item.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "revision", "canonicalName", "displayLabel", "channels");
		item.GetProperty("id").GetGuid().Should().Be(target);
		item.GetProperty("revision").GetGuid().Should().Be(revision);
		candidates.VerifyAll();
		mediator.VerifyNoOtherCalls();
	}

	[Fact]
	public async Task EntryCandidateServiceRejectionReturnsReasonedProblemWithoutCandidatePayload()
	{
		var candidates = new Mock<IReceiptNormalizationCandidates>(MockBehavior.Strict);
		candidates.Setup(x => x.GetCandidatesAsync("receipt-writer", new NormalizationSourceKey("Store A", "FRANKS", "001"), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new NormalizationCatalogException(403, "receipt_entry_actor_required", "Sign in to retrieve receipt product suggestions."));
		var mediator = new Mock<IMediator>(MockBehavior.Strict);
		await using var app = await Start(mediator, enforceAuthentication: true, candidates: candidates);
		using var client = app.GetTestClient();
		client.DefaultRequestHeaders.Add("Test-User", "receipt-writer");

		using var response = await client.GetAsync("/api/receipt-items/normalization-candidates?location=Store%20A&description=FRANKS&itemCode=001");

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
		response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var problem = json.RootElement;
		problem.GetProperty("status").GetInt32().Should().Be(403);
		problem.GetProperty("detail").GetString().Should().Be("Sign in to retrieve receipt product suggestions.");
		problem.GetProperty("code").GetString().Should().Be("receipt_entry_actor_required");
		problem.EnumerateObject().Select(property => property.Name).Should().NotIntersectWith([
			"candidates", "data", "id", "revision", "canonicalName", "displayLabel", "channels"]);
		candidates.VerifyAll();
		mediator.VerifyNoOtherCalls();
	}

	private static async Task<WebApplication> Start(Mock<IMediator> mediator, Mock<IEntityChangeNotifier>? notifier = null,
		Mock<IReceiptNormalizationPreview>? preview = null, bool enforceAuthentication = false, Mock<IReceiptNormalizationCandidates>? candidates = null)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.Services.AddVersioningServices().AddApplicationServices(builder.Configuration).RegisterProgramServices();
		builder.Services.AddAuthorization();
		builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
		builder.Services.AddProblemDetails();
		builder.Services.AddControllers().AddApplicationPart(typeof(ReceiptItemsController).Assembly);
		builder.Services.AddSingleton(mediator.Object);
		builder.Services.AddSingleton((notifier ?? new Mock<IEntityChangeNotifier>()).Object);
		if (preview is not null) { builder.Services.AddSingleton(preview.Object); }
		if (candidates is not null) { builder.Services.AddSingleton(candidates.Object); }
		var app = builder.Build();
		app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
		app.UseMiddleware<ValidationExceptionMiddleware>();
		app.UseAuthentication();
		app.UseAuthorization();
		if (enforceAuthentication) { app.MapControllers(); }
		else { app.MapControllers().AllowAnonymous(); }
		await app.StartAsync();
		return app;
	}

	private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
		: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
	{
		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.TryGetValue("Test-User", out var user)) { return Task.FromResult(AuthenticateResult.NoResult()); }
			var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Role, Request.Headers["Test-Role"].FirstOrDefault() ?? "User")], Scheme.Name));
			return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
		}
	}
}
