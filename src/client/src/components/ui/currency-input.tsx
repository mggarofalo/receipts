import { useState, useRef, type ComponentProps } from "react";
import { cn } from "@/lib/utils";
import { formatDecimal, evaluateMathExpression } from "@/lib/format";

/** Characters allowed while the user is actively typing an expression. */
const EXPRESSION_CHARS = /[^0-9.+\-*/() ]/g;

/** Collapse multiple decimal points into one (same guard the old code used). */
function collapseDoubleDots(s: string): string {
  return s.replace(/(\..*)\./g, "$1");
}

/**
 * For plain-number input, limit to the configured fractional digits and collapse double dots.
 * Returns null when the input contains math operators (caller should skip).
 */
function sanitizePlainNumber(raw: string, precision: 2 | 4): string | null {
  // If it contains math operators or parens, it's an expression — don't sanitize
  if (/[+\-*/()]/.test(raw.replace(/^-/, ""))) return null;

  let sanitized = collapseDoubleDots(raw);
  const parts = sanitized.split(".");
  if (parts.length > 1 && parts[1].length > precision) {
    sanitized = `${parts[0]}.${parts[1].slice(0, precision)}`;
  }
  return sanitized;
}

interface CurrencyInputProps
  extends Omit<ComponentProps<"input">, "type" | "value" | "onChange"> {
  value: number;
  onChange: (value: number) => void;
  onBlur?: () => void;
  symbol?: string;
  /** Unit prices support four decimals; ordinary money inputs retain cents. */
  precision?: 2 | 4;
}

export function CurrencyInput({
  value,
  onChange,
  onBlur,
  onKeyDown,
  symbol = "$",
  precision = 2,
  className,
  ...props
}: CurrencyInputProps) {
  const [text, setText] = useState(() =>
    value === 0 ? "" : formatDecimal(value, precision),
  );
  const inputRef = useRef<HTMLInputElement>(null);
  const [focused, setFocused] = useState(false);
  // Tracks whether commitExpression was already called (e.g. via Enter),
  // so the subsequent blur doesn't fire onChange a second time.
  const committedRef = useRef(false);

  // Track the last value we reported via onChange so we can distinguish
  // external prop changes (form.reset()) from echoed changes (user typing).
  const [lastEmitted, setLastEmitted] = useState(value);

  // Detect external prop changes (e.g. form.reset()) and sync internal text.
  // This is the React-recommended pattern for adjusting state when a prop changes:
  // https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes
  const [prevValue, setPrevValue] = useState(value);
  const [prevPrecision, setPrevPrecision] = useState(precision);
  if (value !== prevValue || precision !== prevPrecision) {
    setPrevValue(value);
    setPrevPrecision(precision);
    // Only sync text for external changes (value differs from what we last emitted).
    // This avoids formatting partial input while the user is typing (e.g. "1" -> "1.00").
    if (value !== lastEmitted || precision !== prevPrecision) {
      setText(value === 0 ? "" : formatDecimal(value, precision));
      setLastEmitted(value);
    }
  }

  // When not focused, show empty string for zero so placeholder is visible
  const displayValue = focused ? text : value === 0 ? "" : formatDecimal(value, precision);

  function handleChange(e: React.ChangeEvent<HTMLInputElement>) {
    // Allow math-expression characters while typing; strip everything else
    const raw = e.target.value.replace(EXPRESSION_CHARS, "");

    const plain = sanitizePlainNumber(raw, precision);
    if (plain !== null) {
      // Plain number path — sanitize before setting state (BUG-001, BUG-002)
      setText(plain);
      const num = parseFloat(plain);
      if (!isNaN(num)) {
        setLastEmitted(num);
        onChange(num);
      } else if (plain === "" || plain === ".") {
        setLastEmitted(0);
        onChange(0);
      }
    } else {
      // Expression path — store as-is, evaluate on blur/Enter
      setText(raw);
    }
  }

  function handleFocus() {
    setFocused(true);
    committedRef.current = false;
    if (value === 0) {
      setText("");
    } else {
      setText(formatDecimal(value, precision));
      setTimeout(() => inputRef.current?.select(), 0);
    }
  }

  function commitExpression() {
    const evaluated = evaluateMathExpression(text);
    const scale = 10 ** precision;
    const final =
      isNaN(evaluated) || !isFinite(evaluated)
        ? 0
        : Math.round(evaluated * scale) / scale;
    setText(final === 0 ? "" : formatDecimal(final, precision));
    setLastEmitted(final);
    onChange(final);
    committedRef.current = true;
  }

  function handleBlur() {
    setFocused(false);
    // Skip if commitExpression was already called via Enter (BUG-003)
    if (!committedRef.current) {
      commitExpression();
    }
    committedRef.current = false;
    onBlur?.();
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") {
      // Only intercept Enter when the text contains math operators.
      // For plain numbers, let the event propagate so forms can submit.
      const hasMathOperators = /[+\-*/()]/.test(text.replace(/^-/, ""));
      if (hasMathOperators) {
        e.preventDefault();
      }
      commitExpression();
    }
    // Chain any caller-supplied handler so the value is committed first.
    onKeyDown?.(e);
  }

  function handlePaste(e: React.ClipboardEvent<HTMLInputElement>) {
    e.preventDefault();
    const pasted = e.clipboardData.getData("text");
    // Allow math expressions in pasted content too
    const cleaned = pasted.replace(EXPRESSION_CHARS, "");

    const plain = sanitizePlainNumber(cleaned, precision);
    if (plain !== null) {
      // Plain number — sanitize and update immediately
      setText(plain);
      const final = parseFloat(plain) || 0;
      setLastEmitted(final);
      onChange(final);
    } else {
      // Expression — store as-is, wait for blur/Enter
      setText(cleaned);
    }
  }

  return (
    <div className="relative">
      <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground text-sm">
        {symbol}
      </span>
      <input
        ref={inputRef}
        type="text"
        inputMode="decimal"
        autoComplete="off"
        value={displayValue}
        placeholder={formatDecimal(0, precision)}
        onChange={handleChange}
        onFocus={handleFocus}
        onBlur={handleBlur}
        onKeyDown={handleKeyDown}
        onPaste={handlePaste}
        className={cn(
          "flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-xs transition-[color,box-shadow] file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
          "pl-7 text-right [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none",
          className,
        )}
        {...props}
      />
    </div>
  );
}
