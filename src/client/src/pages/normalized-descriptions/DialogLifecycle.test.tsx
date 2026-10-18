import { act, screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import { ReviewQueueTab } from "./ReviewQueueTab";
import client from "@/lib/api-client";

vi.mock("@/lib/api-client", () => ({
  default: { POST: vi.fn(), GET: vi.fn() },
}));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock("@/hooks/useNormalizedDescriptions", () => ({
  NORMALIZED_DESCRIPTION_PAGE_SIZE: 50,
  useNormalizedDescriptions: (options: { status?: string | string[] }) =>
    mockQueryResult({
      items: Array.isArray(options.status)
        ? [
            {
              id: "target",
              displayName: "Target",
              canonicalName: "Target",
              status: "active",
              linkedItemCount: 0,
            },
          ]
        : [
            {
              id: "source",
              displayName: "Source",
              canonicalName: "Source",
              status: "pendingReview",
              linkedItemCount: 1,
              sampleRawDescriptions: [],
            },
          ],
      total: 1,
      isSuccess: true,
      isPending: false,
      isLoading: false,
      status: "success",
    }),
}));

it("a real late merge completion cannot close a reopened dialog session", async () => {
  let finish!: (value: Awaited<ReturnType<typeof client.POST>>) => void;
  vi.mocked(client.POST).mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  const user = userEvent.setup();
  renderWithQueryClient(<ReviewQueueTab />);
  await user.click(screen.getByRole("button", { name: "Merge into…" }));
  await user.click(within(screen.getByRole("dialog")).getByRole("radio"));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Merge" }),
  );
  await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }),
  );
  await user.click(screen.getByRole("button", { name: "Merge into…" }));
  await user.type(
    within(screen.getByRole("dialog")).getByLabelText("Search active entries"),
    "new draft",
  );
  await act(async () => {
    finish({
      data: { itemsRelinkedCount: 1 },
      response: new Response(null, { status: 200 }),
    } as Awaited<ReturnType<typeof client.POST>>);
  });
  expect(screen.getByRole("dialog")).toBeInTheDocument();
  expect(
    within(screen.getByRole("dialog")).getByLabelText("Search active entries"),
  ).toHaveValue("new draft");
});
