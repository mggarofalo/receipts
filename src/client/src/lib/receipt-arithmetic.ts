import Decimal from "decimal.js";

// Precision is significant digits, not decimal places. Keep this constructor
// private so other users of Decimal cannot change receipt calculation policy.
const ReceiptDecimal = Decimal.clone({
  precision: 40,
  rounding: Decimal.ROUND_HALF_UP,
});

interface ReceiptLineAmounts {
  quantity: number;
  unitPrice: number;
}

export interface ReceiptBalance {
  isValid: boolean;
  absoluteDifference: number;
  expectedExceedsPayments: boolean;
  isWithinCreationTolerance: boolean;
  hasVisibleDiscrepancy: boolean;
  reconciliationAdjustment: number;
  isReconciled: boolean;
}

function toAmount(value: Decimal): number {
  return value.isZero() ? 0 : value.toNumber();
}

function lineAmount(quantity: number, unitPrice: number): Decimal {
  // Convert each operand before multiplying. Round each line before summing,
  // matching the API mapper's decimal MidpointRounding.AwayFromZero policy.
  return new ReceiptDecimal(quantity).times(unitPrice).toDecimalPlaces(2);
}

export function calculateLineTotal(quantity: number, unitPrice: number): number {
  return toAmount(lineAmount(quantity, unitPrice));
}

export function calculateSubtotal(items: readonly ReceiptLineAmounts[]): number {
  return toAmount(
    items.reduce(
      (total, item) => total.plus(lineAmount(item.quantity, item.unitPrice)),
      new ReceiptDecimal(0),
    ),
  );
}

/** Sum existing amounts without rounding individual tax, adjustment or payment terms. */
export function sumAmounts(amounts: readonly number[]): number {
  return toAmount(
    amounts.reduce((total, amount) => total.plus(amount), new ReceiptDecimal(0)),
  );
}

export function calculateReceiptBalance(
  expectedTotal: number,
  transactionTotal: number,
): ReceiptBalance {
  const isValid = Number.isFinite(expectedTotal) && Number.isFinite(transactionTotal);
  const difference = new ReceiptDecimal(expectedTotal).minus(transactionTotal);
  const absoluteDifference = difference.abs();
  const adjustment = difference.negated().toDecimalPlaces(2);

  return {
    isValid,
    absoluteDifference: toAmount(absoluteDifference),
    expectedExceedsPayments: isValid && difference.greaterThan(0),
    // Creation accepts a one-cent discrepancy, as the backend does. Persisted
    // receipt views still show that discrepancy so it can be reconciled.
    isWithinCreationTolerance: isValid && absoluteDifference.lessThanOrEqualTo("0.01"),
    hasVisibleDiscrepancy: !isValid || absoluteDifference.greaterThanOrEqualTo("0.005"),
    reconciliationAdjustment: toAmount(adjustment),
    isReconciled: isValid && adjustment.isZero(),
  };
}
