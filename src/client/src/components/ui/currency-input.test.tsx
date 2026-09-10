import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { CurrencyInput } from "./currency-input";

// Controlled wrapper that re-renders with updated value when onChange fires
function ControlledCurrencyInput({
  initialValue = 0,
  onChange: externalOnChange,
  ...rest
}: {
  initialValue?: number;
  onChange?: (value: number) => void;
} & Omit<React.ComponentProps<typeof CurrencyInput>, "value" | "onChange">) {
  const [value, setValue] = useState(initialValue);
  return (
    <CurrencyInput
      value={value}
      onChange={(v) => {
        setValue(v);
        externalOnChange?.(v);
      }}
      {...rest}
    />
  );
}

describe("CurrencyInput unit-price precision", () => {
  it.each(["type", "paste"] as const)(
    "preserves four decimal places through %s and blur",
    async (method) => {
      const user = userEvent.setup();
      const onChange = vi.fn();
      render(<ControlledCurrencyInput precision={4} onChange={onChange} />);
      const input = screen.getByRole("textbox");
      await user.click(input);
      if (method === "type") await user.type(input, "7.1234");
      else await user.paste("$7.1234");
      await user.tab();
      expect(onChange).toHaveBeenLastCalledWith(7.1234);
      expect(Number((input as HTMLInputElement).value)).toBe(7.1234);
    },
  );

  it("preserves prefilled and externally reset subcent prices on focus and blur", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const { rerender } = render(
      <CurrencyInput precision={4} value={3.459} onChange={onChange} />,
    );
    const input = screen.getByRole("textbox");
    expect(Number((input as HTMLInputElement).value)).toBe(3.459);
    await user.click(input);
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(3.459);
    rerender(
      <CurrencyInput precision={4} value={8.7654} onChange={onChange} />,
    );
    await user.click(input);
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(8.7654);
  });

  it("commits expressions at unit-price precision without changing default money precision", async () => {
    const user = userEvent.setup();
    const price = vi.fn();
    const money = vi.fn();
    render(
      <>
        <ControlledCurrencyInput
          aria-label="Unit price"
          precision={4}
          onChange={price}
        />
        <ControlledCurrencyInput aria-label="Payment" onChange={money} />
      </>,
    );
    for (const label of ["Unit price", "Payment"]) {
      await user.click(screen.getByLabelText(label));
      await user.paste("7 / 2 + 0.0001");
      await user.keyboard("{Enter}");
      await user.tab();
    }
    expect(price).toHaveBeenLastCalledWith(3.5001);
    expect(money).toHaveBeenLastCalledWith(3.5);
    expect(screen.getByLabelText("Payment")).toHaveValue("3.50");
  });
});

