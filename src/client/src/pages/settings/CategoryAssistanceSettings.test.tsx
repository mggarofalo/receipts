import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import CategoryAssistanceSettings from "./CategoryAssistanceSettings";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));

const current = {
  enabled: false,
  provider: null,
  model: null,
  dailyLimit: 10,
  pendingLimit: 3,
  revision: "11111111-1111-4111-8111-111111111111",
};
const ready = {
  capability: "ready",
  consentEnabled: false,
  suggestionsAvailable: false,
  provider: "jev",
  models: ["jev-1.14.0"],
};
const ok = (data: unknown) => ({ data, error: undefined, response: { status: 200 } });

beforeEach(() => {
  resetMockClient();
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(ready);
    if (path.endsWith("/settings")) return ok(current);
    if (path.endsWith("/activity")) return ok({
      todayAttempts: 9, queued: 1, dispatching: 2, suggested: 3,
      abstained: 4, failed: 5, stale: 6, indeterminate: 7,
    });
    throw new Error(path);
  });
  mockClient.PUT.mockResolvedValue(ok({ ...current, enabled: true, provider: "jev", model: "jev-1.14.0", revision: "22222222-2222-4222-8222-222222222222" }));
});

it("keeps settings discoverable without capability and hides provider controls", async () => {
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok({ ...ready, capability: "unavailable", models: [] });
    if (path.endsWith("/settings")) return ok(current);
    throw new Error(path);
  });
  renderWithQueryClient(<CategoryAssistanceSettings />);
  expect(await screen.findByText(/provider is unavailable/i)).toBeInTheDocument();
  expect(screen.getByText("TYPESAFE_API_KEY")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Back to classification" })).toHaveAttribute("href", "/classification");
  expect(screen.queryByLabelText("Model")).not.toBeInTheDocument();
  expect(screen.getByRole("checkbox", { name: /enable automatic/i })).toBeDisabled();
  expect(mockClient.GET).not.toHaveBeenCalledWith("/api/category-assistance/activity", expect.anything());
});

it("requires explicit consent, available model, and bounded limits before saving with revision", async () => {
  const user = userEvent.setup();
  renderWithQueryClient(<CategoryAssistanceSettings />);
  const consent = await screen.findByRole("checkbox", { name: /enable automatic/i });
  await user.click(consent);
  expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();
  await user.selectOptions(screen.getByLabelText("Provider"), "jev");
  await user.selectOptions(screen.getByLabelText("Model"), "jev-1.14.0");
  await user.clear(screen.getByLabelText(/daily request limit/i));
  await user.type(screen.getByLabelText(/daily request limit/i), "101");
  expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();
  await user.clear(screen.getByLabelText(/daily request limit/i));
  await user.type(screen.getByLabelText(/daily request limit/i), "12");
  await user.click(screen.getByRole("button", { name: "Save settings" }));
  await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledWith(
    "/api/category-assistance/settings",
    expect.objectContaining({ body: {
      enabled: true, provider: "jev", model: "jev-1.14.0",
      dailyLimit: 12, pendingLimit: 3, expectedRevision: current.revision,
    } }),
  ));
  expect(await screen.findByText("Assistance settings saved.")).toBeInTheDocument();
});

it("preserves edits on conflict until an explicit revision refresh", async () => {
  const user = userEvent.setup();
  let settingsReads = 0;
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(ready);
    if (path.endsWith("/settings")) return ok({ ...current, revision: settingsReads++ ? "33333333-3333-4333-8333-333333333333" : current.revision });
    throw new Error(path);
  });
  mockClient.PUT.mockImplementationOnce(async () => ({ data: undefined, error: { detail: "Changed" }, response: { status: 409 } }));
  renderWithQueryClient(<CategoryAssistanceSettings />);
  await screen.findByRole("checkbox", { name: /enable automatic/i });
  await user.clear(screen.getByLabelText(/daily request limit/i));
  await user.type(screen.getByLabelText(/daily request limit/i), "15");
  await user.click(screen.getByRole("button", { name: "Save settings" }));
  expect(await screen.findByText(/settings changed elsewhere/i)).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Save settings" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Refresh revision" }));
  expect(screen.getByLabelText(/daily request limit/i)).toHaveValue(15);
  await user.click(screen.getByRole("button", { name: "Save settings" }));
  await waitFor(() => expect(mockClient.PUT).toHaveBeenLastCalledWith(
    "/api/category-assistance/settings",
    expect.objectContaining({ body: expect.objectContaining({ dailyLimit: 15, expectedRevision: "33333333-3333-4333-8333-333333333333" }) }),
  ));
});

it("loads aggregate technical activity only when expanded and distinguishes abstentions from failures", async () => {
  const user = userEvent.setup();
  renderWithQueryClient(<CategoryAssistanceSettings />);
  await screen.findByRole("checkbox", { name: /enable automatic/i });
  expect(mockClient.GET).not.toHaveBeenCalledWith("/api/category-assistance/activity", expect.anything());
  await user.click(screen.getByRole("button", { name: "Show technical activity" }));
  const technical = await screen.findByRole("heading", { name: "Technical activity" });
  const activity = technical.parentElement!;
  expect(within(activity).getByText("Abstained (no suggestion)")).toBeInTheDocument();
  expect(within(activity).getByText("Failed (request error)")).toBeInTheDocument();
  expect(within(activity).getByText("4")).toBeInTheDocument();
  expect(within(activity).getByText("5")).toBeInTheDocument();
});
