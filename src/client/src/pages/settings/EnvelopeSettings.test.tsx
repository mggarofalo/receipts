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

vi.mock("@/hooks/useYnab", () => ({ useYnabEnvelopeMappings: vi.fn() }));

import { usePageTitle } from "@/hooks/usePageTitle";
import { useYnabEnvelopeMappings } from "@/hooks/useYnab";
import type { components } from "@/generated/api";
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
type YnabLink = components["schemas"]["YnabEnvelopeMappingResponse"];

const groceriesLink: YnabLink = {
  id: "map-groceries",
  envelopeId: "env-groceries",
  envelopeName: "Groceries",
  ynabCategoryId: "ynab-cat-groceries",
  ynabCategoryName: "Groceries",
  ynabCategoryGroupName: "Everyday",
  ynabBudgetId: "ynab-budget",
  createdAt: "2026-10-01T00:00:00Z",
  updatedAt: "2026-10-01T00:00:00Z",
};

function ynabLinksResult(
  links: YnabLink[],
  state: { isLoading?: boolean; isError?: boolean } = {},
) {
  const settled = !state.isLoading && !state.isError;
  return {
    ...mockQueryResult({
      data: settled ? { data: links } : undefined,
      isLoading: state.isLoading ?? false,
      isPending: !settled && !state.isError,
      isError: state.isError ?? false,
      isSuccess: settled,
      status: state.isError ? "error" : settled ? "success" : "pending",
    }),
    mappings: settled ? links : [],
  } as ReturnType<typeof useYnabEnvelopeMappings>;
}

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
  vi.mocked(useYnabEnvelopeMappings).mockReturnValue(
    ynabLinksResult([groceriesLink]),
  );
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
    await user.click(
      within(
        await screen.findByRole("alertdialog", { name: "Delete occasion?" }),
      ).getByRole("button", { name: "Delete" }),
    );

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

  it("shows each envelope's YNAB category with its group, or that it is not linked", () => {
    renderWithProviders(<EnvelopeSettings />);

    const envelopeCard = card("Groceries");
    const row = (name: string) =>
      within(envelopeCard).getByText(name).closest("li") as HTMLElement;
    expect(
      within(row("Groceries")).getByText("YNAB: Everyday: Groceries"),
    ).toBeInTheDocument();
    expect(
      within(row("Travel")).getByText("Not linked to YNAB"),
    ).toBeInTheDocument();
    // Occasions are not budget envelopes and have no YNAB link.
    expect(
      within(card("Occasions")).queryByText(/YNAB/),
    ).not.toBeInTheDocument();
  });

  it.each([
    ["loading", { isLoading: true }],
    ["unavailable", { isError: true }],
  ])("shows no YNAB line while the links are %s", (_, state) => {
    vi.mocked(useYnabEnvelopeMappings).mockReturnValue(
      ynabLinksResult([groceriesLink], state),
    );
    renderWithProviders(<EnvelopeSettings />);

    expect(screen.getByText("Groceries")).toBeInTheDocument();
    expect(screen.queryByText(/YNAB:/)).not.toBeInTheDocument();
    expect(screen.queryByText("Not linked to YNAB")).not.toBeInTheDocument();
  });
});
