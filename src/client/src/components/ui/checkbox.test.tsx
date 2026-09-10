import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { zodResolver } from "@hookform/resolvers/zod";
import { Checkbox } from "./checkbox";
import {
  Form,
  FormField,
  FormItem,
  FormLabel,
  FormControl,
  FormMessage,
} from "./form";

// ---------------------------------------------------------------------------
// Standalone Checkbox tests
// ---------------------------------------------------------------------------

describe("Checkbox", () => {
  it("renders as a button with role=checkbox", () => {
    render(<Checkbox aria-label="Accept terms" />);
    expect(screen.getByRole("checkbox", { name: "Accept terms" })).toBeInTheDocument();
  });

  it("is unchecked by default", () => {
    render(<Checkbox aria-label="Accept terms" />);
    const checkbox = screen.getByRole("checkbox", { name: "Accept terms" });
    expect(checkbox).toHaveAttribute("data-state", "unchecked");
  });

  it("reflects checked state when controlled", () => {
    render(<Checkbox aria-label="Accept terms" checked />);
    const checkbox = screen.getByRole("checkbox", { name: "Accept terms" });
    expect(checkbox).toHaveAttribute("data-state", "checked");
  });

  it("calls onCheckedChange when clicked", async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();
    render(
      <Checkbox aria-label="Accept terms" onCheckedChange={onCheckedChange} />,
    );
    await user.click(screen.getByRole("checkbox", { name: "Accept terms" }));
    expect(onCheckedChange).toHaveBeenCalledWith(true);
  });

  it("does not call onCheckedChange when disabled", async () => {
    const user = userEvent.setup();
    const onCheckedChange = vi.fn();
    render(
      <Checkbox
        aria-label="Accept terms"
        disabled
        onCheckedChange={onCheckedChange}
      />,
    );
    await user.click(screen.getByRole("checkbox", { name: "Accept terms" }));
    expect(onCheckedChange).not.toHaveBeenCalled();
  });

  it("uses border-input token class (no hardcoded border-gray-300)", () => {
    render(<Checkbox aria-label="Token check" />);
    const checkbox = screen.getByRole("checkbox", { name: "Token check" });
    expect(checkbox.className).toContain("border-input");
    expect(checkbox.className).not.toContain("border-gray-300");
  });

  it("delegates focus-visible styling to the global focus rule", () => {
    render(<Checkbox aria-label="Focus check" />);
    const checkbox = screen.getByRole("checkbox", { name: "Focus check" });
    expect(checkbox.className).not.toContain("focus-visible:");
  });
});

// ---------------------------------------------------------------------------
// Checkbox inside react-hook-form (label association)
// ---------------------------------------------------------------------------

const schema = z.object({
  isActive: z.boolean(),
});
type TestValues = z.infer<typeof schema>;

function CheckboxForm({
  onSubmit = () => {},
  defaultValues = { isActive: false },
}: {
  onSubmit?: (values: TestValues) => void;
  defaultValues?: Partial<TestValues>;
}) {
  const form = useForm<TestValues>({
    resolver: zodResolver(schema),
    defaultValues: { isActive: false, ...defaultValues },
  });

  return (
    <Form {...form}>
      <form onSubmit={form.handleSubmit(onSubmit)}>
        <FormField
          control={form.control}
          name="isActive"
          render={({ field }) => (
            <FormItem>
              <div className="flex items-center gap-2">
                <FormControl>
                  <Checkbox
                    checked={field.value}
                    onCheckedChange={field.onChange}
                  />
                </FormControl>
                <FormLabel>Active</FormLabel>
              </div>
              <FormMessage />
            </FormItem>
          )}
        />
        <button type="submit">Submit</button>
      </form>
    </Form>
  );
}

describe("Checkbox in Form", () => {
  it("renders label text", () => {
    render(<CheckboxForm />);
    expect(screen.getByText("Active")).toBeInTheDocument();
  });

  it("label is programmatically associated with the checkbox via htmlFor/id", () => {
    render(<CheckboxForm />);
    // getByLabelText will find the checkbox only if htmlFor matches id
    const checkbox = screen.getByLabelText("Active");
    expect(checkbox).toBeInTheDocument();
    expect(checkbox).toHaveAttribute("role", "checkbox");
  });

  it("toggles state when clicked", async () => {
    const user = userEvent.setup();
    render(<CheckboxForm defaultValues={{ isActive: false }} />);
    const checkbox = screen.getByLabelText("Active");
    expect(checkbox).toHaveAttribute("data-state", "unchecked");
    await user.click(checkbox);
    expect(checkbox).toHaveAttribute("data-state", "checked");
  });

  it("submits the correct boolean value", async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    render(<CheckboxForm onSubmit={onSubmit} defaultValues={{ isActive: false }} />);
    await user.click(screen.getByLabelText("Active"));
    await user.click(screen.getByRole("button", { name: /submit/i }));
    // react-hook-form handleSubmit passes (values, event) to the onSubmit handler
    expect(onSubmit).toHaveBeenCalledWith({ isActive: true }, expect.anything());
  });
});
