import {
  useState,
  useRef,
  useCallback,
  useMemo,
  type ComponentProps,
  type Ref,
} from "react";
import { useIsTouchDevice } from "@/hooks/useIsTouchDevice";
import { format, parse, isValid, isAfter, isBefore } from "date-fns";
import { CalendarIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Calendar } from "@/components/ui/calendar";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";

/**
 * Supported text input formats for parsing user-typed dates.
 * Tried in order; first valid parse wins.
 */
const PARSE_FORMATS = [
  "yyyy-MM-dd",
  "MM/dd/yyyy",
  "M/d/yyyy",
  "MM-dd-yyyy",
  "M-d-yyyy",
  "MMddyyyy",
  "yyyyMMdd",
];

/** Canonical wire format used by the rest of the app. */
const WIRE_FORMAT = "yyyy-MM-dd";

/** Display format shown to the user in the text input. */
const DISPLAY_FORMAT = "MM/dd/yyyy";

function tryParseDate(text: string): Date | null {
  const trimmed = text.trim();
  if (!trimmed) return null;
  for (const fmt of PARSE_FORMATS) {
    const d = parse(trimmed, fmt, new Date());
    if (isValid(d) && format(d, fmt) === trimmed) return d;
  }
  return null;
}

/** Convert a wire-format value to display text. */
function wireToDisplay(wire: string | undefined): string {
  if (!wire) return "";
  const d = tryParseDate(wire);
  return d ? format(d, DISPLAY_FORMAT) : wire;
}

/**
 * Assigns a value to a React ref, handling both callback refs and ref objects.
 */
function assignRef<T>(ref: Ref<T> | undefined, value: T | null): void {
  if (typeof ref === "function") {
    ref(value);
  } else if (ref && typeof ref === "object" && "current" in ref) {
    (ref as React.MutableRefObject<T | null>).current = value;
  }
}

interface DateInputProps
  extends Omit<
    ComponentProps<"input">,
    "type" | "value" | "onChange" | "onBlur"
  > {
  /** Date value in YYYY-MM-DD wire format. */
  value?: string;
  /** Called with YYYY-MM-DD wire format string. */
  onChange: (value: string) => void;
  onBlur?: () => void;
  /** Maximum selectable date in YYYY-MM-DD format. */
  max?: string;
  /** Minimum selectable date in YYYY-MM-DD format. */
  min?: string;
  /** React 19 ref-as-prop */
  ref?: Ref<HTMLInputElement>;
}

