import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import EnvelopeSettings from "./EnvelopeSettings";

vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));

// The defaults card owns its own reads and has its own component test.
vi.mock("@/components/envelopes/ItemTypeDefaultsSection", () => ({
  ItemTypeDefaultsSection: ({ envelopes }: { envelopes: { name: string }[] }) => (
    <div data-testid="item-type-defaults">
      {envelopes.map((envelope) => envelope.name).join(", ")}
    </div>
  ),
}));

vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: vi.fn(),
  useCreateEnvelope: vi.fn(),
  useUpdateEnvelope: vi.fn(),
  useDeleteEnvelope: vi.fn(),
  useOccasions: vi.fn(),
  useCreateOccasion: vi.fn(),
  useUpdateOccasion: vi.fn(),
  useDeleteOccasion: vi.fn(),
}));

import { usePageTitle } from "@/hooks/usePageTitle";
import {
  useCreateEnvelope,
  useCreateOccasion,
  useDeleteEnvelope,
  useDeleteOccasion,
  useEnvelopes,
  useOccasions,
  useUpdateEnvelope,
  useUpdateOccasion,
  type Envelope,
  type Occasion,
} from "@/hooks/useEnvelopes";

const envelopes: Envelope[] = [
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-travel", name: "Travel", isActive: false },
];
const occasions: Occasion[] = [
  { id: "occ-birthday", name: "Birthday", isActive: true },
];

type ReadState = { isLoading?: boolean; isError?: boolean };

function envelopesResult(rows: Envelope[], state: ReadState = {}) {
  return {
    ...mockQueryResult({
      data: { data: rows },
      isLoading: state.isLoading ?? false,
      isError: state.isError ?? false,
    }),
    envelopes: rows,
  } as ReturnType<typeof useEnvelopes>;
}

function occasionsResult(rows: Occasion[], state: ReadState = {}) {
  return {
    ...mockQueryResult({
      data: { data: rows },
      isLoading: state.isLoading ?? false,
      isError: state.isError ?? false,
    }),
    occasions: rows,
  } as ReturnType<typeof useOccasions>;
}

const mutations = {
  createEnvelope: vi.fn(),
  updateEnvelope: vi.fn(),
  deleteEnvelope: vi.fn(),
  createOccasion: vi.fn(),
  updateOccasion: vi.fn(),
  deleteOccasion: vi.fn(),
};

function mutation(mutateAsync: ReturnType<typeof vi.fn>, isPending = false) {
  // Cast through the narrowest shared shape; each hook's mutation result differs.
  return mockMutationResult({ mutateAsync, isPending }) as never;
}

beforeEach(() => {
  for (const fn of Object.values(mutations))
    fn.mockReset().mockResolvedValue(undefined);
  vi.mocked(useEnvelopes).mockReturnValue(envelopesResult(envelopes));
  vi.mocked(useOccasions).mockReturnValue(occasionsResult(occasions));
  vi.mocked(useCreateEnvelope).mockReturnValue(
    mutation(mutations.createEnvelope),
  );
  vi.mocked(useUpdateEnvelope).mockReturnValue(
    mutation(mutations.updateEnvelope),
  );
  vi.mocked(useDeleteEnvelope).mockReturnValue(
    mutation(mutations.deleteEnvelope),
  );
  vi.mocked(useCreateOccasion).mockReturnValue(
    mutation(mutations.createOccasion),
  );
  vi.mocked(useUpdateOccasion).mockReturnValue(
    mutation(mutations.updateOccasion),
  );
  vi.mocked(useDeleteOccasion).mockReturnValue(
    mutation(mutations.deleteOccasion),
  );
});

function card(title: string) {
  return screen
    .getAllByText(title)
    .map((node) => node.closest('[data-slot="card"]'))
    .find(Boolean) as HTMLElement;
}

describe("EnvelopeSettings", () => {
  it("renders the envelope, defaults and occasion sections", () => {
    renderWithProviders(<EnvelopeSettings />);

    expect(usePageTitle).toHaveBeenCalledWith("Envelopes");
    expect(
      screen.getByRole("heading", { level: 1, name: "Envelopes" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "YNAB settings" })).toHaveAttribute(
      "href",
      "/settings/ynab",
    );
    const envelopeCard = card("Groceries");
    expect(within(envelopeCard).getByText("Travel")).toBeInTheDocument();
    expect(within(envelopeCard).getByText("Archived")).toBeInTheDocument();
    expect(within(card("Occasions")).getByText("Birthday")).toBeInTheDocument();
    // Archived envelopes reach the defaults card, which filters its own choices.
    expect(screen.getByTestId("item-type-defaults")).toHaveTextContent(
      "Groceries, Travel",
    );
  });

  it("shows each library's loading state independently", () => {
    vi.mocked(useEnvelopes).mockReturnValue(
      envelopesResult([], { isLoading: true }),
    );
    renderWithProviders(<EnvelopeSettings />);

    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(screen.getByText("Birthday")).toBeInTheDocument();
  });

  it("shows each library's read failure independently", () => {
    vi.mocked(useOccasions).mockReturnValue(
      occasionsResult([], { isError: true }),
    );
    renderWithProviders(<EnvelopeSettings />);

    expect(
      screen.getByText("The occasion list is unavailable."),
    ).toBeInTheDocument();
    expect(screen.getByText("Groceries")).toBeInTheDocument();
  });

  it("creates an envelope and an occasion through their own mutations", async () => {
    const user = userEvent.setup();
    renderWithProviders(<EnvelopeSettings />);

    await user.type(screen.getByLabelText("New envelope"), "Dining");
    await user.click(
      within(card("Groceries")).getByRole("button", { name: "Add" }),
    );
    await user.type(screen.getByLabelText("New occasion"), "Wedding");
    await user.click(
      within(card("Occasions")).getByRole("button", { name: "Add" }),
    );

    await waitFor(() =>
      expect(mutations.createEnvelope).toHaveBeenCalledWith("Dining"),
    );
    await waitFor(() =>
      expect(mutations.createOccasion).toHaveBeenCalledWith("Wedding"),
    );
  });

  it("archives an envelope and deletes an occasion", async () => {
    const user = userEvent.setup();
    renderWithProviders(<EnvelopeSettings />);

    await user.click(
      within(card("Groceries")).getByRole("button", { name: "Archive" }),
    );
    await user.click(screen.getByRole("button", { name: "Delete Birthday" }));

    expect(mutations.updateEnvelope).toHaveBeenCalledWith({
      id: "env-groceries",
      name: "Groceries",
      isActive: false,
    });
    expect(mutations.deleteOccasion).toHaveBeenCalledWith("occ-birthday");
    expect(mutations.deleteEnvelope).not.toHaveBeenCalled();
  });

  it("disables envelope actions while an envelope write is pending, leaving occasions usable", () => {
    vi.mocked(useUpdateEnvelope).mockReturnValue(
      mutation(mutations.updateEnvelope, true),
    );
    renderWithProviders(<EnvelopeSettings />);

    expect(
      within(card("Groceries")).getByRole("button", { name: "Add" }),
    ).toBeDisabled();
    expect(
      within(card("Occasions")).getByRole("button", { name: "Add" }),
    ).toBeEnabled();
  });
});