describe("CurrencyInput", () => {
  const defaultProps = {
    value: 0,
    onChange: vi.fn(),
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders with formatted value and dollar symbol", () => {
    render(<CurrencyInput {...defaultProps} value={12.5} />);

    const input = screen.getByRole("textbox");
    expect(input).toHaveValue("12.50");
    expect(screen.getByText("$")).toBeInTheDocument();
  });

  it("shows placeholder instead of 0.00 when value is zero", () => {
    render(<CurrencyInput {...defaultProps} value={0} />);

    const input = screen.getByRole("textbox");
    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("placeholder", "0.00");
  });

  it("formats value on blur to two decimal places", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={5} onChange={onChange} />);

    const input = screen.getByRole("textbox");
    await user.click(input);
    await user.clear(input);
    await user.type(input, "7.1");
    await user.tab();

    // On blur, onChange is called with the final parsed number
    expect(onChange).toHaveBeenCalledWith(7.1);
    // After blur, the display should show "7.10" (formatted with two decimals)
    expect(input).toHaveValue("7.10");
  });

  it("strips non-numeric characters during input", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

    const input = screen.getByRole("textbox");
    await user.click(input);
    await user.clear(input);
    await user.type(input, "12");

    // onChange should have been called with numeric values
    const lastCall = onChange.mock.calls[onChange.mock.calls.length - 1];
    expect(lastCall[0]).toBe(12);
  });

  it("calls onChange with parsed number on valid input", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

    const input = screen.getByRole("textbox");
    await user.click(input);
    await user.clear(input);
    await user.type(input, "25.99");

    // Find the call with the final value
    const calls = onChange.mock.calls.map((c) => c[0]);
    expect(calls).toContain(25.99);
  });

  it("handles paste events by stripping non-numeric content", () => {
    const onChange = vi.fn();

    render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

    const input = screen.getByRole("textbox");

    // Focus the input
    fireEvent.focus(input);

    // Use fireEvent.paste which works in jsdom
    fireEvent.paste(input, {
      clipboardData: {
        getData: () => "$1,234.56",
      },
    });

    // The paste handler should parse "1234.56" and call onChange with 1234.56
    expect(onChange).toHaveBeenCalledWith(1234.56);
  });

  it("renders custom currency symbol when provided", () => {
    render(<CurrencyInput {...defaultProps} value={10} symbol="EUR" />);

    expect(screen.getByText("EUR")).toBeInTheDocument();
  });

  it("disables browser autofill with autoComplete='off'", () => {
    render(<CurrencyInput {...defaultProps} />);

    const input = screen.getByRole("textbox");
    expect(input).toHaveAttribute("autocomplete", "off");
  });

  it("delegates focus-visible styling to the global focus rule", () => {
    render(<CurrencyInput {...defaultProps} />);

    const input = screen.getByRole("textbox");
    expect(input.className).not.toContain("focus-visible:");
  });

  it("keeps text empty (not '0.00') when focusing a zero-value field", async () => {
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={0} />);

    const input = screen.getByRole("textbox");
    await user.click(input);

    expect(input).toHaveValue("");
  });

  it("returns to empty with placeholder after focusing and blurring a zero-value field without typing", async () => {
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={0} />);

    const input = screen.getByRole("textbox");
    await user.click(input);
    await user.tab();

    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("placeholder", "0.00");
  });

  it("applies normal formatting when typing a value into a zero-value field and blurring", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

    const input = screen.getByRole("textbox");
    await user.click(input);
    await user.type(input, "42.5");
    await user.tab();

    expect(onChange).toHaveBeenCalledWith(42.5);
    expect(input).toHaveValue("42.50");
  });

  it("clears displayed text when value prop is reset to 0 externally (e.g. form.reset())", async () => {
    function ResettableWrapper() {
      const [value, setValue] = useState(25.99);
      return (
        <>
          <CurrencyInput value={value} onChange={setValue} />
          <button onClick={() => setValue(0)}>Reset</button>
        </>
      );
    }

    const user = userEvent.setup();
    render(<ResettableWrapper />);

    const input = screen.getByRole("textbox");
    expect(input).toHaveValue("25.99");

    await user.click(screen.getByText("Reset"));

    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("placeholder", "0.00");
  });

  it("clears displayed text when value prop is reset to 0 while input is focused (Enter key submit)", async () => {
    function FormResetWrapper() {
      const [value, setValue] = useState(0);
      return (
        <form onSubmit={(e) => { e.preventDefault(); setValue(0); }}>
          <CurrencyInput value={value} onChange={setValue} />
        </form>
      );
    }

    const user = userEvent.setup();
    render(<FormResetWrapper />);

    const input = screen.getByRole("textbox");

    await user.click(input);
    await user.type(input, "5.99");
    expect(input).toHaveValue("5.99");

    await user.keyboard("{Enter}");

    expect(input).toHaveValue("");
  });

  it("updates displayed text when value prop changes to a new non-zero value externally", async () => {
    function ExternalUpdateWrapper() {
      const [value, setValue] = useState(10);
      return (
        <>
          <CurrencyInput value={value} onChange={setValue} />
          <button onClick={() => setValue(42.5)}>Update</button>
        </>
      );
    }

    const user = userEvent.setup();
    render(<ExternalUpdateWrapper />);

    const input = screen.getByRole("textbox");
    expect(input).toHaveValue("10.00");

    await user.click(screen.getByText("Update"));

    expect(input).toHaveValue("42.50");
  });

  describe("math expression support", () => {
    it("allows typing arithmetic operators", async () => {
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "24.99-7.30");

      expect(input).toHaveValue("24.99-7.30");
    });

    it("evaluates expression on blur", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "10+5");
      await user.tab();

      expect(onChange).toHaveBeenCalledWith(15);
      expect(input).toHaveValue("15.00");
    });

    it("evaluates subtraction expression on blur", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "24.99-7.30");
      await user.tab();

      expect(input).toHaveValue("17.69");
    });

    it("evaluates multiplication expression on blur", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "4.50*3");
      await user.tab();

      expect(input).toHaveValue("13.50");
    });

    it("evaluates expression on Enter key", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "10+5");
      await user.keyboard("{Enter}");

      expect(onChange).toHaveBeenCalledWith(15);
      expect(input).toHaveValue("15.00");
    });

    it("rounds result to two decimal places", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "10/3");
      await user.tab();

      expect(input).toHaveValue("3.33");
      expect(onChange).toHaveBeenCalledWith(3.33);
    });

    it("treats invalid expression as zero on blur", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "5+");
      await user.tab();

      expect(onChange).toHaveBeenCalledWith(0);
      expect(input).toHaveValue("");
    });

    it("treats division by zero as zero on blur", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "5/0");
      await user.tab();

      expect(onChange).toHaveBeenCalledWith(0);
      expect(input).toHaveValue("");
    });

    it("blocks more than 2 decimal digits in plain number input (BUG-002 regression)", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "42.999");

      // The third decimal digit should be blocked — value stays at 42.99
      const calls = onChange.mock.calls.map((c) => c[0]);
      expect(calls).not.toContain(42.999);
      expect(input).not.toHaveValue("42.999");
    });

    it("collapses double dots in plain number input (BUG-001 regression)", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "42.5");
      // Try typing a second dot — it should be collapsed
      await user.type(input, ".0");
      await user.tab();

      // Should not evaluate to 0 (which would happen if NaN)
      const lastOnChange = onChange.mock.calls[onChange.mock.calls.length - 1][0];
      expect(lastOnChange).not.toBe(0);
    });

    it("does not fire onChange twice when Enter is followed by blur (BUG-003 regression)", async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();

      render(<ControlledCurrencyInput initialValue={0} onChange={onChange} />);

      const input = screen.getByRole("textbox");
      await user.click(input);
      await user.type(input, "10+5");

      // Clear calls from typing
      onChange.mockClear();

      // Press Enter (commits expression)
      await user.keyboard("{Enter}");
      const callsAfterEnter = onChange.mock.calls.length;
      expect(callsAfterEnter).toBe(1);
      expect(onChange).toHaveBeenCalledWith(15);

      // Tab away (should NOT fire onChange again)
      onChange.mockClear();
      await user.tab();
      expect(onChange.mock.calls.length).toBe(0);
    });
  });
});
