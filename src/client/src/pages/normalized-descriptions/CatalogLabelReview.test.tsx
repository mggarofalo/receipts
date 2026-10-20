import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import client from "@/lib/api-client";
import { createQueryWrapper } from "@/test/test-utils";
import { CatalogLabelReview } from "./CatalogLabelReview";

vi.mock("@/lib/api-client", () => ({ default: { POST: vi.fn() } }));
const api = vi.mocked(client);
const targetId = "10000000-0000-0000-0000-000000000001";
const targetRevision = "20000000-0000-0000-0000-000000000001";
const review = {
  targetId,
  targetRevision,
  canonicalName: "BF FRANKS",
  currentDisplayLabel: null,
  proposedDisplayLabel: "Beef franks",
  affectedItemCount: 12,
};
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});
beforeEach(() => vi.clearAllMocks());

it("requires a separate global preview and invalidates approval when the draft changes", async () => {
  api.POST.mockResolvedValue(ok(review) as never);
  const user = userEvent.setup();
  render(
    <CatalogLabelReview targetId={targetId} suggestedLabel="Beef franks" />,
    {
      wrapper: createQueryWrapper(),
    },
  );
  expect(
    screen.getByText(/across all stores and purchases/),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Approve global display name" }),
  ).not.toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Preview global name change" }),
  );
  await screen.findByText(/12 current purchases/);
  expect(api.POST).toHaveBeenCalledTimes(1);
  expect(api.POST).toHaveBeenLastCalledWith(
    "/api/normalization-catalog/targets/{id}/preview-label",
    {
      params: { path: { id: targetId } },
      body: { displayLabel: "Beef franks" },
    },
  );
  await user.type(
    screen.getByRole("textbox", { name: "Display name" }),
    ", 8 count",
  );
  expect(
    screen.queryByRole("button", { name: "Approve global display name" }),
  ).not.toBeInTheDocument();
  expect(api.POST).toHaveBeenCalledTimes(1);
});

it("submits the reviewed revision and impact, shows stale rejection, and permits a fresh preview", async () => {
  api.POST.mockResolvedValueOnce(ok(review) as never)
    .mockResolvedValueOnce({
      data: undefined,
      error: {
        status: 409,
        title: "Conflict",
        detail: "Purchase count changed.",
      },
      response: new Response(null, { status: 409 }),
    } as never)
    .mockResolvedValueOnce(ok({ ...review, affectedItemCount: 13 }) as never)
    .mockResolvedValueOnce(ok({ ...review, affectedItemCount: 13 }) as never);
  const user = userEvent.setup();
  render(
    <CatalogLabelReview targetId={targetId} suggestedLabel="Beef franks" />,
    {
      wrapper: createQueryWrapper(),
    },
  );
  await user.click(
    screen.getByRole("button", { name: "Preview global name change" }),
  );
  await user.click(
    await screen.findByRole("button", { name: "Approve global display name" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Purchase count changed.",
  );
  expect(api.POST).toHaveBeenNthCalledWith(
    2,
    "/api/normalization-catalog/targets/{id}/approve-label",
    {
      params: { path: { id: targetId } },
      body: {
        displayLabel: "Beef franks",
        expectedTargetRevision: targetRevision,
        expectedAffectedItemCount: 12,
      },
    },
  );
  expect(screen.queryByText(/Display name updated/)).not.toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Preview global name change" }),
  );
  await screen.findByText(/13 current purchases/);
  await user.click(
    screen.getByRole("button", { name: "Approve global display name" }),
  );
  await waitFor(() =>
    expect(screen.getByRole("status")).toHaveTextContent(
      "Purchase assignments are unchanged",
    ),
  );
});

it("closing and reopening discards an earlier global approval preview", async () => {
  api.POST.mockResolvedValue(ok(review) as never);
  const user = userEvent.setup();
  const first = render(
    <CatalogLabelReview targetId={targetId} suggestedLabel="Beef franks" />,
    {
      wrapper: createQueryWrapper(),
    },
  );
  await user.click(
    screen.getByRole("button", { name: "Preview global name change" }),
  );
  await screen.findByRole("button", { name: "Approve global display name" });
  first.unmount();
  render(
    <CatalogLabelReview targetId={targetId} suggestedLabel="Franks, beef" />,
    {
      wrapper: createQueryWrapper(),
    },
  );
  expect(screen.getByRole("textbox", { name: "Display name" })).toHaveValue(
    "Franks, beef",
  );
  expect(
    screen.queryByRole("button", { name: "Approve global display name" }),
  ).not.toBeInTheDocument();
});
