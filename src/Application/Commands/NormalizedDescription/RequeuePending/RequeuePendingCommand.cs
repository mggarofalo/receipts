using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.RequeuePending;

// Deletes every PendingReview description so the background resolver rebuilds it with near-miss
// evidence (RECEIPTS-883). ExpectedFingerprint is the digest the caller was shown by the preview;
// a null result means the live pending set no longer matches it, which the controller surfaces as
// 409 — the caller previewed a different world and must re-read before destroying anything.
public record RequeuePendingCommand(string ExpectedFingerprint) : ICommand<RequeuePendingResult?>;
