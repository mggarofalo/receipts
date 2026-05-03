import { describe, expect, it } from "vitest";
import {
  initialItemsAndConfidence,
  initialTransactionsAndConfidence,
  mapProposalToInitialValues,
  mapProposalToConfidenceMap,
} from "./proposalMappers";
import type { ScanInitialValues, ReceiptConfidenceMap } from "./types";
import type { components } from "@/generated/api";

type ProposedReceiptResponse = components["schemas"]["ProposedReceiptResponse"];

function makeProposal(
  overrides: Partial<ProposedReceiptResponse> = {},
): ProposedReceiptResponse {
  return {
    storeName: "Test Store",
    storeNameConfidence: "high",
    storeAddress: null,
    storeAddressConfidence: "high",
    storePhone: null,
    storePhoneConfidence: "high",
    date: "2024-06-15",
    dateConfidence: "high",
    items: [],
    subtotal: 0,
    subtotalConfidence: "high",
    taxLines: [],
    total: 0,
    totalConfidence: "high",
    paymentMethod: null,
    paymentMethodConfidence: "high",
    payments: [],
    proposedTransactions: [],
    receiptId: null,
    receiptIdConfidence: "high",
    storeNumber: null,
    storeNumberConfidence: "high",
    terminalId: null,
    terminalIdConfidence: "high",
    droppedPageCount: 0,
    ...overrides,
  };
}

