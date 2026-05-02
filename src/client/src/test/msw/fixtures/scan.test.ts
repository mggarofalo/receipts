import { describe, expect, it } from "vitest";
import { scanProposal } from "./scan";
import {
  mapProposalToInitialValues,
  mapProposalToConfidenceMap,
} from "@/pages/scan-receipt/proposalMappers";

/**
 * Regression coverage for RECEIPTS-632.
 *
 * The MSW scan fixture used to drift from the OpenAPI contract because it
 * was a bare object literal with no type annotation — TypeScript silently
 * accepted missing required fields, and integration tests crashed at runtime
 * with `TypeError: Cannot read properties of undefined`.
 *
 * These tests lock down the fixture's shape against the same proposal
 * mappers that production code uses, so any future drift surfaces here
 * (and in the type-check) instead of in a downstream test.
 */
describe("scanProposal fixture", () => {
  it("can be mapped to initial values without throwing", () => {
    expect(() => mapProposalToInitialValues(scanProposal)).not.toThrow();
  });

  it("can be mapped to a confidence map without throwing", () => {
    expect(() => mapProposalToConfidenceMap(scanProposal)).not.toThrow();
  });

  it("provides a non-empty proposedTransactions array (RECEIPTS-658)", () => {
    // The wizard pre-populates Transaction rows from this array; an empty
    // value silently hides the prefilled rows and would mask an upstream
    // resolver regression.
    expect(scanProposal.proposedTransactions).toBeDefined();
    expect(scanProposal.proposedTransactions!.length).toBeGreaterThan(0);
    for (const txn of scanProposal.proposedTransactions!) {
      expect(txn).toHaveProperty("cardId");
      expect(txn).toHaveProperty("cardIdConfidence");
      expect(txn).toHaveProperty("accountId");
      expect(txn).toHaveProperty("accountIdConfidence");
      expect(txn).toHaveProperty("amount");
      expect(txn).toHaveProperty("amountConfidence");
      expect(txn).toHaveProperty("date");
      expect(txn).toHaveProperty("dateConfidence");
    }
  });

  it("provides taxCode and taxCodeConfidence for every item", () => {
    // Per-item taxCode is required by the OpenAPI contract; missing it
    // crashes mapProposalToInitialValues at runtime.
    expect(scanProposal.items.length).toBeGreaterThan(0);
    for (const item of scanProposal.items) {
      expect(item).toHaveProperty("taxCode");
      expect(item).toHaveProperty("taxCodeConfidence");
    }
  });

  it("populates every required top-level metadata field", () => {
    // These fields were added during VLM rollout and are required by the
    // OpenAPI contract; the fixture must keep them populated.
    expect(scanProposal).toHaveProperty("storeAddress");
    expect(scanProposal).toHaveProperty("storeAddressConfidence");
    expect(scanProposal).toHaveProperty("storePhone");
    expect(scanProposal).toHaveProperty("storePhoneConfidence");
    expect(scanProposal).toHaveProperty("receiptId");
    expect(scanProposal).toHaveProperty("receiptIdConfidence");
    expect(scanProposal).toHaveProperty("storeNumber");
    expect(scanProposal).toHaveProperty("storeNumberConfidence");
    expect(scanProposal).toHaveProperty("terminalId");
    expect(scanProposal).toHaveProperty("terminalIdConfidence");
    expect(scanProposal).toHaveProperty("droppedPageCount");
  });

  it("exercises every confidence level (high, medium, low) across its fields", () => {
    // The fixture exists to drive UI badge rendering; require all three
    // confidence levels to appear so the wizard's high/medium/low badge
    // paths stay covered. A weaker `size > 1` check would silently let
    // a future fixture edit drop one level entirely.
    const allConfidences = new Set<string>();
    allConfidences.add(scanProposal.storeNameConfidence);
    allConfidences.add(scanProposal.storeAddressConfidence);
    allConfidences.add(scanProposal.storePhoneConfidence);
    allConfidences.add(scanProposal.dateConfidence);
    allConfidences.add(scanProposal.terminalIdConfidence);
    allConfidences.add(scanProposal.storeNumberConfidence);
    expect(allConfidences).toContain("high");
    expect(allConfidences).toContain("medium");
    expect(allConfidences).toContain("low");
  });
});