export function DateInput({
  value,
  onChange,
  onBlur,
  max,
  min,
  ref,
  className,
  disabled,
  ...props
}: DateInputProps) {
  const isTouchDevice = useIsTouchDevice();
  const internalRef = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const [isInvalid, setIsInvalid] = useState(false);
  // Local text tracks what the user is typing while focused
  const [localText, setLocalText] = useState("");

  // When not focused, always derive the display from the controlled prop.
  // When focused, show the user's in-progress typed text.
  const displayValue = focused ? localText : wireToDisplay(value);

  // Parse min/max to Date for calendar and validation
  const maxDate = useMemo(
    () => (max ? (tryParseDate(max) ?? undefined) : undefined),
    [max],
  );
  const minDate = useMemo(
    () => (min ? (tryParseDate(min) ?? undefined) : undefined),
    [min],
  );

  // Build calendar disabled matcher combining min and max
  const calendarDisabled = useMemo(() => {
    const matchers: Array<{ before: Date } | { after: Date }> = [];
    if (minDate) matchers.push({ before: minDate });
    if (maxDate) matchers.push({ after: maxDate });
    return matchers.length > 0 ? matchers : undefined;
  }, [minDate, maxDate]);

  // Callback ref that assigns to both internal and forwarded ref
  const mergedRef = useCallback(
    (el: HTMLInputElement | null) => {
      (internalRef as React.MutableRefObject<HTMLInputElement | null>).current =
        el;
      assignRef(ref, el);
    },
    [ref],
  );

  const commitText = useCallback(
    (raw: string) => {
      const d = tryParseDate(raw);
      if (d) {
        // Enforce max bound
        if (maxDate && isAfter(d, maxDate)) {
          setIsInvalid(true);
          return;
        }
        // Enforce min bound
        if (minDate && isBefore(d, minDate)) {
          setIsInvalid(true);
          return;
        }
        setIsInvalid(false);
        const wire = format(d, WIRE_FORMAT);
        // Skip redundant onChange when value hasn't changed (e.g., calendar pick → blur)
        if (wire !== value) {
          onChange(wire);
        }
      } else if (!raw.trim()) {
        setIsInvalid(false);
        if (value !== "") {
          onChange("");
        }
      } else {
        // Non-empty but unparseable text — mark invalid
        setIsInvalid(true);
      }
    },
    [onChange, maxDate, minDate, value],
  );

  function handleFocus() {
    setFocused(true);
    // Initialize local text from the current wire value
    setLocalText(wireToDisplay(value));
  }

  function handleTextChange(e: React.ChangeEvent<HTMLInputElement>) {
    setLocalText(e.target.value);
    // Don't eagerly commit — let commitText on blur/Enter handle parsing.
    // This prevents wrong intermediate dates from firing onChange.
  }

  function handleBlur() {
    commitText(localText);
    setFocused(false);
    onBlur?.();
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") {
      commitText(localText);
      // Blur triggers the normal blur flow which reformats the display
      internalRef.current?.blur();
    }
  }

  function handleCalendarSelect(date: Date | undefined) {
    if (date) {
      const wire = format(date, WIRE_FORMAT);
      onChange(wire);
      setIsInvalid(false);
      // Also update local text in case input is still focused
      setLocalText(format(date, DISPLAY_FORMAT));
    }
    setOpen(false);
  }

  // Convert wire value to Date for the calendar's `selected` prop
  const selectedDate = value ? (tryParseDate(value) ?? undefined) : undefined;

  if (isTouchDevice) {
    return (
      <input
        ref={mergedRef}
        type="date"
        data-slot="input"
        autoComplete="off"
        value={value || ""}
        min={min}
        max={max}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur}
        disabled={disabled}
        aria-invalid={isInvalid || undefined}
        className={cn(
          "file:text-foreground placeholder:text-muted-foreground selection:bg-primary selection:text-primary-foreground dark:bg-input/30 border-input h-9 w-full min-w-0 rounded-md border bg-transparent px-3 py-1 text-base shadow-xs transition-[color,box-shadow] file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm",
          className,
        )}
        {...props}
      />
    );
  }

  return (
    <div className="relative flex items-center">
      <input
        ref={mergedRef}
        type="text"
        inputMode="text"
        data-slot="input"
        autoComplete="off"
        value={displayValue}
        onFocus={handleFocus}
        onChange={handleTextChange}
        onBlur={handleBlur}
        onKeyDown={handleKeyDown}
        placeholder="MM/DD/YYYY"
        disabled={disabled}
        aria-invalid={isInvalid || undefined}
        className={cn(
          "file:text-foreground placeholder:text-muted-foreground selection:bg-primary selection:text-primary-foreground dark:bg-input/30 border-input h-9 w-full min-w-0 rounded-md border bg-transparent px-3 py-1 pr-11 text-base shadow-xs transition-[color,box-shadow] file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm",
          "aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
          className,
        )}
        {...props}
      />
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            disabled={disabled}
            className="absolute right-0 size-11 rounded-l-none text-muted-foreground hover:text-foreground"
            aria-label="Pick a date"
            aria-expanded={open}
          >
            <CalendarIcon className="h-4 w-4" />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="end">
          <Calendar
            mode="single"
            selected={selectedDate}
            onSelect={handleCalendarSelect}
            defaultMonth={selectedDate}
            disabled={calendarDisabled}
          />
        </PopoverContent>
      </Popover>
    </div>
  );
}