describe("mapProposalToInitialValues", () => {
  it("populates header from proposal including new address/phone fields", () => {
    const proposal = makeProposal({
      storeName: "Walmart",
      storeAddress: "123 Main St, Springfield",
      storePhone: "(555) 123-4567",
      date: "2024-06-15",
      taxLines: [
        {
          label: "Tax",
          labelConfidence: "high",
          amount: 1.25,
          amountConfidence: "high",
        },
      ],
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.header).toEqual({
      location: "Walmart",
      date: "2024-06-15",
      taxAmount: 1.25,
      storeAddress: "123 Main St, Springfield",
      storePhone: "(555) 123-4567",
    });
  });

  it("populates metadata with receiptId, storeNumber, and terminalId", () => {
    const proposal = makeProposal({
      receiptId: "TX-987654",
      storeNumber: "0042",
      terminalId: "T01",
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.metadata).toEqual({
      receiptId: "TX-987654",
      storeNumber: "0042",
      terminalId: "T01",
    });
  });

  it("populates proposedTransactions array preserving order (RECEIPTS-658)", () => {
    const proposal = makeProposal({
      proposedTransactions: [
        {
          cardId: "card-a",
          cardIdConfidence: "high",
          accountId: "acct-a",
          accountIdConfidence: "high",
          amount: 54.32,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "MASTERCARD",
        },
        {
          cardId: "card-b",
          cardIdConfidence: "high",
          accountId: "acct-b",
          accountIdConfidence: "high",
          amount: 5.0,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "VISA",
        },
      ],
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.proposedTransactions).toEqual([
      {
        cardId: "card-a",
        accountId: "acct-a",
        amount: 54.32,
        date: "2024-06-15",
      },
      {
        cardId: "card-b",
        accountId: "acct-b",
        amount: 5.0,
        date: "2024-06-15",
      },
    ]);
  });

  it("falls back to proposal date when transaction date is missing", () => {
    const proposal = makeProposal({
      date: "2024-06-15",
      proposedTransactions: [
        {
          cardId: "card-a",
          cardIdConfidence: "high",
          accountId: "acct-a",
          accountIdConfidence: "high",
          amount: 10,
          amountConfidence: "high",
          date: null,
          dateConfidence: "none",
          methodSnapshot: null,
        },
      ],
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.proposedTransactions[0].date).toBe("2024-06-15");
  });

  it("coerces null cardId/accountId to empty strings (unmatched payment)", () => {
    const proposal = makeProposal({
      proposedTransactions: [
        {
          cardId: null,
          cardIdConfidence: "none",
          accountId: null,
          accountIdConfidence: "none",
          amount: 10,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "Cash",
        },
      ],
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.proposedTransactions[0].cardId).toBe("");
    expect(result.proposedTransactions[0].accountId).toBe("");
  });

  it("populates per-item taxCode", () => {
    const proposal = makeProposal({
      items: [
        {
          code: "MILK-GAL",
          codeConfidence: "high",
          description: "Whole milk",
          descriptionConfidence: "high",
          quantity: 1,
          quantityConfidence: "high",
          unitPrice: 3.99,
          unitPriceConfidence: "high",
          totalPrice: 3.99,
          totalPriceConfidence: "high",
          taxCode: "F",
          taxCodeConfidence: "high",
        },
      ],
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.items[0].taxCode).toBe("F");
  });

  it("defaults nullable fields to empty strings or zero", () => {
    const proposal = makeProposal({
      storeName: null,
      storeAddress: null,
      storePhone: null,
      date: null,
      receiptId: null,
      storeNumber: null,
      terminalId: null,
    });

    const result = mapProposalToInitialValues(proposal);

    expect(result.header.location).toBe("");
    expect(result.header.date).toBe("");
    expect(result.header.storeAddress).toBe("");
    expect(result.header.storePhone).toBe("");
    expect(result.metadata).toEqual({
      receiptId: "",
      storeNumber: "",
      terminalId: "",
    });
    expect(result.proposedTransactions).toEqual([]);
    expect(result.items).toEqual([]);
  });

  it("treats omitted proposedTransactions as an empty array", () => {
    // Defensive guard: the OpenAPI contract declares proposedTransactions as
    // optional; older fixtures or partially-stubbed handlers may omit it.
    const proposal = makeProposal();
    delete (proposal as { proposedTransactions?: unknown })
      .proposedTransactions;

    const result = mapProposalToInitialValues(proposal);

    expect(result.proposedTransactions).toEqual([]);
  });

  it("strips ISO time component from a datetime date", () => {
    const proposal = makeProposal({ date: "2024-06-15T12:34:56Z" });

    const result = mapProposalToInitialValues(proposal);

    expect(result.header.date).toBe("2024-06-15");
  });

  // RECEIPTS-655: Walmart-style receipts only print a per-line total — quantity
  // and unit-price arrive as 0/None confidence. The mapper must enter "flat"
  // pricing mode and use totalPrice as the source of truth, not silently produce
  // a $0.00 row by computing 1 × 0.
  describe("flat-vs-quantity pricing decision (RECEIPTS-655)", () => {
    it("enters flat mode when only totalPrice is reliable (Walmart shape)", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "WMT-001",
            codeConfidence: "high",
            description: "GV WHL MLK",
            descriptionConfidence: "high",
            quantity: 0,
            quantityConfidence: "none",
            unitPrice: 0,
            unitPriceConfidence: "none",
            totalPrice: 4.97,
            totalPriceConfidence: "high",
            taxCode: "F",
            taxCodeConfidence: "high",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      expect(result.items).toHaveLength(1);
      const item = result.items[0];
      expect(item.pricingMode).toBe("flat");
      expect(item.totalPrice).toBe(4.97);
      // Domain rule: flat-priced items keep quantity == 1 and unitPrice == 0.
      expect(item.quantity).toBe(1);
      expect(item.unitPrice).toBe(0);
      // The reference Walmart bug surfaced as $0.00 placeholders — assert we
      // never produce a row whose effective line total is zero when the VLM
      // returned a positive total.
      expect(item.totalPrice).toBeGreaterThan(0);
    });

    it("uses quantity mode for traditional q × p receipts", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "MILK-GAL",
            codeConfidence: "high",
            description: "Whole milk",
            descriptionConfidence: "high",
            quantity: 2,
            quantityConfidence: "high",
            unitPrice: 3.99,
            unitPriceConfidence: "high",
            totalPrice: 7.98,
            totalPriceConfidence: "high",
            taxCode: "F",
            taxCodeConfidence: "high",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      const item = result.items[0];
      expect(item.pricingMode).toBe("quantity");
      expect(item.quantity).toBe(2);
      expect(item.unitPrice).toBe(3.99);
      expect(item.totalPrice).toBe(7.98);
    });

    it("falls back to computed total when VLM omits totalPrice for a quantity row", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "X",
            codeConfidence: "high",
            description: "Bananas",
            descriptionConfidence: "high",
            quantity: 3,
            quantityConfidence: "high",
            unitPrice: 0.5,
            unitPriceConfidence: "high",
            totalPrice: null,
            totalPriceConfidence: "none",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      const item = result.items[0];
      expect(item.pricingMode).toBe("quantity");
      expect(item.totalPrice).toBeCloseTo(1.5, 2);
    });

    it("handles a mixed-shape receipt (one flat, one quantity)", () => {
      const proposal = makeProposal({
        items: [
          {
            code: null,
            codeConfidence: "none",
            description: "Bread",
            descriptionConfidence: "high",
            quantity: 0,
            quantityConfidence: "none",
            unitPrice: 0,
            unitPriceConfidence: "none",
            totalPrice: 3.49,
            totalPriceConfidence: "high",
            taxCode: null,
            taxCodeConfidence: "none",
          },
          {
            code: "MILK",
            codeConfidence: "high",
            description: "Milk",
            descriptionConfidence: "high",
            quantity: 2,
            quantityConfidence: "high",
            unitPrice: 2.0,
            unitPriceConfidence: "high",
            totalPrice: 4.0,
            totalPriceConfidence: "high",
            taxCode: "F",
            taxCodeConfidence: "high",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      expect(result.items[0].pricingMode).toBe("flat");
      expect(result.items[0].totalPrice).toBe(3.49);
      expect(result.items[1].pricingMode).toBe("quantity");
      expect(result.items[1].totalPrice).toBe(4.0);
    });

    it("does not regress to $0.00: every item has a positive line value when the VLM gave one", () => {
      const proposal = makeProposal({
        items: Array.from({ length: 5 }, (_, idx) => ({
          code: `WMT-${idx}`,
          codeConfidence: "high" as const,
          description: `Item ${idx}`,
          descriptionConfidence: "high" as const,
          quantity: 0,
          quantityConfidence: "none" as const,
          unitPrice: 0,
          unitPriceConfidence: "none" as const,
          totalPrice: 1.99 + idx,
          totalPriceConfidence: "high" as const,
          taxCode: null,
          taxCodeConfidence: "none" as const,
        })),
      });

      const result = mapProposalToInitialValues(proposal);

      // Every row should be non-zero — the bug was producing all-$0 rows.
      for (const item of result.items) {
        expect(item.pricingMode).toBe("flat");
        expect(item.totalPrice).toBeGreaterThan(0);
      }
    });
  });

  // RECEIPTS-661: Weight-priced items (Walmart "TOMATO 2.300 lb @ 0.92") arrive
  // from the VLM with quantity + unitPrice populated and a recognised
  // confidence, but no separate totalPrice — the API serialises this as
  // `totalPrice: 0, totalPriceConfidence: "none"` because the C# value-type
  // defaults to 0. The previous fallback `totalPrice ?? quantity * unitPrice`
  // short-circuited to 0 because nullish-coalescing only fires for null /
  // undefined, leaving the wizard with $0.00 rows. The server now derives the
  // missing total upstream; this defensive check still covers any path that
  // emits a 0 + none pair.
  describe("zero-totalPrice + none-confidence fallback (RECEIPTS-661)", () => {
    it("computes totalPrice from quantity * unitPrice when API sends 0 + none", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "TOMATO",
            codeConfidence: "high",
            description: "TOMATO",
            descriptionConfidence: "high",
            quantity: 2.3,
            quantityConfidence: "high",
            unitPrice: 0.92,
            unitPriceConfidence: "high",
            totalPrice: 0,
            totalPriceConfidence: "none",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      const tomato = result.items[0];
      expect(tomato.pricingMode).toBe("quantity");
      // 2.3 * 0.92 = 2.116; the wizard rounds to cents = 2.12
      expect(tomato.totalPrice).toBeCloseTo(2.12, 2);
      expect(tomato.totalPrice).toBeGreaterThan(0);
    });

    it("does not overwrite a positive totalPrice when confidence is high", () => {
      // Pass-through: the server already supplied a totalPrice; respect it
      // even when q * p computes to a slightly different value.
      const proposal = makeProposal({
        items: [
          {
            code: "MILK",
            codeConfidence: "high",
            description: "Milk",
            descriptionConfidence: "high",
            quantity: 2,
            quantityConfidence: "high",
            unitPrice: 3,
            unitPriceConfidence: "high",
            totalPrice: 5.99, // VLM said 5.99 even though 2 * 3 = 6
            totalPriceConfidence: "high",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      expect(result.items[0].totalPrice).toBe(5.99);
    });

    it("uses totalPrice when confidence is medium and value is positive", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "X",
            codeConfidence: "high",
            description: "x",
            descriptionConfidence: "high",
            quantity: 1,
            quantityConfidence: "high",
            unitPrice: 2,
            unitPriceConfidence: "high",
            totalPrice: 1.99,
            totalPriceConfidence: "medium",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      expect(result.items[0].totalPrice).toBe(1.99);
    });

    it("uses totalPrice when confidence is low and value is positive", () => {
      const proposal = makeProposal({
        items: [
          {
            code: "X",
            codeConfidence: "high",
            description: "x",
            descriptionConfidence: "high",
            quantity: 1,
            quantityConfidence: "high",
            unitPrice: 2,
            unitPriceConfidence: "high",
            totalPrice: 1.49,
            totalPriceConfidence: "low",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      expect(result.items[0].totalPrice).toBe(1.49);
    });

    it("tolerates capitalised confidence values until RECEIPTS-660 lands", () => {
      // The model occasionally emits "None" / "High" before serialisation
      // normalisation. Defensive `.toLowerCase()` keeps the fallback working
      // until RECEIPTS-660 forces a single lowercase wire format.
      const proposal = makeProposal({
        items: [
          {
            code: "BANANAS",
            codeConfidence: "high",
            description: "BANANAS",
            descriptionConfidence: "high",
            quantity: 2.46,
            quantityConfidence: "high",
            unitPrice: 0.5,
            unitPriceConfidence: "high",
            totalPrice: 0,
            // Cast away the lowercase enum constraint so the test can express
            // the (incorrect-but-real) capitalised wire shape.
            totalPriceConfidence: "None" as unknown as "none",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      const bananas = result.items[0];
      expect(bananas.totalPrice).toBeCloseTo(1.23, 2); // 2.46 * 0.5
      expect(bananas.totalPrice).toBeGreaterThan(0);
    });

    it("falls back to computed total when VLM emits (0, 'high') for a quantity row", () => {
      // Defence against a hallucinated zero: ReceiptItem's domain validator
      // rejects `totalAmount = 0` for a quantity-mode row whose unitPrice > 0
      // (|0 − Math.Floor(q×up×100)/100| > 0.01). Passing the bogus zero through
      // would surface as a server-side ArgumentException at submit time — falling
      // back to `q × up` keeps the wizard usable without forcing the user to
      // manually re-enter every line total.
      const proposal = makeProposal({
        items: [
          {
            code: "X",
            codeConfidence: "high",
            description: "x",
            descriptionConfidence: "high",
            quantity: 2,
            quantityConfidence: "high",
            unitPrice: 5.99,
            unitPriceConfidence: "high",
            totalPrice: 0,
            totalPriceConfidence: "high",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      // 2 × 5.99 = 11.98 (cent-rounded fallback), not 0.
      expect(result.items[0].totalPrice).toBeCloseTo(11.98, 2);
    });

    it("rolling subtotal across mixed weight-priced rows matches sum of q × p", () => {
      // The real-world Walmart bug: TOMATO and BANANAS rows both arrive with
      // (totalPrice=0, none) — the rolling subtotal must equal 2.116 + 1.23 = 3.346
      // (rounded to cents = $3.35), not $0.00.
      const proposal = makeProposal({
        items: [
          {
            code: "TOMATO",
            codeConfidence: "high",
            description: "TOMATO",
            descriptionConfidence: "high",
            quantity: 2.3,
            quantityConfidence: "high",
            unitPrice: 0.92,
            unitPriceConfidence: "high",
            totalPrice: 0,
            totalPriceConfidence: "none",
            taxCode: null,
            taxCodeConfidence: "none",
          },
          {
            code: "BANANAS",
            codeConfidence: "high",
            description: "BANANAS",
            descriptionConfidence: "high",
            quantity: 2.46,
            quantityConfidence: "high",
            unitPrice: 0.5,
            unitPriceConfidence: "high",
            totalPrice: 0,
            totalPriceConfidence: "none",
            taxCode: null,
            taxCodeConfidence: "none",
          },
        ],
      });

      const result = mapProposalToInitialValues(proposal);

      const subtotal = result.items.reduce(
        (sum, item) => sum + item.totalPrice,
        0,
      );
      expect(subtotal).toBeGreaterThan(0);
      expect(subtotal).toBeCloseTo(3.35, 2);
    });
  });
});

describe("mapProposalToConfidenceMap", () => {
  it("omits high-confidence fields", () => {
    const proposal = makeProposal();

    const result = mapProposalToConfidenceMap(proposal);

    expect(result).toEqual({});
  });

  it("omits 'none' confidence fields (RECEIPTS-631: absent fields need no badge)", () => {
    // 'none' means the source receipt did not contain that field at all — the user
    // has nothing to review, so no badge should appear in the confidence map.
    const proposal = makeProposal({
      storeAddress: null,
      storeAddressConfidence: "none",
      storePhone: null,
      storePhoneConfidence: "none",
      receiptId: null,
      receiptIdConfidence: "none",
      storeNumber: null,
      storeNumberConfidence: "none",
      terminalId: null,
      terminalIdConfidence: "none",
    });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result).toEqual({});
  });

  it("falls back to subtotalConfidence when first tax line's amount is 'none'", () => {
    // Regression for the bug-finder review of RECEIPTS-631: the `??` operator
    // does NOT fall through for the non-nullish string "none". Without an
    // explicit check, a tax line whose amount is absent would silently drop
    // the subtotal-based review badge — the user would never be prompted to
    // verify a low-confidence subtotal.
    const proposal = makeProposal({
      taxLines: [
        {
          label: "Tax",
          labelConfidence: "high",
          amount: null,
          amountConfidence: "none",
        },
      ],
      subtotalConfidence: "low",
    });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result.taxAmount).toBe("low");
  });

  it("uses first tax line's amountConfidence when present and not 'none'", () => {
    const proposal = makeProposal({
      taxLines: [
        {
          label: "Tax",
          labelConfidence: "high",
          amount: 1.25,
          amountConfidence: "low",
        },
      ],
      subtotalConfidence: "high",
    });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result.taxAmount).toBe("low");
  });

  it("flags low/medium confidence on the new fields", () => {
    const proposal = makeProposal({
      storeAddress: "123 Main St",
      storeAddressConfidence: "low",
      storePhone: "(555) 123-4567",
      storePhoneConfidence: "medium",
      receiptId: "TX-1",
      receiptIdConfidence: "low",
      storeNumber: "0042",
      storeNumberConfidence: "medium",
      terminalId: "T01",
      terminalIdConfidence: "low",
    });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result).toMatchObject({
      storeAddress: "low",
      storePhone: "medium",
      receiptId: "low",
      storeNumber: "medium",
      terminalId: "low",
    });
  });

  it("emits a transactions array entry for each pre-resolved transaction, dropping high-confidence fields", () => {
    const proposal = makeProposal({
      proposedTransactions: [
        {
          cardId: "card-a",
          cardIdConfidence: "low",
          accountId: "acct-a",
          accountIdConfidence: "high",
          amount: 54.32,
          amountConfidence: "low",
          date: "2024-06-15",
          dateConfidence: "medium",
          methodSnapshot: "MASTERCARD",
        },
        {
          cardId: "card-b",
          cardIdConfidence: "high",
          accountId: "acct-b",
          accountIdConfidence: "high",
          amount: 5,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "Cash",
        },
      ],
    });

    const result = mapProposalToConfidenceMap(proposal);

    // The empty object `{}` for the all-high-confidence transaction is
    // intentional: the array is positional (entry N corresponds to row N),
    // so we cannot omit "high"-only entries without misaligning indices.
    expect(result.transactions).toEqual([
      { cardId: "low", amount: "low", date: "medium" },
      {},
    ]);
  });

  it("flags low cardId confidence (ambiguous lookup) but never 'none'", () => {
    // A no-match yields `cardIdConfidence: "none"`; the user has no card to
    // confirm so no badge is needed. An ambiguous lookup yields "low" so the
    // user is prompted to pick the right card.
    const proposal = makeProposal({
      proposedTransactions: [
        {
          cardId: null,
          cardIdConfidence: "none",
          accountId: null,
          accountIdConfidence: "none",
          amount: 5,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "Cash",
        },
        {
          cardId: null,
          cardIdConfidence: "low",
          accountId: null,
          accountIdConfidence: "low",
          amount: 5,
          amountConfidence: "high",
          date: "2024-06-15",
          dateConfidence: "high",
          methodSnapshot: "VISA",
        },
      ],
    });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result.transactions).toEqual([{}, { cardId: "low" }]);
  });

  it("does not include transactions key when proposedTransactions is empty", () => {
    const proposal = makeProposal({ proposedTransactions: [] });

    const result = mapProposalToConfidenceMap(proposal);

    expect(result.transactions).toBeUndefined();
  });

  it("includes items entries only when at least one taxCode confidence is non-high", () => {
    const lowItem = {
      code: "X",
      codeConfidence: "high" as const,
      description: "x",
      descriptionConfidence: "high" as const,
      quantity: 1,
      quantityConfidence: "high" as const,
      unitPrice: 1,
      unitPriceConfidence: "high" as const,
      totalPrice: 1,
      totalPriceConfidence: "high" as const,
      taxCode: "F",
      taxCodeConfidence: "low" as const,
    };
    const highItem = { ...lowItem, taxCodeConfidence: "high" as const };

    const allHigh = mapProposalToConfidenceMap(
      makeProposal({ items: [highItem] }),
    );
    expect(allHigh.items).toBeUndefined();

    const someLow = mapProposalToConfidenceMap(
      makeProposal({ items: [highItem, lowItem] }),
    );
    expect(someLow.items).toEqual([{}, { taxCode: "low" }]);
  });
});

describe("initialItemsAndConfidence", () => {
  function makeInitial(items: ScanInitialValues["items"]): ScanInitialValues {
    return {
      header: {
        location: "",
        date: "",
        taxAmount: 0,
        storeAddress: "",
        storePhone: "",
      },
      metadata: { receiptId: "", storeNumber: "", terminalId: "" },
      proposedTransactions: [],
      items,
    };
  }

  it("returns empty items + empty Map when initialValues is undefined", () => {
    const result = initialItemsAndConfidence(undefined, undefined);
    expect(result.items).toEqual([]);
    expect(result.itemConfidenceById.size).toBe(0);
  });

  it("returns empty items + empty Map when initialValues has no items", () => {
    const result = initialItemsAndConfidence(makeInitial([]), undefined);
    expect(result.items).toEqual([]);
    expect(result.itemConfidenceById.size).toBe(0);
  });

  it("assigns a unique generated id to each item", () => {
    const result = initialItemsAndConfidence(
      makeInitial([
        {
          receiptItemCode: "MILK",
          description: "Milk",
          pricingMode: "quantity",
          quantity: 1,
          unitPrice: 3.5,
          totalPrice: 3.5,
          category: "",
          subcategory: "",
          taxCode: "",
        },
        {
          receiptItemCode: "BREAD",
          description: "Bread",
          pricingMode: "quantity",
          quantity: 2,
          unitPrice: 2.5,
          totalPrice: 5,
          category: "",
          subcategory: "",
          taxCode: "",
        },
      ]),
      undefined,
    );

    expect(result.items).toHaveLength(2);
    expect(result.items[0].id).toBeTruthy();
    expect(result.items[1].id).toBeTruthy();
    expect(result.items[0].id).not.toBe(result.items[1].id);
  });

  it("pairs each item id with its corresponding confidence entry", () => {
    const result = initialItemsAndConfidence(
      makeInitial([
        {
          receiptItemCode: "MILK",
          description: "Milk",
          pricingMode: "quantity",
          quantity: 1,
          unitPrice: 3.5,
          totalPrice: 3.5,
          category: "",
          subcategory: "",
          taxCode: "F",
        },
        {
          receiptItemCode: "BREAD",
          description: "Bread",
          pricingMode: "quantity",
          quantity: 1,
          unitPrice: 2,
          totalPrice: 2,
          category: "",
          subcategory: "",
          taxCode: "",
        },
      ]),
      { items: [{ taxCode: "low" }, { taxCode: "medium" }] },
    );

    expect(result.itemConfidenceById.size).toBe(2);
    expect(result.itemConfidenceById.get(result.items[0].id)).toEqual({
      taxCode: "low",
    });
    expect(result.itemConfidenceById.get(result.items[1].id)).toEqual({
      taxCode: "medium",
    });
  });

  it("omits Map entries for items lacking a confidence record", () => {
    const result = initialItemsAndConfidence(
      makeInitial([
        {
          receiptItemCode: "MILK",
          description: "Milk",
          pricingMode: "quantity",
          quantity: 1,
          unitPrice: 3.5,
          totalPrice: 3.5,
          category: "",
          subcategory: "",
          taxCode: "",
        },
      ]),
      {} as ReceiptConfidenceMap,
    );

    expect(result.items).toHaveLength(1);
    expect(result.itemConfidenceById.size).toBe(0);
  });
});

describe("initialTransactionsAndConfidence", () => {
  function makeInitial(
    proposedTransactions: ScanInitialValues["proposedTransactions"],
  ): ScanInitialValues {
    return {
      header: {
        location: "",
        date: "",
        taxAmount: 0,
        storeAddress: "",
        storePhone: "",
      },
      metadata: { receiptId: "", storeNumber: "", terminalId: "" },
      proposedTransactions,
      items: [],
    };
  }

  it("returns empty transactions + empty Map when initialValues is undefined", () => {
    const result = initialTransactionsAndConfidence(undefined, undefined);
    expect(result.transactions).toEqual([]);
    expect(result.transactionConfidenceById.size).toBe(0);
  });

  it("returns empty transactions + empty Map when initialValues has no proposedTransactions", () => {
    const result = initialTransactionsAndConfidence(makeInitial([]), undefined);
    expect(result.transactions).toEqual([]);
    expect(result.transactionConfidenceById.size).toBe(0);
  });

  it("preserves cardId/accountId/amount/date and assigns unique ids", () => {
    const result = initialTransactionsAndConfidence(
      makeInitial([
        {
          cardId: "card-a",
          accountId: "acct-a",
          amount: 54.32,
          date: "2024-06-15",
        },
        {
          cardId: "card-b",
          accountId: "acct-b",
          amount: 5,
          date: "2024-06-15",
        },
      ]),
      undefined,
    );

    expect(result.transactions).toHaveLength(2);
    expect(result.transactions[0]).toMatchObject({
      cardId: "card-a",
      accountId: "acct-a",
      amount: 54.32,
      date: "2024-06-15",
    });
    expect(result.transactions[1]).toMatchObject({
      cardId: "card-b",
      accountId: "acct-b",
      amount: 5,
      date: "2024-06-15",
    });
    expect(result.transactions[0].id).toBeTruthy();
    expect(result.transactions[0].id).not.toBe(result.transactions[1].id);
  });

  it("pairs each transaction id with its confidence entry", () => {
    const result = initialTransactionsAndConfidence(
      makeInitial([
        {
          cardId: "card-a",
          accountId: "acct-a",
          amount: 54.32,
          date: "2024-06-15",
        },
        {
          cardId: "card-b",
          accountId: "acct-b",
          amount: 5,
          date: "2024-06-15",
        },
      ]),
      {
        transactions: [{ cardId: "low" }, { amount: "medium" }],
      },
    );

    expect(
      result.transactionConfidenceById.get(result.transactions[0].id),
    ).toEqual({ cardId: "low" });
    expect(
      result.transactionConfidenceById.get(result.transactions[1].id),
    ).toEqual({ amount: "medium" });
  });
});
