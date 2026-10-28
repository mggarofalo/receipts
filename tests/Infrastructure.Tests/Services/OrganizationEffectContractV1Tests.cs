using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Infrastructure.Services;

namespace Infrastructure.Tests.Services;

public sealed class OrganizationEffectContractV1Tests
{
	[Fact]
	public void RequestRejectsUnpairedSurrogatesBeforeJsonReplacementCanCollide()
	{
		JsonSerializer.Serialize("\uD800").Should().Be(JsonSerializer.Serialize("\uFFFD"));
		var request = Request();
		foreach (string invalid in new[] { "\uD800", "\uDC00", "prefix\uD800suffix" })
		{
			OrganizationEffectContractV1.ItemRequest[] changes =
			[
				request with { Actor = invalid }, request with { PolicyVersion = invalid },
				request with { Source = request.Source with { Store = invalid } },
				request with { Source = request.Source with { RawDescription = invalid } },
				request with { Source = request.Source with { ItemCode = invalid } }
			];
			foreach (var changed in changes)
			{
				FluentActions.Invoking(() => OrganizationEffectContractV1.RequestHash(changed))
					.Should().Throw<InvalidDataException>().WithMessage("*surrogate*");
			}
		}
		FluentActions.Invoking(() => OrganizationEffectContractV1.RequestHash(request with
		{ Source = request.Source with { RawDescription = "Synthetic \U0001F600" } })).Should().NotThrow();
	}

