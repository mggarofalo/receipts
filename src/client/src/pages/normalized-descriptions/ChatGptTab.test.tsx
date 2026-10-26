import { useEffect } from "react";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useLocation } from "react-router";
import client from "@/lib/api-client";
import { createQueryWrapper } from "@/test/test-utils";
import { ChatGptTab } from "./ChatGptTab";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
}));
type ApiResult = { data: unknown; error: unknown; response: Response };
type ApiOptions = { body?: unknown; params?: unknown; signal?: AbortSignal };
const api = client as unknown as {
  GET: ReturnType<
    typeof vi.fn<(path: string, options?: ApiOptions) => Promise<ApiResult>>
  >;
  POST: ReturnType<
    typeof vi.fn<(path: string, options?: ApiOptions) => Promise<ApiResult>>
  >;
};
const runId = "10000000-0000-0000-0000-000000000001";
const itemId = "20000000-0000-0000-0000-000000000001";
const proposalId = "30000000-0000-0000-0000-000000000001";
const revision = "40000000-0000-0000-0000-000000000001";
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});
const failure = (status: number, detail: string) => ({
  data: undefined,
  error: { status, detail },
  response: new Response(null, { status }),
});
let cache: QueryClient;
let connection: {
  configured: boolean;
  isOwner: boolean;
  credentialsPresent: boolean;
  busy: boolean;
  generation: string;
  models: string[];
};
let currentRun: Omit<
  ReturnType<typeof readyRun>,
  "errorCode" | "errorDetail"
> & { errorCode: string | null; errorDetail: string | null };
function readyRun() {
  return {
    id: runId,
    revision,
    clientRequestId: runId,
    state: "ready",
    model: "test-model",
    createdAt: "2026-01-01T00:00:00Z",
    completedAt: null,
    errorCode: null,
    errorDetail: null,
    usage: null,
    proposals: [
      {
        id: proposalId,
        receiptItemId: itemId,
        description: "RAW MLK",
        action: "create",
        targetId: null,
        canonicalName: "Whole milk",
        displayLabel: null,
        reason: "Explicit product description",
        state: "pending",
      },
    ],
  };
}
function Probe() {
  const queryClient = useQueryClient();
  useEffect(() => {
    cache = queryClient;
  }, [queryClient]);
  const location = useLocation();
  return (
    <output aria-label="Current URL">
      {location.pathname + location.search}
    </output>
  );
}
function mount(
  route = "/normalized-descriptions?tab=chatgpt",
  roles = ["Admin"],
) {
  return render(
    <>
      <ChatGptTab />
      <Probe />
    </>,
    {
      wrapper: createQueryWrapper({
        route,
        auth: {
          user: {
            userId: "owner",
            email: "owner@example.test",
            roles,
            mustResetPassword: false,
          },
        },
      }),
    },
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  connection = {
    configured: true,
    isOwner: true,
    credentialsPresent: true,
    busy: false,
    generation: revision,
    models: ["test-model"],
  };
  currentRun = readyRun();
  api.GET.mockImplementation(async (path) => {
    if (path.endsWith("/connection")) return ok(connection) as never;
    if (path.endsWith("/candidates"))
      return ok({
        items: [
          {
            id: itemId,
            receiptId: runId,
            description: "RAW MLK",
            location: "Synthetic store",
            date: "2026-01-01",
          },
        ],
        total: 1,
      }) as never;
    if (path.endsWith("/runs/{id}")) return ok(currentRun) as never;
    if (path.endsWith("/runs")) return ok([currentRun]) as never;
    throw new Error("Unexpected GET " + path);
  });
});

it.each(["not-owner", "not-admin", "not-configured"])(
  "hides device sign-in from %s",
  async (gate) => {
    if (gate === "not-owner") connection.isOwner = false;
    if (gate === "not-configured") connection.configured = false;
    mount(undefined, gate === "not-admin" ? ["User"] : ["Admin"]);
    await screen.findByRole("heading", { name: "ChatGPT normalization" });
    expect(
      screen.queryByRole("button", {
        name: /^(Sign in with|Reconnect) ChatGPT$/,
      }),
    ).not.toBeInTheDocument();
  },
);

