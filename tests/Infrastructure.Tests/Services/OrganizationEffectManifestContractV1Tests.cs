using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Contract = Infrastructure.Services.OrganizationEffectContractV1;

namespace Infrastructure.Tests.Services;

public sealed class OrganizationEffectManifestContractV1Tests
{
	[Fact]
	public void ManifestRoundTripBindsCompleteOrderedMembershipDespiteEqualTimes()
	{
		var request = Request();
		string canonical = Contract.WriteManifestRequest(request);
		var root = JsonNode.Parse(canonical)!.AsObject();
		var reordered = new JsonObject(root.Reverse().Select(pair =>
			new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())));
		string expected = Contract.ManifestRequestHash(request);
		Contract.WriteManifestRequest(Contract.ReadManifestRequest(reordered.ToJsonString())).Should().Be(canonical);
		Contract.ManifestRequestHash(Contract.ReadManifestRequest(canonical)).Should().Be(expected);
		Contract.ManifestRequestHash(request with { Members = [request.Members[1], request.Members[0]] }).Should().NotBe(expected);
		Contract.ManifestRequestHash(request with { Members = [request.Members[0]] }).Should().NotBe(expected);
		Contract.ManifestRequestHash(request with { Members = [] }).Should().NotBe(expected);
		expected.Should().NotBe(Contract.RequestHash(request.Members[0]));
		canonical.Should().Contain("\"ExpectedAssignmentId\":null").And.Contain("\"ItemCode\":null")
			.And.Contain("2026-10-28T12:00:00.123456Z");
	}

	[Fact]
	public void ManifestCodecEnforcesOneMebibyteBoundForTheWholeRequest()
	{
		var request = Request();
		var member = request.Members[0];
		request = request with { Members = [member with { Source = member.Source with { RawDescription = "" } }] };
		int padding = 1024 * 1024 - System.Text.Encoding.UTF8.GetByteCount(Contract.WriteManifestRequest(request));
		member = request.Members[0];
		request = request with { Members = [member with { Source = member.Source with { RawDescription = new string('A', padding) } }] };
		string canonical = Contract.WriteManifestRequest(request);
		System.Text.Encoding.UTF8.GetByteCount(canonical).Should().Be(1024 * 1024);
		Contract.WriteManifestRequest(Contract.ReadManifestRequest(canonical)).Should().Be(canonical);
		FluentActions.Invoking(() => Contract.ReadManifestRequest(canonical + " "))
			.Should().Throw<InvalidDataException>().WithMessage("*size limit*");
		member = request.Members[0];
		FluentActions.Invoking(() => Contract.WriteManifestRequest(request with
		{
			Members = [member with { Source = member.Source with { RawDescription = member.Source.RawDescription + "A" } }]
		})).Should().Throw<InvalidDataException>().WithMessage("*size limit*");
	}

	[Fact]
	public void ManifestHashBindsEveryDeclaredContextField()
	{
		var request = Request();
		Contract.ManifestRequest[] changes =
		[
			Recontext(request with { ManifestId = Guid.NewGuid() }),
			Recontext(request with { Actor = "another synthetic actor" }),
			Recontext(request with { PolicyVersion = "candidate-v2" }),
			Recontext(request with { TaxonomyRevisionId = Guid.NewGuid() }),
			Recontext(request with { CapturedAt = request.CapturedAt.AddTicks(10) })
		];
		foreach (var changed in changes)
		{
			Contract.ManifestRequestHash(changed).Should().NotBe(Contract.ManifestRequestHash(request));
		}
	}

	[Fact]
	public void ManifestHashRetainsExactTypedMemberData()
	{
		var request = Request();
		var member = request.Members[0];
		Contract.ItemRequest[] changes =
		[
			member with { Source = member.Source with { ReceiptId = Guid.NewGuid() } },
			member with { Source = member.Source with { ItemId = Guid.NewGuid() } },
			member with { Source = member.Source with { ReceiptRevision = Guid.NewGuid() } },
			member with { Source = member.Source with { ItemRevision = Guid.NewGuid() } },
			member with { Source = member.Source with { Store = "Synthetic store " } },
			member with { Source = member.Source with { RawDescription = "Cafe\u0301 \U0001F600" } },
			member with { Source = member.Source with { ItemCode = "" } },
			member with { ExpectedAssignmentId = Guid.NewGuid() },
			member with { ApplyOperationId = Guid.NewGuid() },
			member with { UndoOperationId = Guid.NewGuid() },
			member with { Target = member.Target with { Path = [member.Target.CategoryId!.Value, Guid.NewGuid()] } },
			member with { Decision = member.Decision with { MappingRevisionId = Guid.NewGuid() } },
			member with { Decision = member.Decision with { ApprovalTaxonomyRevisionId = Guid.NewGuid() } },
			member with { Decision = member.Decision with { SourceFingerprint = new string('B', 64) } }
		];
		foreach (var changed in changes)
		{
			Contract.ManifestRequestHash(request with { Members = [changed, request.Members[1]] })
				.Should().NotBe(Contract.ManifestRequestHash(request));
		}
	}

	[Fact]
	public void ManifestRejectsDuplicateMembersAndAllCrossPhaseOperationCollisions()
	{
		var request = Request();
		var first = request.Members[0];
		var second = request.Members[1];
		Contract.ItemRequest[] invalid =
		[
			second with { Source = second.Source with { ItemId = first.Source.ItemId } },
			second with { ApplyOperationId = first.ApplyOperationId },
			second with { ApplyOperationId = first.UndoOperationId },
			second with { UndoOperationId = first.ApplyOperationId },
			second with { UndoOperationId = first.UndoOperationId }
		];
		foreach (var member in invalid) { Reject(request with { Members = [first, member] }); }
	}

	[Fact]
	public void ManifestRejectsMismatchedReferencesAndInvalidMetadata()
	{
		var request = Request();
		var member = request.Members[0];
		Contract.ItemRequest[] invalid =
		[
			member with { ManifestId = Guid.NewGuid() }, member with { Actor = "other" },
			member with { PolicyVersion = "other" }, member with { CapturedAt = member.CapturedAt.AddTicks(10) },
			member with { Target = member.Target with { TaxonomyRevisionId = Guid.NewGuid() } }, null!
		];
		foreach (var changed in invalid) { Reject(request with { Members = [changed] }); }
		Reject(request with { Version = 2 });
		Reject(request with { ManifestId = Guid.Empty });
		Reject(request with { TaxonomyRevisionId = Guid.Empty });
		Reject(request with { Actor = null! });
		Reject(request with { PolicyVersion = " " });
		Reject(request with { Members = null! });
		Reject(request with { CapturedAt = request.CapturedAt.AddTicks(1) });
		Reject(request with { CapturedAt = request.CapturedAt.ToOffset(TimeSpan.FromHours(1)) });
		Reject(Recontext(request with { Actor = "\uD800" }));
		Reject(request with { Members = [member with { Source = member.Source with { ItemCode = "\0" } }] });
	}

	[Fact]
	public void ManifestReaderRejectsUnknownMissingDuplicateAndNullData()
	{
		string canonical = Contract.WriteManifestRequest(Request());
		foreach (string name in new[] { "Version", "ManifestId", "Actor", "PolicyVersion", "TaxonomyRevisionId", "CapturedAt", "Members" })
		{
			var root = JsonNode.Parse(canonical)!.AsObject();
			root.Remove(name);
			FluentActions.Invoking(() => Contract.ReadManifestRequest(root.ToJsonString())).Should().Throw<JsonException>();
		}
		string[] invalid =
		[
			"null", canonical.Insert(1, "\"Unknown\":true,"), canonical.Insert(1, "\"Version\":1,"),
			canonical.Replace("2026-10-28T12:00:00.123456Z", "2026-10-28T12:00:00.1234567Z"),
			canonical.Replace("\"ItemCode\":null,", ""), canonical.Replace("\"Members\":[", "\"Members\":[null,")
		];
		foreach (string json in invalid)
		{
			FluentActions.Invoking(() => Contract.ReadManifestRequest(json)).Should().Throw<Exception>();
		}
	}

	private static void Reject(Contract.ManifestRequest request) =>
		FluentActions.Invoking(() => Contract.ManifestRequestHash(request)).Should().Throw<InvalidDataException>();

	private static Contract.ManifestRequest Recontext(Contract.ManifestRequest request) => request with
	{
		Members = request.Members.Select(member => member with
		{
			ManifestId = request.ManifestId,
			Actor = request.Actor,
			PolicyVersion = request.PolicyVersion,
			CapturedAt = request.CapturedAt,
			Target = member.Target with { TaxonomyRevisionId = request.TaxonomyRevisionId }
		}).ToArray()
	};

	private static Contract.ManifestRequest Request()
	{
		Guid category = Guid.NewGuid();
		var member = new Contract.ItemRequest(1, Guid.NewGuid(), "synthetic actor", "candidate-v1",
			DateTimeOffset.Parse("2026-10-28T12:00:00.123456Z"),
			new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic store", "Caf\u00E9 \U0001F600", null, Guid.NewGuid(), Guid.NewGuid()),
			null, new(category, Guid.NewGuid(), [category]), Guid.NewGuid(), Guid.NewGuid(),
			new(1, "approved-exact-mapping", Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)));
		return new(1, member.ManifestId, member.Actor, member.PolicyVersion, member.Target.TaxonomyRevisionId,
			member.CapturedAt, [member, member with
			{
				Source = member.Source with { ItemId = Guid.NewGuid() }, ApplyOperationId = Guid.NewGuid(), UndoOperationId = Guid.NewGuid()
			}]);
	}
}
