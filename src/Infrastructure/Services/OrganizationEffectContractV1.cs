using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Services;

// Inactive candidate claims only. Parsing and hashing never establish mapping approval,
// source eligibility, assignment authority, or permission to execute either phase.
internal static class OrganizationEffectContractV1
{
	internal sealed record MappingEvidence(int Version, string Kind, Guid MappingRevisionId,
		Guid ApprovalTaxonomyRevisionId, string SourceFingerprint);
	internal sealed record Source(Guid ReceiptId, Guid ItemId, string Store, string RawDescription,
		string? ItemCode, Guid ReceiptRevision, Guid ItemRevision);
	// Paths run from selected category to root, retaining stable identities in order.
	internal sealed record CategoryIntent(Guid? CategoryId, Guid TaxonomyRevisionId, Guid[] Path);
	internal sealed record ItemRequest(int Version, Guid ManifestId, string Actor, string PolicyVersion,
		DateTimeOffset CapturedAt, Source Source, Guid? ExpectedAssignmentId, CategoryIntent Target,
		Guid ApplyOperationId, Guid UndoOperationId, MappingEvidence Decision);
	// Members is the complete caller-declared ordered vector, not a set or timestamp order.
	// A digest binds the declaration only; a future transactional witness must attest completeness.
	internal sealed record ManifestRequest(int Version, Guid ManifestId, string Actor, string PolicyVersion,
		Guid TaxonomyRevisionId, DateTimeOffset CapturedAt, ItemRequest[] Members);
	internal sealed record PhaseCommand(int Version, string Phase, string ItemRequestHash,
		Guid OperationId, Guid? ExpectedAssignmentId, CategoryIntent Target, DateTimeOffset RecordedAt);

