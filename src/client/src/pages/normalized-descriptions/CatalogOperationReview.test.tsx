import { toApiError } from "@/lib/problem-details";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CatalogOperationReview } from "./CatalogOperationReview";
import {
  useCatalogActions,
  type CatalogOperation,
} from "@/hooks/useNormalizationCatalog";
vi.mock("@/hooks/useNormalizationCatalog", () => ({
  useCatalogActions: vi.fn(),
}));
const mutate = vi.fn();
const base: CatalogOperation = {
  id: "operation-1",
  revision: "revision-1",
  state: "preview",
  source: { location: "Store", description: "BF FRANKS", itemCode: "001" },
  mappingId: null,
  targetId: "target",
  targetName: "Beef Franks",
  newCanonicalName: null,
  includeLegacyUnknown: false,
  total: 205,
  eligible: 200,
  protected: 5,
  applied: 0,
  conflicted: 0,
  remaining: 200,
  undone: 0,
  createdAt: "2026-01-01T00:00:00Z",
  exclude: false,
};
let pending = false;
let error: ReturnType<typeof toApiError> | null = null;
beforeEach(() => {
  vi.clearAllMocks();
  pending = false;
  error = null;
  vi.mocked(useCatalogActions).mockImplementation(
    () => ({ operate: { mutate, isPending: pending, error } }) as never,
  );
});

it("requires explicit scope confirmation and resets it when revision changes without losing the operation", async () => {
  const user = userEvent.setup();
  const { rerender } = render(<CatalogOperationReview operation={base} />);
  expect(screen.getByText("205")).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Approve mapping and apply" }),
  ).toBeDisabled();
  await user.click(screen.getByRole("checkbox", { name: /I reviewed/ }));
  await user.click(
    screen.getByRole("button", { name: "Approve mapping and apply" }),
  );
  expect(mutate).toHaveBeenCalledExactlyOnceWith({
    operation: base,
    action: "approve",
  });
  rerender(
    <CatalogOperationReview operation={{ ...base, revision: "revision-2" }} />,
  );
  expect(
    screen.getByRole("checkbox", { name: /I reviewed/ }),
  ).not.toBeChecked();
  expect(
    screen.getByRole("button", { name: "Approve mapping and apply" }),
  ).toBeDisabled();
});

it("keeps a failed mutation visible across a polled revision and prevents duplicate submissions while pending", async () => {
  error = toApiError(409, {
    detail: "Concurrent source edit requires another preview.",
  });
  const { rerender } = render(<CatalogOperationReview operation={base} />);
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Concurrent source edit requires another preview.",
  );
  pending = true;
  rerender(
    <CatalogOperationReview
      operation={{
        ...base,
        revision: "revision-2",
        state: "applying",
        applied: 100,
        remaining: 100,
      }}
    />,
  );
  expect(screen.getByRole("alert")).toHaveTextContent("Concurrent source edit");
  expect(
    screen.getByRole("button", { name: "Stop operation and future reuse" }),
  ).toBeDisabled();
  expect(screen.getByText(/Processing in the background/)).toBeInTheDocument();
});

it("exposes paused recovery and sends cancellation against the currently displayed revision", async () => {
  const user = userEvent.setup();
  const operation = {
    ...base,
    state: "paused",
    mappingId: "mapping",
    applied: 100,
    remaining: 100,
    errorDetail: "History commit interrupted.",
  };
  render(<CatalogOperationReview operation={operation} />);
  expect(screen.getByRole("alert")).toHaveTextContent(
    "History commit interrupted.",
  );
  await user.click(screen.getByRole("button", { name: "Resume operation" }));
  expect(mutate).toHaveBeenLastCalledWith({ operation, action: "resume" });
  await user.click(
    screen.getByRole("button", { name: "Stop operation and future reuse" }),
  );
  expect(mutate).toHaveBeenLastCalledWith({ operation, action: "cancel" });
});

it("requires separate undo confirmation and invalidates it after intervening progress", async () => {
  const user = userEvent.setup();
  const operation = {
    ...base,
    state: "completed",
    mappingId: "mapping",
    applied: 200,
    remaining: 0,
  };
  const { rerender } = render(<CatalogOperationReview operation={operation} />);
  expect(screen.getByRole("button", { name: "Undo operation" })).toBeDisabled();
  await user.click(
    screen.getByRole("checkbox", {
      name: "Revoke this mapping and undo eligible changes",
    }),
  );
  rerender(
    <CatalogOperationReview
      operation={{ ...operation, revision: "revision-2" }}
    />,
  );
  expect(screen.getByRole("button", { name: "Undo operation" })).toBeDisabled();
  await user.click(
    screen.getByRole("checkbox", {
      name: "Revoke this mapping and undo eligible changes",
    }),
  );
  await user.click(screen.getByRole("button", { name: "Undo operation" }));
  expect(mutate).toHaveBeenLastCalledWith({
    operation: { ...operation, revision: "revision-2" },
    action: "undo",
  });
});
