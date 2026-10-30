import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@/test/setup-combobox-polyfills";
import { mockMutationResult, mockQueryResult } from "@/test/mock-hooks";
import { ItemTypeDefaultsSection } from "./ItemTypeDefaultsSection";

vi.mock("@/hooks/useCategoryTaxonomy", () => ({
  useCategoryTaxonomy: vi.fn(),
}));

vi.mock("@/hooks/useEnvelopes", () => ({
  useItemTypeEnvelopeDefaults: vi.fn(),
  useSetItemTypeEnvelopeDefault: vi.fn(),
  useDeleteItemTypeEnvelopeDefault: vi.fn(),
}));

import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import {
  useDeleteItemTypeEnvelopeDefault,
  useItemTypeEnvelopeDefaults,
  useSetItemTypeEnvelopeDefault,
  type Envelope,
  type ItemTypeEnvelopeDefault,
} from "@/hooks/useEnvelopes";

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.setPointerCapture ??= () => {};
  Element.prototype.releasePointerCapture ??= () => {};
});

const taxonomy = {
  id: "revision-1",
  nodes: [
    { id: "food", name: "Food", parentId: null, archived: false },
    { id: "groceries", name: "Groceries", parentId: "food", archived: false },
    { id: "transport", name: "Transport", parentId: null, archived: false },
  ],
};

const envelopes: Envelope[] = [
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-fun", name: "Fun money", isActive: true },
  { id: "env-old", name: "Old budget", isActive: false },
];

function defaultsResult(
  defaults: ItemTypeEnvelopeDefault[],
  state: { isLoading?: boolean; isError?: boolean } = {},
) {
  return {
    ...mockQueryResult({
      data: { data: defaults },
      isLoading: state.isLoading ?? false,
      isError: state.isError ?? false,
      isSuccess: !state.isLoading && !state.isError,
    }),
    defaults,
  } as ReturnType<typeof useItemTypeEnvelopeDefaults>;
}

let setDefault: ReturnType<typeof vi.fn>;
let deleteDefault: ReturnType<typeof vi.fn>;

beforeEach(() => {
  setDefault = vi.fn();
  deleteDefault = vi.fn();
  vi.mocked(useCategoryTaxonomy).mockReturnValue(
    mockQueryResult({
      data: taxonomy,
      isSuccess: true,
      isLoading: false,
      isError: false,
    }) as ReturnType<typeof useCategoryTaxonomy>,
  );
  vi.mocked(useItemTypeEnvelopeDefaults).mockReturnValue(defaultsResult([]));
  vi.mocked(useSetItemTypeEnvelopeDefault).mockReturnValue(
    mockMutationResult({ mutate: setDefault }) as ReturnType<
      typeof useSetItemTypeEnvelopeDefault
    >,
  );
  vi.mocked(useDeleteItemTypeEnvelopeDefault).mockReturnValue(
    mockMutationResult({ mutate: deleteDefault }) as ReturnType<
      typeof useDeleteItemTypeEnvelopeDefault
    >,
  );
});

describe("ItemTypeDefaultsSection", () => {
  it("lists defaults by item type path and envelope name, sorted by path", () => {
    vi.mocked(useItemTypeEnvelopeDefaults).mockReturnValue(
      defaultsResult([
        {
          categoryId: "transport",
          envelopeId: "env-fun",
          updatedAt: "2026-01-01T00:00:00Z",
        },
        {
          categoryId: "groceries",
          envelopeId: "env-groceries",
          updatedAt: "2026-01-01T00:00:00Z",
        },
        {
          categoryId: "retired",
          envelopeId: "env-missing",
          updatedAt: "2026-01-01T00:00:00Z",
        },
      ]),
    );

    render(<ItemTypeDefaultsSection envelopes={envelopes} />);

    const rows = within(screen.getByRole("list")).getAllByRole("listitem");
    expect(rows.map((row) => row.textContent)).toEqual([
      expect.stringContaining("Archived or unknown item type"),
      expect.stringContaining("Food / Groceries"),
      expect.stringContaining("Transport"),
    ]);
    expect(rows[0]).toHaveTextContent("Unknown envelope");
    expect(rows[1]).toHaveTextContent("Groceries");
    expect(rows[2]).toHaveTextContent("Fun money");
  });

  it.each([
    [{ isLoading: true }, "Loading…"],
    [{ isError: true }, "Item type defaults are unavailable."],
    [{}, /No defaults yet/],
  ])("shows the %o state", (state, text) => {
    vi.mocked(useItemTypeEnvelopeDefaults).mockReturnValue(
      defaultsResult([], state),
    );

    render(<ItemTypeDefaultsSection envelopes={envelopes} />);

    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it("offers only active envelopes as a new default", async () => {
    const user = userEvent.setup();
    render(<ItemTypeDefaultsSection envelopes={envelopes} />);

    await user.click(screen.getByRole("combobox", { name: "Default envelope" }));

    expect(screen.getByRole("option", { name: "Groceries" })).toBeVisible();
    expect(screen.getByRole("option", { name: "Fun money" })).toBeVisible();
    expect(
      screen.queryByRole("option", { name: "Old budget" }),
    ).not.toBeInTheDocument();
  });

  it("saves a default only once both an item type and an envelope are chosen", async () => {
    const user = userEvent.setup();
    render(<ItemTypeDefaultsSection envelopes={envelopes} />);
    const save = screen.getByRole("button", { name: "Save default" });
    expect(save).toBeDisabled();

    await user.click(screen.getByRole("combobox", { name: "Item type" }));
    await user.click(screen.getByRole("option", { name: "Food / Groceries" }));
    expect(save).toBeDisabled();
    await user.click(screen.getByRole("combobox", { name: "Default envelope" }));
    await user.click(screen.getByRole("option", { name: "Fun money" }));
    await user.click(save);

    expect(setDefault).toHaveBeenCalledWith(
      { categoryId: "groceries", envelopeId: "env-fun" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("removes a default by item type", async () => {
    const user = userEvent.setup();
    vi.mocked(useItemTypeEnvelopeDefaults).mockReturnValue(
      defaultsResult([
        {
          categoryId: "groceries",
          envelopeId: "env-groceries",
          updatedAt: "2026-01-01T00:00:00Z",
        },
      ]),
    );
    render(<ItemTypeDefaultsSection envelopes={envelopes} />);

    await user.click(
      screen.getByRole("button", { name: "Remove default for Food / Groceries" }),
    );

    expect(deleteDefault).toHaveBeenCalledWith("groceries");
  });

  it("disables choices while a default write is pending", () => {
    vi.mocked(useSetItemTypeEnvelopeDefault).mockReturnValue(
      mockMutationResult({ mutate: setDefault, isPending: true }) as ReturnType<
        typeof useSetItemTypeEnvelopeDefault
      >,
    );
    render(<ItemTypeDefaultsSection envelopes={envelopes} />);

    expect(
      screen.getByRole("combobox", { name: "Default envelope" }),
    ).toBeDisabled();
    expect(screen.getByRole("button", { name: "Save default" })).toBeDisabled();
  });
});