	private static readonly JsonSerializerOptions Options = new()
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectRequiredConstructorParameters = true,
		MaxDepth = 16,
		Converters = { new MicrosecondTimestampConverter() }
	};

	internal static MappingEvidence ReadDecision(string json)
	{
		var decision = Read<MappingEvidence>(json);
		Validate(decision);
		return decision;
	}

	internal static ItemRequest ReadRequest(string json)
	{
		var request = Read<ItemRequest>(json);
		Validate(request);
		return request;
	}

	internal static ManifestRequest ReadManifestRequest(string json)
	{
		var request = Read<ManifestRequest>(json);
		Validate(request);
		return request;
	}

	internal static string WriteManifestRequest(ManifestRequest request)
	{
		Validate(request);
		return Canonical(request);
	}

	internal static string ManifestRequestHash(ManifestRequest request) =>
		Digest("receipts.organization-effect.manifest-request.v1", WriteManifestRequest(request));

	internal static PhaseCommand ReadPhase(string json)
	{
		var command = Read<PhaseCommand>(json);
		Validate(command);
		return command;
	}

	internal static string WriteRequest(ItemRequest request)
	{
		Validate(request);
		return Canonical(request);
	}

	internal static string RequestHash(ItemRequest request) =>
		Digest("receipts.organization-effect.item-request.v1", WriteRequest(request));

	internal static string PhaseHash(PhaseCommand command)
		=> Digest($"receipts.organization-effect.{command.Phase}.v1", WritePhase(command));

	internal static string WritePhase(PhaseCommand command)
	{
		Validate(command);
		return Canonical(command);
	}

	private static T Read<T>(string json) where T : class =>
		JsonSerializer.Deserialize<T>(PortableTaxonomyArchive.CanonicalEffectDecision(json), Options)
		?? throw new InvalidDataException("Null effect contract.");

	private static string Canonical<T>(T value) => PortableTaxonomyArchive.CanonicalEffectDecision(
		JsonSerializer.Serialize(value, Options));

	private static string Digest(string domain, string canonical) => Convert.ToHexString(
		SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\0" + canonical)));

	private static void Validate(MappingEvidence decision)
	{
		Check(decision is not null && decision.Version == 1 && decision.Kind == "approved-exact-mapping",
			"Unknown effect decision contract.");
		Id(decision!.MappingRevisionId); Id(decision.ApprovalTaxonomyRevisionId);
		Hash(decision.SourceFingerprint);
	}

	private static void Validate(ItemRequest request)
	{
		Check(request is not null && request.Version == 1 && request.Source is not null,
			"Invalid effect request contract.");
		Id(request!.ManifestId); Text(request.Actor); Text(request.PolicyVersion); Timestamp(request.CapturedAt);
		var source = request.Source!;
		Id(source.ReceiptId); Id(source.ItemId); Id(source.ReceiptRevision); Id(source.ItemRevision);
		Raw(source.Store); Raw(source.RawDescription);
		if (source.ItemCode is not null) { Raw(source.ItemCode); }
		OptionalId(request.ExpectedAssignmentId);
		Validate(request.Target);
		Check(request.Target.CategoryId is not null, "Apply requires a target category.");
		Id(request.ApplyOperationId); Id(request.UndoOperationId);
		Check(request.ApplyOperationId != request.UndoOperationId, "Effect phase operations must differ.");
		Validate(request.Decision);
	}

	private static void Validate(ManifestRequest request)
	{
		Check(request is not null && request.Version == 1 && request.Members is not null,
			"Invalid effect manifest request contract.");
		Id(request!.ManifestId); Text(request.Actor); Text(request.PolicyVersion);
		Id(request.TaxonomyRevisionId); Timestamp(request.CapturedAt);
		HashSet<Guid> members = [];
		HashSet<Guid> operations = [];
		foreach (var member in request.Members)
		{
			Validate(member);
			Check(member.ManifestId == request.ManifestId && member.Actor == request.Actor
				&& member.PolicyVersion == request.PolicyVersion
				&& member.Target.TaxonomyRevisionId == request.TaxonomyRevisionId
				&& member.CapturedAt == request.CapturedAt,
				"Effect member references differ from manifest context.");
			Check(members.Add(member.Source.ItemId), "Duplicate effect manifest member.");
			Check(operations.Add(member.ApplyOperationId) && operations.Add(member.UndoOperationId),
				"Duplicate effect manifest operation.");
		}
	}

	private static void Validate(PhaseCommand command)
	{
		Check(command is not null && command.Version == 1 && command.Phase is "apply" or "undo",
			"Invalid effect phase contract.");
		Hash(command!.ItemRequestHash); Id(command.OperationId); OptionalId(command.ExpectedAssignmentId);
		Validate(command.Target); Timestamp(command.RecordedAt);
		Check(command.Phase != "apply" || command.Target.CategoryId is not null,
			"Apply requires a target category.");
		Check(command.Phase != "undo" || command.ExpectedAssignmentId is not null,
			"Undo requires the applied assignment identity.");
	}

	private static void Validate(CategoryIntent target)
	{
		Check(target is not null && target.Path is not null, "Missing effect category intent.");
		Id(target!.TaxonomyRevisionId); OptionalId(target.CategoryId);
		Check(target.CategoryId is Guid category
			? target.Path.Length > 0 && target.Path[0] == category
			: target.Path.Length == 0, "Effect category and path differ.");
		foreach (var id in target.Path) { Id(id); }
		Check(target.Path.Distinct().Count() == target.Path.Length, "Repeated category in effect path.");
	}

	private static void Id(Guid id) => Check(id != Guid.Empty, "Empty effect identity.");
	private static void OptionalId(Guid? id) { if (id.HasValue) { Id(id.Value); } }
	private static void Raw(string text)
	{
		Check(text is not null && !text.Contains('\0'), "Invalid effect source text.");
		for (int index = 0; index < text.Length; index++)
		{
			if (!char.IsSurrogate(text[index])) { continue; }
			Check(char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]),
				"Unpaired surrogate in effect text.");
			index++;
		}
	}
	private static void Text(string text) { Raw(text); Check(!string.IsNullOrWhiteSpace(text), "Empty effect metadata."); }
	private static void Hash(string hash) => Check(hash is not null && hash.Length == 64
		&& hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'), "Expected uppercase SHA-256 value.");
	private static void Timestamp(DateTimeOffset value) => Check(value.Offset == TimeSpan.Zero
		&& value.Ticks % 10 == 0, "Effect timestamp must be UTC with exact microsecond precision.");
	private static void Check([DoesNotReturnIf(false)] bool condition, string message)
	{
		if (!condition) { throw new InvalidDataException(message); }
	}

	private sealed class MicrosecondTimestampConverter : JsonConverter<DateTimeOffset>
	{
		public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
		{
			string? text = reader.GetString();
			Check(DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
				out var value), "Effect timestamp requires six fractional digits and UTC Z.");
			Timestamp(value);
			return value;
		}
		public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
		{
			Timestamp(value);
			writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
		}
	}
}