it("exposes reconnect to configured owner administrator while preserving CLI help", async () => {
  mount();
  expect(
    await screen.findByRole("button", { name: "Reconnect ChatGPT" }),
  ).toBeDisabled();
  expect(screen.getByText(/docker compose run/)).toBeInTheDocument();
});

it("shows loading then a retryable connection failure", async () => {
  let resolve!: (value: ApiResult) => void;
  api.GET.mockReturnValue(
    new Promise((done) => {
      resolve = done;
    }),
  );
  mount();
  expect(screen.getByText(/Checking ChatGPT connection/)).toHaveAttribute(
    "role",
    "status",
  );
  await act(async () => resolve(failure(503, "offline") as never));
  expect(
    await screen.findByText("ChatGPT status could not be loaded."),
  ).toBeVisible();
  expect(screen.getByRole("button", { name: /retry/i })).toBeEnabled();
});

it.each(["not-configured", "different-owner", "disconnected"])(
  "does not expose inference selection when %s",
  async (state) => {
    if (state === "not-configured") connection.configured = false;
    if (state === "different-owner") connection.isOwner = false;
    if (state === "disconnected") connection.credentialsPresent = false;
    mount();
    await screen.findByRole("heading", { name: "ChatGPT normalization" });
    expect(
      screen.queryByRole("region", { name: "Select items for ChatGPT" }),
    ).not.toBeInTheDocument();
    expect(
      api.GET.mock.calls.some(([path]) => path.endsWith("/candidates")),
    ).toBe(false);
    if (state === "different-owner")
      expect(api.GET.mock.calls.some(([path]) => path.endsWith("/runs"))).toBe(
        false,
      );
  },
);

it("retains selected items after failed start and reuses intent ID for deliberate retry", async () => {
  const user = userEvent.setup();
  api.POST.mockResolvedValue(
    failure(503, "Model currently unavailable.") as never,
  );
  mount();
  await user.click(
    await screen.findByRole("checkbox", { name: "Select RAW MLK" }),
  );
  await user.selectOptions(
    screen.getByRole("combobox", { name: "ChatGPT model" }),
    "test-model",
  );
  await user.click(
    screen.getByRole("button", { name: "Request suggestions for 1 items" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Model currently unavailable.",
  );
  expect(
    screen.getByRole("checkbox", { name: "Select RAW MLK" }),
  ).toBeChecked();
  const first = api.POST.mock.calls[0][1]?.body;
  api.POST.mockResolvedValue(ok(currentRun) as never);
  await user.selectOptions(
    screen.getByRole("combobox", { name: "ChatGPT model" }),
    "test-model",
  );
  await user.click(
    screen.getByRole("button", { name: "Request suggestions for 1 items" }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      `aiRun=${runId}`,
    ),
  );
  expect(api.POST.mock.calls[1][1]?.body).toEqual(first);
  expect(
    screen.queryByRole("region", { name: "Select items for ChatGPT" }),
  ).not.toBeInTheDocument();
});

it("clears selected candidates when applying a different item search", async () => {
  const user = userEvent.setup();
  mount();
  await user.click(
    await screen.findByRole("checkbox", { name: "Select RAW MLK" }),
  );
  await user.type(
    screen.getByRole("textbox", { name: "Find items for normalization" }),
    "bread",
  );
  await user.click(screen.getByRole("button", { name: "Search items" }));
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Request suggestions for 0 items" }),
    ).toBeDisabled(),
  );
  expect(api.POST).not.toHaveBeenCalled();
});