	private static OrganizationEffectContractV1.ItemRequest Request()
	{
		Guid category = Guid.Parse("00000000-0000-0000-0000-000000000001");
		return new(1, Guid.NewGuid(), "synthetic-actor", "candidate-policy",
			DateTimeOffset.Parse("2026-10-28T12:00:00.123456Z"),
			new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic store", "Synthetic item", null,
				Guid.NewGuid(), Guid.NewGuid()), null,
			new(category, Guid.NewGuid(), [category]), Guid.NewGuid(), Guid.NewGuid(),
			new(1, "approved-exact-mapping", Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)));
	}

	[Fact]
	public void RequestHashIsStableAcrossObjectOrderAndKeepsExplicitNulls()
	{
		var request = Request();
		string canonical = OrganizationEffectContractV1.WriteRequest(request);
		var root = JsonNode.Parse(canonical)!.AsObject();
		var reverse = new JsonObject(root.Reverse().Select(p =>
			new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
		var parsed = OrganizationEffectContractV1.ReadRequest(reverse.ToJsonString());
		OrganizationEffectContractV1.RequestHash(parsed).Should().Be(OrganizationEffectContractV1.RequestHash(request));
		canonical.Should().Contain("\"ExpectedAssignmentId\":null").And.Contain("\"ItemCode\":null")
			.And.Contain("2026-10-28T12:00:00.123456Z");
		root.Remove("ExpectedAssignmentId");
		FluentActions.Invoking(() => OrganizationEffectContractV1.ReadRequest(root.ToJsonString()))
			.Should().Throw<JsonException>();
	}

	[Fact]
	public void RequestHashBindsSourceRevisionsOperationAndMappingClaims()
	{
		var request = Request();
		string hash = OrganizationEffectContractV1.RequestHash(request);
		OrganizationEffectContractV1.ItemRequest[] changes =
		[
			request with { Source = request.Source with { Store = "Synthetic Store" } },
			request with { Source = request.Source with { RawDescription = "Synthetic item " } },
			request with { Source = request.Source with { ItemCode = "" } },
			request with { Source = request.Source with { ItemRevision = Guid.NewGuid() } },
			request with { Source = request.Source with { ReceiptRevision = Guid.NewGuid() } },
			request with { ApplyOperationId = Guid.NewGuid() },
			request with { UndoOperationId = Guid.NewGuid() },
			request with { Target = request.Target with { TaxonomyRevisionId = Guid.NewGuid() } },
			request with { Target = request.Target with { Path = [request.Target.CategoryId!.Value, Guid.NewGuid()] } },
			request with { Decision = request.Decision with { MappingRevisionId = Guid.NewGuid() } },
			request with { Decision = request.Decision with { ApprovalTaxonomyRevisionId = Guid.NewGuid() } },
			request with { Decision = request.Decision with { SourceFingerprint = new string('B', 64) } },
			request with { ExpectedAssignmentId = Guid.NewGuid() },
			request with { Actor = "another-synthetic-actor" },
			request with { CapturedAt = request.CapturedAt.AddTicks(10) }
		];
		foreach (var changed in changes)
		{
			OrganizationEffectContractV1.RequestHash(changed).Should().NotBe(hash);
		}
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"Version\":1,\"Kind\":\"approved-exact-mapping\"}")]
	[InlineData("{\"Version\":1,\"Version\":1}")]
	public void DecisionRejectsOpaqueIncompleteOrDuplicateEvidence(string json)
	{
		FluentActions.Invoking(() => OrganizationEffectContractV1.ReadDecision(json)).Should().Throw<Exception>();
	}

	[Fact]
	public void DecisionRejectsUnknownKeysVersionsKindsAndNullReferences()
	{
		var decision = Request().Decision;
		var json = JsonSerializer.SerializeToNode(decision)!.AsObject();
		json["Approved"] = true;
		FluentActions.Invoking(() => OrganizationEffectContractV1.ReadDecision(json.ToJsonString()))
			.Should().Throw<JsonException>();
		json.Remove("Approved");
		foreach (var mutation in new[] { decision with { Version = 2 }, decision with { Kind = "opaque" },
			decision with { MappingRevisionId = Guid.Empty }, decision with { SourceFingerprint = null! } })
		{
			FluentActions.Invoking(() => OrganizationEffectContractV1.ReadDecision(JsonSerializer.Serialize(mutation)))
				.Should().Throw<InvalidDataException>();
		}
		OrganizationEffectContractV1.ReadDecision(json.ToJsonString()).Should().Be(decision);
	}

	[Fact]
	public void TimestampRejectsSubMicrosecondsAndNonUtcWithoutRounding()
	{
		var request = Request();
		foreach (var time in new[] { request.CapturedAt.AddTicks(1), request.CapturedAt.ToOffset(TimeSpan.FromHours(1)) })
		{
			FluentActions.Invoking(() => OrganizationEffectContractV1.RequestHash(request with { CapturedAt = time }))
				.Should().Throw<InvalidDataException>();
			var json = JsonSerializer.SerializeToNode(request)!;
			json["CapturedAt"] = time;
			FluentActions.Invoking(() => OrganizationEffectContractV1.ReadRequest(json.ToJsonString()))
				.Should().Throw<InvalidDataException>();
		}
		var excessivePrecision = JsonSerializer.SerializeToNode(request)!;
		excessivePrecision["CapturedAt"] = "2026-10-28T12:00:00.12345601Z";
		FluentActions.Invoking(() => OrganizationEffectContractV1.ReadRequest(excessivePrecision.ToJsonString()))
			.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void PhaseHashBindsPhaseRequestOperationPriorIntentAndTime()
	{
		var request = Request();
		var command = new OrganizationEffectContractV1.PhaseCommand(1, "apply",
			OrganizationEffectContractV1.RequestHash(request), request.ApplyOperationId, Guid.NewGuid(),
			request.Target, request.CapturedAt);
		string hash = OrganizationEffectContractV1.PhaseHash(command);
		OrganizationEffectContractV1.PhaseCommand[] changes =
		[
			command with { Phase = "undo" },
			command with { OperationId = request.UndoOperationId },
			command with { ItemRequestHash = new string('B', 64) },
			command with { ExpectedAssignmentId = Guid.NewGuid() },
			command with { Target = command.Target with { TaxonomyRevisionId = Guid.NewGuid() } },
			command with { RecordedAt = command.RecordedAt.AddTicks(10) }
		];
		foreach (var changed in changes) { OrganizationEffectContractV1.PhaseHash(changed).Should().NotBe(hash); }
		hash.Should().NotBe(command.ItemRequestHash);
		OrganizationEffectContractV1.PhaseHash(OrganizationEffectContractV1.ReadPhase(OrganizationEffectContractV1.WritePhase(command)))
			.Should().Be(hash);
		var undo = command with { Phase = "undo", Target = new(null, request.Target.TaxonomyRevisionId, []) };
		FluentActions.Invoking(() => OrganizationEffectContractV1.PhaseHash(undo)).Should().NotThrow();
		FluentActions.Invoking(() => OrganizationEffectContractV1.PhaseHash(undo with { ExpectedAssignmentId = null }))
			.Should().Throw<InvalidDataException>();
	}
}
