import type { AiRun } from "@/hooks/useNormalizationAi";

export function visibleAiProposals(run: AiRun) {
  if (!["ready", "applied"].includes(run.state)) {
    return run.proposals.filter((proposal) =>
      ["applied", "overridden", "no_change"].includes(proposal.state),
    );
  }
  return run.proposals.filter(
    (proposal) =>
      proposal.action === "abstain" ||
      (proposal.action === "reuse" && !!proposal.targetId) ||
      (proposal.action === "create" && !!proposal.canonicalName) ||
      (proposal.labelOnly && !!proposal.suggestedDisplayLabel) ||
      ["applied", "overridden", "no_change"].includes(proposal.state),
  );
}