it("resumes from URL, requires review confirmation, and preserves choices after a stale rejection", async () => {
  const user = userEvent.setup();
  api.POST.mockResolvedValue(
    failure(409, "A source item changed. Nothing was applied.") as never,
  );
  mount(`/normalized-descriptions?tab=chatgpt&aiRun=${runId}`);
  const review = await screen.findByRole("region", {
    name: "Review ChatGPT suggestions",
  });
  const apply = within(review).getByRole("button", {
    name: "Apply reviewed suggestions",
  });
  expect(apply).toBeDisabled();
  await user.click(
    within(review).getByRole("checkbox", {
      name: "Accept suggestion for RAW MLK",
    }),
  );
  expect(apply).toBeDisabled();
  await user.click(
    within(review).getByRole("checkbox", {
      name: "I reviewed these 1 suggestions",
    }),
  );
  await user.click(apply);
  expect(await within(review).findByRole("alert")).toHaveTextContent(
    "A source item changed. Nothing was applied.",
  );
  expect(
    within(review).getByRole("checkbox", {
      name: "Accept suggestion for RAW MLK",
    }),
  ).toBeChecked();
  expect(api.POST).toHaveBeenCalledWith(
    "/api/normalization-ai/runs/{id}/apply",
    {
      params: { path: { id: runId } },
      body: { proposalIds: [proposalId], expectedRevision: revision },
    },
  );
});

it("resets acceptance and confirmation after a server revision changes", async () => {
  const user = userEvent.setup();
  mount(`/normalized-descriptions?aiRun=${runId}`);
  await user.click(
    await screen.findByRole("checkbox", {
      name: "Accept suggestion for RAW MLK",
    }),
  );
  await user.click(
    screen.getByRole("checkbox", { name: "I reviewed these 1 suggestions" }),
  );
  currentRun = {
    ...currentRun,
    revision: "40000000-0000-0000-0000-000000000002",
  };
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: ["normalization-ai", "run", runId],
    });
  });
  await waitFor(() =>
    expect(
      screen.getByRole("checkbox", { name: "Accept suggestion for RAW MLK" }),
    ).not.toBeChecked(),
  );
  expect(
    screen.getByRole("button", { name: "Apply reviewed suggestions" }),
  ).toBeDisabled();
});

it("allows discarding a ready run without applying proposals", async () => {
  const user = userEvent.setup();
  api.POST.mockResolvedValue(
    ok({ ...currentRun, state: "cancelled" }) as never,
  );
  mount(`/normalized-descriptions?aiRun=${runId}`);
  await user.click(
    await screen.findByRole("button", { name: "Discard suggestions" }),
  );
  await waitFor(() =>
    expect(api.POST).toHaveBeenCalledWith(
      "/api/normalization-ai/runs/{id}/cancel",
      { params: { path: { id: runId } } },
    ),
  );
  expect(api.POST.mock.calls.some(([path]) => path.endsWith("/apply"))).toBe(
    false,
  );
});

it.each(["running", "unknown", "failed"])(
  "can cancel a %s run without retrying inference",
  async (state) => {
    currentRun = { ...currentRun, state, proposals: [] };
    api.POST.mockResolvedValue(ok({ ...currentRun, state: "cancelled" }));
    const user = userEvent.setup();
    mount(`/normalized-descriptions?aiRun=${runId}`);
    await user.click(
      await screen.findByRole("button", { name: "Cancel request" }),
    );
    await waitFor(() =>
      expect(api.POST).toHaveBeenCalledWith(
        "/api/normalization-ai/runs/{id}/cancel",
        { params: { path: { id: runId } } },
      ),
    );
    expect(api.POST).toHaveBeenCalledTimes(1);
  },
);

it("keeps existing runs accessible while the provider is busy", async () => {
  connection = {
    ...connection,
    busy: true,
    credentialsPresent: false,
    models: [],
  };
  const user = userEvent.setup();
  mount();
  expect(await screen.findByText(/ChatGPT is busy/)).toBeVisible();
  expect(
    screen.getByRole("button", { name: "Request suggestions for 0 items" }),
  ).toBeDisabled();
  const history = await screen.findByRole("region", {
    name: "Recent ChatGPT runs",
  });
  await user.click(await within(history).findByRole("button"));
  expect(
    await screen.findByRole("region", { name: "Review ChatGPT suggestions" }),
  ).toBeVisible();
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      `aiRun=${runId}`,
    ),
  );
});

