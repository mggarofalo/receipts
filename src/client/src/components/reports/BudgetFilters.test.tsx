import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { mockQueryResult } from "@/test/mock-hooks";
import type { Envelope, Occasion } from "@/hooks/useEnvelopes";
import { BudgetFilters } from "./BudgetFilters";

vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: vi.fn(),
  useOccasions: vi.fn(),
}));
import { useEnvelopes, useOccasions } from "@/hooks/useEnvelopes";

const GROCERIES = "11111111-1111-1111-1111-111111111111";
const OLD_RENT = "22222222-2222-2222-2222-222222222222";
const BIRTHDAY = "33333333-3333-3333-3333-333333333333";
const WEDDING = "44444444-4444-4444-4444-444444444444";

const envelopes: Envelope[] = [
  { id: GROCERIES, name: "Groceries", isActive: true },
  { id: OLD_RENT, name: "Old rent", isActive: false },
];
const occasions: Occasion[] = [
  { id: BIRTHDAY, name: "Birthday", isActive: true },
  { id: WEDDING, name: "Wedding", isActive: false },
];

function setup(lists: { envelopes?: Envelope[]; occasions?: Occasion[] } = {}) {
  const envelopeList = lists.envelopes ?? envelopes;
  const occasionList = lists.occasions ?? occasions;
  vi.mocked(useEnvelopes).mockReturnValue({
    ...mockQueryResult({
      data: { data: envelopeList },
      isSuccess: true,
      isLoading: false,
    }),
    envelopes: envelopeList,
  });
  vi.mocked(useOccasions).mockReturnValue({
    ...mockQueryResult({
      data: { data: occasionList },
      isSuccess: true,
      isLoading: false,
    }),
    occasions: occasionList,
  });
}

beforeEach(() => vi.clearAllMocks());

it("lists archived envelopes and occasions, labelled as archived", () => {
  setup();
  render(<BudgetFilters onChange={vi.fn()} />);
  const envelope = screen.getByLabelText("Envelope filter");
  expect(
    Array.from(envelope.querySelectorAll("option"), (o) => o.textContent),
  ).toEqual(["All envelopes", "Groceries", "Old rent (archived)"]);
  const occasion = screen.getByLabelText("Occasion filter");
  expect(
    Array.from(occasion.querySelectorAll("option"), (o) => o.textContent),
  ).toEqual(["All occasions", "Birthday", "Wedding (archived)"]);
});

it("shows the current selection", () => {
  setup();
  render(
    <BudgetFilters
      envelopeId={OLD_RENT}
      occasionId={BIRTHDAY}
      onChange={vi.fn()}
    />,
  );
  expect(screen.getByLabelText("Envelope filter")).toHaveValue(OLD_RENT);
  expect(screen.getByLabelText("Occasion filter")).toHaveValue(BIRTHDAY);
});

it("reports a chosen id, and null when cleared", async () => {
  setup();
  const onChange = vi.fn();
  const { rerender } = render(<BudgetFilters onChange={onChange} />);
  await userEvent.selectOptions(
    screen.getByLabelText("Envelope filter"),
    "Old rent (archived)",
  );
  expect(onChange).toHaveBeenLastCalledWith({ envelopeId: OLD_RENT });
  await userEvent.selectOptions(
    screen.getByLabelText("Occasion filter"),
    "Wedding (archived)",
  );
  expect(onChange).toHaveBeenLastCalledWith({ occasionId: WEDDING });

  rerender(
    <BudgetFilters
      envelopeId={OLD_RENT}
      occasionId={WEDDING}
      onChange={onChange}
    />,
  );
  await userEvent.selectOptions(
    screen.getByLabelText("Envelope filter"),
    "All envelopes",
  );
  expect(onChange).toHaveBeenLastCalledWith({ envelopeId: null });
  await userEvent.selectOptions(
    screen.getByLabelText("Occasion filter"),
    "All occasions",
  );
  expect(onChange).toHaveBeenLastCalledWith({ occasionId: null });
});

it("hides a filter whose list is empty", () => {
  setup({ envelopes: [] });
  const { unmount } = render(<BudgetFilters onChange={vi.fn()} />);
  expect(screen.queryByLabelText("Envelope filter")).toBeNull();
  expect(screen.getByLabelText("Occasion filter")).toBeInTheDocument();
  unmount();

  setup({ occasions: [] });
  render(<BudgetFilters onChange={vi.fn()} />);
  expect(screen.getByLabelText("Envelope filter")).toBeInTheDocument();
  expect(screen.queryByLabelText("Occasion filter")).toBeNull();
});

it("hides a filter the caller turns off", () => {
  setup();
  render(<BudgetFilters showEnvelope={false} onChange={vi.fn()} />);
  expect(screen.queryByLabelText("Envelope filter")).toBeNull();
  expect(screen.getByLabelText("Occasion filter")).toBeInTheDocument();
});
