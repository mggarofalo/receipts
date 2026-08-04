using Application.Interfaces;
using Application.Models.NormalizedDescriptions;

namespace Application.Commands.NormalizedDescription.RequeuePending;

// Deletes every PendingReview description so the background resolver rebuilds it with near-miss
// evidence (RECEIPTS-883). Returns null when ExpectedPendingCount disagrees with the live count,
// which the controller surfaces as 409 — the caller previewed a different world and must re-read
// before destroying anything.
public record RequeuePendingCommand(int ExpectedPendingCount) : ICommand<RequeuePendingResult?>;