it("navigates to the new run even when start invalidation discovers a busy connection", async () => {
  const user = userEvent.setup();
  api.POST.mockImplementation(async () => {
    connection = {
      ...connection,
      busy: true,
      credentialsPresent: false,
      models: [],
    };
    currentRun = { ...currentRun, state: "running", proposals: [] };
    return ok(currentRun);
  });
  mount();
  await user.click(
    await screen.findByRole("checkbox", { name: "Select RAW MLK" }),
  );
  await user.selectOptions(
    screen.getByRole("combobox", { name: "ChatGPT model" }),
    "test-model",
  );
  await user.click(
    screen.getByRole("button", { name: "Request suggestions for 1 items" }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      `aiRun=${runId}`,
    ),
  );
  expect(await screen.findByText(/ChatGPT is busy/)).toBeVisible();
  expect(
    await screen.findByRole("button", { name: "Cancel request" }),
  ).toBeEnabled();
});

it("navigates to the retry run before invalidation remounts the cancelled original", async () => {
  const nextId = "10000000-0000-0000-0000-000000000002";
  currentRun = { ...currentRun, state: "unknown", proposals: [] };
  const originalGet = api.GET.getMockImplementation()!;
  api.GET.mockImplementation(async (path, options) => {
    if (
      path.endsWith("/runs/{id}") &&
      (options?.params as { path?: { id?: string } })?.path?.id === nextId
    )
      return ok({ ...currentRun, id: nextId, state: "queued", proposals: [] });
    return originalGet(path, options);
  });
  api.POST.mockImplementation(async () => {
    currentRun = {
      ...currentRun,
      revision: "40000000-0000-0000-0000-000000000003",
      state: "cancelled",
    };
    return ok({ ...currentRun, id: nextId, state: "queued" });
  });
  const user = userEvent.setup();
  mount(`/normalized-descriptions?aiRun=${runId}`);
  await user.click(
    await screen.findByRole("button", { name: "Retry as a new request" }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      `aiRun=${nextId}`,
    ),
  );
  const review = await screen.findByRole("region", {
    name: "Review ChatGPT suggestions",
  });
  await waitFor(() =>
    expect(within(review).getByRole("status")).toHaveTextContent("Queued"),
  );
  expect(api.POST).toHaveBeenCalledTimes(1);
});

it("keeps the chosen model and item draft consistent through a busy refresh", async () => {
  const user = userEvent.setup();
  mount();
  await user.click(
    await screen.findByRole("checkbox", { name: "Select RAW MLK" }),
  );
  const model = screen.getByRole("combobox", { name: "ChatGPT model" });
  await user.selectOptions(model, "test-model");
  connection = {
    ...connection,
    busy: true,
    credentialsPresent: false,
    models: [],
  };
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: ["normalization-ai", "connection"],
    });
  });
  await screen.findByText(/ChatGPT is busy/);
  expect(screen.getByRole("combobox", { name: "ChatGPT model" })).toHaveValue(
    "test-model",
  );
  expect(
    screen.getByRole("combobox", { name: "ChatGPT model" }),
  ).toBeDisabled();
  expect(
    screen.getByRole("checkbox", { name: "Select RAW MLK" }),
  ).toBeChecked();
  connection = {
    ...connection,
    busy: false,
    credentialsPresent: true,
    models: ["test-model"],
  };
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: ["normalization-ai", "connection"],
    });
  });
  await screen.findByText(/Credentials are present/);
  expect(screen.getByRole("combobox", { name: "ChatGPT model" })).toHaveValue(
    "test-model",
  );
  api.POST.mockResolvedValue(ok(currentRun));
  await user.click(
    screen.getByRole("button", { name: "Request suggestions for 1 items" }),
  );
  await waitFor(() =>
    expect(api.POST).toHaveBeenCalledWith(
      "/api/normalization-ai/runs",
      expect.objectContaining({
        body: expect.objectContaining({
          model: "test-model",
          itemIds: [itemId],
        }),
      }),
    ),
  );
});

