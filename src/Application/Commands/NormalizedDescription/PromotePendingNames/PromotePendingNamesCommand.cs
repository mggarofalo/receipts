using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.PromotePendingNames;

// Promotes the previewed set of consistent Pending review names (RECEIPTS-1107). A null result means
// the eligible set changed since the preview; the controller answers 409 and the caller re-reads.
public record PromotePendingNamesCommand(string Actor, string ExpectedFingerprint) : ICommand<PendingNamePromotionResult?>;
