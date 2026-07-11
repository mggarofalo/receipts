import { format as formatDateFns, parse, isValid } from "date-fns";

export function formatCurrency(amount: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
  }).format(amount);
}

const DATE_ONLY_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Parses a date value into a Date anchored in the LOCAL timezone.
 *
 * A bare `yyyy-MM-dd` string (how the API serializes `DateOnly`) fed to
 * `new Date(...)` is parsed as UTC midnight, which formats to the *previous*
 * day in every negative-UTC-offset timezone (all of the US). Parsing it with
 * date-fns `parse(value, "yyyy-MM-dd", ...)` instead anchors it to local
 * midnight so the calendar date is preserved everywhere (RECEIPTS-788).
 *
 * Full ISO datetimes (with a time component) still go through `new Date(...)`.
 * Returns null when the value is empty or cannot be parsed.
 */
export function parseDateValue(value: string | null | undefined): Date | null {
  if (!value) return null;
  if (DATE_ONLY_PATTERN.test(value)) {
    const parsed = parse(value, "yyyy-MM-dd", new Date());
    return isValid(parsed) ? parsed : null;
  }
  const fallback = new Date(value);
  return isValid(fallback) ? fallback : null;
}

/**
 * Formats a receipt/transaction date for display, timezone-safe for the
 * date-only strings the API returns. Falls back to the raw value when the
 * input can't be parsed and to an em dash when it's empty. Use this (or
 * `formatShortDate`) for ALL receipt-date display so the list, dashboard, and
 * widgets always agree on the calendar day (RECEIPTS-788).
 */
export function formatDate(
  value: string | null | undefined,
  pattern = "MMM d, yyyy",
): string {
  if (!value) return "—";
  const parsed = parseDateValue(value);
  return parsed ? formatDateFns(parsed, pattern) : value;
}

/** Short "MMM d" variant of {@link formatDate}, e.g. "Jan 15". */
export function formatShortDate(value: string | null | undefined): string {
  return formatDate(value, "MMM d");
}

/**
 * True when `value` is a calendar date strictly after today (local time).
 *
 * Used to catch the server's "Date cannot be in the future" rejection on the
 * client so users get inline feedback before submitting (RECEIPTS-782).
 * Compares date-parts only, so "today" is never considered in the future.
 */
export function isFutureDate(value: string | null | undefined): boolean {
  const parsed = parseDateValue(value);
  if (!parsed) return false;
  const now = new Date();
  const todayStart = new Date(
    now.getFullYear(),
    now.getMonth(),
    now.getDate(),
  ).getTime();
  const valueStart = new Date(
    parsed.getFullYear(),
    parsed.getMonth(),
    parsed.getDate(),
  ).getTime();
  return valueStart > todayStart;
}

export function formatDecimal(value: number, decimals = 2): string {
  return value.toFixed(decimals);
}

export function parseCurrencyInput(raw: string): string {
  return raw.replace(/[^0-9.]/g, "").replace(/(\..*)\./g, "$1");
}

/**
 * Parse and evaluate a simple arithmetic expression containing +, -, *, /
 * with standard operator precedence and optional parentheses.
 *
 * Uses a recursive-descent parser — no eval() or Function() for safety.
 * Returns NaN for invalid or empty expressions, and Infinity/-Infinity for
 * division by zero (callers should treat those as invalid).
 */
export function evaluateMathExpression(input: string): number {
  const expr = input.replace(/\s/g, "");
  if (expr === "") return NaN;

  let pos = 0;

  function peek(): string {
    return expr[pos] ?? "";
  }

  function consume(): string {
    return expr[pos++];
  }

  // number = ["-"] digit+ ["." digit+]
  function parseNumber(): number {
    const start = pos;
    if (peek() === "-") consume();
    if (!/[0-9.]/.test(peek())) return NaN;
    while (/[0-9]/.test(peek())) consume();
    if (peek() === ".") {
      consume();
      while (/[0-9]/.test(peek())) consume();
    }
    return parseFloat(expr.slice(start, pos));
  }

  // atom = number | "(" expression ")"
  function parseAtom(): number {
    if (peek() === "(") {
      consume(); // "("
      const val = parseExpression();
      if (peek() === ")") consume();
      else return NaN; // unmatched paren
      return val;
    }
    return parseNumber();
  }

  // unary = ["-"] atom  (handles negation before parenthesised sub-expressions)
  function parseUnary(): number {
    if (peek() === "-") {
      consume();
      return -parseAtom();
    }
    return parseAtom();
  }

  // term = unary (("*" | "/") unary)*
  function parseTerm(): number {
    let left = parseUnary();
    while (peek() === "*" || peek() === "/") {
      const op = consume();
      const right = parseUnary();
      left = op === "*" ? left * right : left / right;
    }
    return left;
  }

  // expression = term (("+" | "-") term)*
  function parseExpression(): number {
    let left = parseTerm();
    while (peek() === "+" || peek() === "-") {
      const op = consume();
      const right = parseTerm();
      left = op === "+" ? left + right : left - right;
    }
    return left;
  }

  const result = parseExpression();

  // If there are leftover characters the expression was malformed
  if (pos < expr.length) return NaN;

  return result;
}

/**
 * Convert a camelCase or PascalCase string to Title Case with spaces.
 * e.g. "loyaltyRedemption" → "Loyalty Redemption", "taxAmount" → "Tax Amount"
 */
export function camelToTitle(str: string): string {
  return str
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, (c) => c.toUpperCase());
}

/**
 * Capitalize the first letter of a string.
 */
export function capitalize(str: string): string {
  return str.charAt(0).toUpperCase() + str.slice(1);
}
