import { useRef } from "react";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import { HeaderSection } from "./HeaderSection";
import { headerSchema, type HeaderFormValues } from "./headerSchema";
import type { ReceiptConfidenceMap } from "@/pages/scan-receipt/types";

interface HostProps {
  defaultValues?: Partial<HeaderFormValues>;
  confidenceMap?: ReceiptConfidenceMap;
  locationOptions?: Array<{ value: string; label: string }>;
  onSubmit?: (values: HeaderFormValues) => void;
}

function HeaderHost({
  defaultValues,
  confidenceMap,
  locationOptions = [{ value: "Walmart", label: "Walmart" }],
  onSubmit,
}: HostProps) {
  const locationRef = useRef<HTMLButtonElement>(null);
  const form = useForm<HeaderFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(headerSchema) as any,
    defaultValues: {
      location: "",
      date: "",
      taxAmount: 0,
      storeAddress: "",
      storePhone: "",
      ...defaultValues,
    },
  });
  return (
    <form onSubmit={form.handleSubmit((v) => onSubmit?.(v))}>
      <HeaderSection
        form={form}
        locationOptions={locationOptions}
        locationRef={locationRef}
        confidenceMap={confidenceMap}
      />
      <button type="submit">Submit</button>
    </form>
  );
}

describe("HeaderSection", () => {
  it("renders all five header fields", () => {
    renderWithProviders(<HeaderHost />);
    expect(screen.getByText(/^Location/)).toBeInTheDocument();
    expect(screen.getByText(/^Date/)).toBeInTheDocument();
    expect(screen.getByText(/^Tax Amount/)).toBeInTheDocument();
    expect(screen.getByLabelText(/store address/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/store phone/i)).toBeInTheDocument();
  });

  it("pre-populates store address and phone from defaultValues", () => {
    renderWithProviders(
      <HeaderHost
        defaultValues={{
          storeAddress: "123 Main St",
          storePhone: "(555) 123-4567",
        }}
      />,
    );
    expect(
      (screen.getByLabelText(/store address/i) as HTMLInputElement).value,
    ).toBe("123 Main St");
    expect(
      (screen.getByLabelText(/store phone/i) as HTMLInputElement).value,
    ).toBe("(555) 123-4567");
  });

  it("displays low-confidence indicator next to location label when confidenceMap.location is low", () => {
    renderWithProviders(<HeaderHost confidenceMap={{ location: "low" }} />);
    // ConfidenceIndicator renders a chip with an aria-label exposing the rating.
    expect(
      screen.getByLabelText("AI confidence rating: low"),
    ).toBeInTheDocument();
  });

  it("validates a missing location on submit", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <HeaderHost defaultValues={{ date: "2024-06-15", taxAmount: 0 }} />,
    );
    await user.click(screen.getByRole("button", { name: /submit/i }));
    expect(
      await screen.findByText(/location is required/i),
    ).toBeInTheDocument();
  });

  it("validates a missing date on submit", async () => {
    const user = userEvent.setup();
    renderWithProviders(<HeaderHost defaultValues={{ location: "Walmart" }} />);
    await user.click(screen.getByRole("button", { name: /submit/i }));
    expect(await screen.findByText(/date is required/i)).toBeInTheDocument();
  });

  it("rejects an invalid store phone format", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <HeaderHost
        defaultValues={{
          location: "Walmart",
          date: "2024-06-15",
          storePhone: "not-a-phone",
        }}
      />,
    );
    await user.click(screen.getByRole("button", { name: /submit/i }));
    expect(
      await screen.findByText(/store phone is not in a recognised format/i),
    ).toBeInTheDocument();
  });

  it("accepts a valid US store phone", async () => {
    const onSubmit = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(
      <HeaderHost
        defaultValues={{
          location: "Walmart",
          date: "2024-06-15",
          storePhone: "(555) 123-4567",
        }}
        onSubmit={onSubmit}
      />,
    );
    await user.click(screen.getByRole("button", { name: /submit/i }));
    await vi.waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(
        expect.objectContaining({
          location: "Walmart",
          date: "2024-06-15",
          storePhone: "(555) 123-4567",
        }),
      );
    });
  });

  it("rejects a location longer than 200 characters", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <HeaderHost
        defaultValues={{ location: "x".repeat(201), date: "2024-06-15" }}
      />,
    );
    await user.click(screen.getByRole("button", { name: /submit/i }));
    expect(
      await screen.findByText(/location must be 200 characters or fewer/i),
    ).toBeInTheDocument();
  });

  it("rejects a negative tax amount", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <HeaderHost
        defaultValues={{
          location: "Walmart",
          date: "2024-06-15",
          taxAmount: -1,
        }}
      />,
    );
    await user.click(screen.getByRole("button", { name: /submit/i }));
    expect(
      await screen.findByText(/tax amount must be non-negative/i),
    ).toBeInTheDocument();
  });
});
