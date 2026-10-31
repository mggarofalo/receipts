import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import type {
  RestructureApplication,
  RestructurePreview,
  RestructureReport,
} from "@/hooks/useRestructurePlans";
import RestructureCategories from "./RestructureCategories";

vi.mock("@/hooks/usePageTitle", () => ({ usePageTitle: vi.fn() }));

const previewMutate = vi.fn();
const previewReset = vi.fn();
const runMutate = vi.fn();
const hooks = vi.hoisted(() => ({
  preview: {} as Record<string, unknown>,
  run: {} as Record<string, unknown>,
  applications: {} as Record<string, unknown>,
}));

vi.mock("@/hooks/useRestructurePlans", () => ({
  useRestructurePreview: () => hooks.preview,
  useRestructureRun: () => hooks.run,
  useRestructureApplications: () => hooks.applications,
}));

const report: RestructureReport = {
  taxonomy: { created: 2, renamed: 1, moved: 3, defined: 40, archived: 12, restored: 0, activeBefore: 50, activeAfter: 40 },
  items: { considered: 1000, reassigned: 321, cleared: 17, unchanged: 670, conflicts: 2, missingFromDeployment: 0, byRule: { node: 300, d: 21 } },
  transitions: [
    { fromCategoryId: "a", fromPath: "Event / Dairy", toCategoryId: "b", toPath: "Groceries / Dairy", count: 7 },
  ],
  defaults: { kept: 30, moved: 2, removed: 5, set: 1 },
  mappings: { reapproved: 0, repointed: 1, revoked: 0, leftStale: 0 },
  envelopeMerges: [{ source: "Medical (2)", target: "Medical", items: 183, receipts: 0, defaults: 0, itemsChangingYnabCategory: 183 }],
  budget: { itemsChangingEnvelope: 183, itemsChangingYnabCategory: 183 },
  occasions: [{ name: "Easter", created: true, items: 26 }],
};

function application(state: RestructureApplication["state"]): RestructureApplication {
  return {
    id: "app-1", planId: "p", reason: "Item types, not envelopes", appliedAt: "2026-10-30T12:00:00Z", undoneAt: null, state, report,
    progress: { operationId: "app-1", state, total: 323, applied: 321, conflicted: 2, skipped: 0, pending: 0, undone: 0, undoConflicted: 0, undoSkipped: 0 },
  };
}

function setPreview(data?: RestructurePreview, extra: Record<string, unknown> = {}) {
  hooks.preview = { mutate: previewMutate, reset: previewReset, data, isPending: false, isError: false, error: null, ...extra };
}

beforeEach(() => {
  vi.clearAllMocks();
  setPreview();
  hooks.run = { mutate: runMutate, isPending: false, isError: false, error: null };
  hooks.applications = { data: [], isPending: false, isError: false };
});

async function upload(content: string) {
  const user = userEvent.setup();
  renderWithProviders(<RestructureCategories />);
  await user.upload(screen.getByLabelText("Plan file"), new File([content], "plan.json", { type: "application/json" }));
  return user;
}

it("sends a chosen plan for a dry run and rejects a file that is not JSON", async () => {
  await upload('{"planId":"p"}');
  await waitFor(() => expect(previewMutate).toHaveBeenCalledWith({ planId: "p" }));

  await upload("not json");
  expect(await screen.findByText("This file is not valid JSON.")).toBeInTheDocument();
});

it("shows every validation error and offers no apply button for an invalid plan", () => {
  setPreview({ valid: false, errors: ["Every active category needs a definition.", "Categories changed since the plan was generated."], warnings: [], report });
  renderWithProviders(<RestructureCategories />);

  expect(screen.getByText("This plan cannot be applied")).toBeInTheDocument();
  expect(screen.getByText("Every active category needs a definition.")).toBeInTheDocument();
  expect(screen.getByText("Categories changed since the plan was generated.")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Apply plan" })).not.toBeInTheDocument();
});

it("shows the dry run report and applies the plan after confirmation", async () => {
  setPreview({ valid: true, errors: [], warnings: ["2 purchases were classified after the plan's snapshot and are left as they are."], report });
  const user = await upload('{"planId":"p"}');

  expect(screen.getByText("Check before applying")).toBeInTheDocument();
  expect(screen.getByText("Groceries / Dairy")).toBeInTheDocument();
  expect(screen.getByText(/Medical \(2\) → Medical: 183 purchases/)).toBeInTheDocument();
  expect(screen.getByText(/Envelope merges move 183 purchases/)).toBeInTheDocument();

  await user.click(screen.getByRole("button", { name: "Apply plan" }));
  const dialog = await screen.findByRole("alertdialog");
  expect(within(dialog).getByText(/321 purchases are reassigned/)).toBeInTheDocument();
  await user.click(within(dialog).getByRole("button", { name: "Apply" }));

  expect(runMutate).toHaveBeenCalledWith(expect.objectContaining({ kind: "apply" }), expect.anything());
});

it("lists applied plans with undo, and resume for an interrupted one", async () => {
  const user = userEvent.setup();
  hooks.applications = { data: [application("completed"), { ...application("applying"), id: "app-2" }], isPending: false, isError: false };
  renderWithProviders(<RestructureCategories />);

  expect(screen.getAllByText("Item types, not envelopes")).toHaveLength(2);
  await user.click(screen.getAllByRole("button", { name: "Undo" })[0]);
  expect(runMutate).toHaveBeenCalledWith(expect.objectContaining({ kind: "undo", id: "app-1" }));
  await user.click(screen.getByRole("button", { name: "Resume" }));
  expect(runMutate).toHaveBeenCalledWith(expect.objectContaining({ kind: "resume", id: "app-2" }));
});

it("reports a failed run and keeps completed work resumable", () => {
  hooks.run = { mutate: runMutate, isPending: false, isError: true, error: { type: "about:blank", title: "Conflict", status: 409, detail: "Categories changed." } };
  renderWithProviders(<RestructureCategories />);

  expect(screen.getByText(/Categories changed\. Completed work is kept/)).toBeInTheDocument();
});

it("shows an empty activity list", () => {
  renderWithProviders(<RestructureCategories />);

  expect(screen.getByText("No plans applied yet.")).toBeInTheDocument();
});
