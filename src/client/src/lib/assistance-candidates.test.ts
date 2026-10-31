import { describe, it, expect } from "vitest";
import { assistanceSuggestions } from "./assistance-candidates";

describe("assistanceSuggestions", () => {
  it("lists item-type candidates before group candidates, keeping each stage's rank", () => {
    expect(
      assistanceSuggestions({
        rootCandidates: [
          { categoryId: "food", probability: 0.7 },
          { categoryId: "household", probability: 0.2 },
        ],
        leafCandidates: [
          { categoryId: "produce", probability: 0.6 },
          { categoryId: "dairy", probability: 0.3 },
        ],
      }),
    ).toEqual([
      { categoryId: "produce", confidence: 0.6, reason: "Jev" },
      { categoryId: "dairy", confidence: 0.3, reason: "Jev" },
      { categoryId: "food", confidence: 0.7, reason: "Jev" },
      { categoryId: "household", confidence: 0.2, reason: "Jev" },
    ]);
  });

  it("offers only group candidates when stage 2 did not run", () => {
    expect(
      assistanceSuggestions({
        rootCandidates: [{ categoryId: "food", probability: 0.9 }],
        leafCandidates: [],
      }),
    ).toEqual([{ categoryId: "food", confidence: 0.9, reason: "Jev" }]);
  });

  it("offers nothing for results without candidates", () => {
    expect(
      assistanceSuggestions({ rootCandidates: [], leafCandidates: [] }),
    ).toEqual([]);
  });
});