it.each([
  ["queued", "runner_busy", "Queued"],
  ["running", null, "Preparing suggestions"],
  ["unknown", "unknown_outcome", "Outcome uncertain"],
  ["failed", "runner_busy_timeout", "Failed before request"],
  ["failed", "runner_setup_failed", "Failed before request"],
  ["failed", "auth_required", "Reconnect"],
])(
  "keeps %s placeholder rows out of the review and new selection",
  async (state, errorCode, label) => {
    currentRun = { ...currentRun, state: state!, errorCode };
    connection.busy = true;
    mount(`/normalized-descriptions?tab=chatgpt&aiRun=${runId}`);
    const review = await screen.findByRole("region", {
      name: "Review ChatGPT suggestions",
    });
    expect(within(review).getByRole("status")).toHaveTextContent(label!);
    expect(
      within(review).getByRole("heading", { name: "Suggestion request" }),
    ).toBeVisible();
    expect(
      within(review).queryByRole("heading", { name: "Review suggestions" }),
    ).not.toBeInTheDocument();
    expect(within(review).queryByRole("table")).not.toBeInTheDocument();
    expect(within(review).queryByRole("checkbox")).not.toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Select items for ChatGPT" }),
    ).not.toBeInTheDocument();
    expect(api.POST).not.toHaveBeenCalled();
    if (errorCode?.startsWith("runner_")) {
      expect(
        within(review).queryByText(/may have consumed|allowance again/),
      ).not.toBeInTheDocument();
    }
    if (state === "unknown")
      expect(within(review).getAllByText(/allowance again/)).toHaveLength(1);
    if (errorCode === "auth_required")
      expect(
        within(review).getByRole("link", { name: /connection settings/ }),
      ).toHaveAttribute(
        "href",
        "/admin/normalized-descriptions?tab=chatgpt#chatgpt-connection-settings",
      );
  },
);

it("closes the active run to select another request, with controls before items", async () => {
  const user = userEvent.setup();
  mount(`/normalized-descriptions?tab=chatgpt&aiRun=${runId}`);
  await user.click(await screen.findByRole("button", { name: "Close run" }));
  const selection = await screen.findByRole("region", {
    name: "Select items for ChatGPT",
  });
  const model = within(selection).getByRole("combobox", {
    name: "ChatGPT model",
  });
  const item = await within(selection).findByRole("checkbox", {
    name: "Select RAW MLK",
  });
  expect(
    model.compareDocumentPosition(item) & Node.DOCUMENT_POSITION_FOLLOWING,
  ).toBeTruthy();
  expect(
    screen.queryByRole("region", { name: "Review ChatGPT suggestions" }),
  ).not.toBeInTheDocument();
  expect(screen.getByLabelText("Current URL")).toHaveTextContent("tab=chatgpt");
});

it("keeps connected setup commands behind a disclosure without asserting model access", async () => {
  mount();
  const summary = await screen.findByText(
    "Connection settings and reconnect help",
  );
  expect(summary.closest("details")).not.toHaveAttribute("open");
  expect(screen.getByText(/Credentials are present/)).toHaveTextContent(
    "Model access is confirmed when a request succeeds",
  );
});

it("shows applied outcomes without acceptance controls", async () => {
  currentRun = {
    ...currentRun,
    state: "applied",
    proposals: currentRun.proposals.map((p) => ({ ...p, state: "applied" })),
  };
  mount(`/normalized-descriptions?aiRun=${runId}`);
  const review = await screen.findByRole("region", {
    name: "Review ChatGPT suggestions",
  });
  expect(within(review).getByText("Whole milk")).toBeVisible();
  expect(within(review).queryByRole("checkbox")).not.toBeInTheDocument();
  expect(
    within(review).getByText(/Accepted suggestions were applied/),
  ).toBeVisible();
});
