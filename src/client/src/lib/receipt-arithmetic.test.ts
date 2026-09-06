import Decimal from "decimal.js";
import vectors from "../../../../test-data/receipt-arithmetic.json";
import {
  calculateLineTotal,
  calculateReceiptBalance,
  calculateSubtotal,
  sumAmounts,
} from "./receipt-arithmetic";

describe("receipt arithmetic shared with the actual API mapper and aggregates", () => {
  it.each(vectors.cases)("$id", (vector) => {
    for (const line of vector.lines) {
      expect(calculateLineTotal(line.quantity, line.unitPrice)).toBe(
        line.total,
      );
    }
    const subtotal = calculateSubtotal(vector.lines);
    const adjustmentTotal = sumAmounts(vector.adjustments);
    const expectedTotal = sumAmounts([subtotal, vector.tax, adjustmentTotal]);
    const paymentTotal = sumAmounts(vector.payments);
    expect(subtotal).toBe(vector.subtotal);
    expect(adjustmentTotal).toBe(vector.adjustmentTotal);
    expect(expectedTotal).toBe(vector.expectedTotal);
    expect(paymentTotal).toBe(vector.paymentTotal);
    expect(calculateReceiptBalance(expectedTotal, paymentTotal)).toEqual({
      isValid: true,
      absoluteDifference: vector.absoluteDifference,
      expectedExceedsPayments: vector.expectedExceedsPayments,
      isWithinCreationTolerance: vector.isWithinCreationTolerance,
      hasVisibleDiscrepancy: vector.hasVisibleDiscrepancy,
      reconciliationAdjustment: vector.reconciliationAdjustment,
      isReconciled: vector.isReconciled,
    });
  });

  it.each([
    [0.5, -2.01, -1.01],
    [-0.5, 2.01, -1.01],
    [-0.5, -2.01, 1.01],
  ])(
    "rounds signed midpoint %s × %s away from zero",
    (quantity, price, expected) => {
      expect(calculateLineTotal(quantity, price)).toBe(expected);
    },
  );

  it("does not round existing amounts individually while adding", () => {
    expect(sumAmounts([0.004, 0.004])).toBe(0.008);
    expect(sumAmounts([])).toBe(0);
    expect(calculateSubtotal([])).toBe(0);
  });

  it.each([0.005, -0.005])(
    "retains the signed half-cent reconciliation boundary %s",
    (delta) => {
      const balance = calculateReceiptBalance(0, delta);
      expect(balance.isWithinCreationTolerance).toBe(true);
      expect(balance.hasVisibleDiscrepancy).toBe(true);
      expect(balance.reconciliationAdjustment).toBe(delta > 0 ? 0.01 : -0.01);
      expect(balance.isReconciled).toBe(false);
    },
  );

  it.each([Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY])(
    "never treats nonfinite amount %s as balanced or reconciled",
    (invalid) => {
      for (const [expected, payments] of [
        [invalid, 1],
        [1, invalid],
        [invalid, invalid],
      ]) {
        const balance = calculateReceiptBalance(expected, payments);
        expect(balance.isValid).toBe(false);
        expect(balance.isWithinCreationTolerance).toBe(false);
        expect(balance.isReconciled).toBe(false);
        expect(balance.hasVisibleDiscrepancy).toBe(true);
        expect(Number.isFinite(balance.reconciliationAdjustment)).toBe(false);
      }
      expect(Number.isFinite(sumAmounts([1, invalid]))).toBe(false);
      expect(Number.isFinite(calculateLineTotal(1, invalid))).toBe(false);
    },
  );

  it("does not inherit or modify another Decimal consumer's global rounding policy", () => {
    const previous = {
      precision: Decimal.precision,
      rounding: Decimal.rounding,
    };
    try {
      Decimal.set({ precision: 3, rounding: Decimal.ROUND_DOWN });
      expect(calculateLineTotal(1.2345, 7.8912)).toBe(9.74);
      expect(calculateLineTotal(0.5, 2.01)).toBe(1.01);
      expect(Decimal.precision).toBe(3);
      expect(Decimal.rounding).toBe(Decimal.ROUND_DOWN);
    } finally {
      Decimal.set(previous);
    }
  });
});
