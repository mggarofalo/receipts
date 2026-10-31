import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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

it.each(["connected", "not-configured", "different-owner", "disconnected"])(
  "never offers per-purchase item selection when %s",
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

it("sends owners to Catalog to request suggestions", async () => {
  const user = userEvent.setup();
  mount("/normalized-descriptions?tab=chatgpt");
  await user.click(
    await screen.findByRole("button", {
      name: "Request suggestions in Catalog",
    }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Current URL")).toHaveTextContent(
      "tab=catalog",
    ),
  );
  expect(api.POST).not.toHaveBeenCalled();
});

it("shows a ready per-purchase run read-only with only a discard action", async () => {
  mount(`/normalized-descriptions?tab=chatgpt&aiRun=${runId}`);
  const review = await screen.findByRole("region", {
    name: "Review ChatGPT suggestions",
  });
  expect(within(review).getByText("Whole milk")).toBeVisible();
  expect(within(review).getByText(/can no longer be applied/)).toBeVisible();
  expect(within(review).queryByRole("checkbox")).not.toBeInTheDocument();
  expect(
    within(review).queryByRole("button", { name: /apply/i }),
  ).not.toBeInTheDocument();
  expect(
    within(review).getByRole("button", { name: "Discard suggestions" }),
  ).toBeEnabled();
  expect(
    screen.queryByRole("button", { name: "Request suggestions in Catalog" }),
  ).not.toBeInTheDocument();
});

it.each(["failed", "unknown"])(
  "offers no retry for a %s per-purchase run",
  async (state) => {
    currentRun = { ...currentRun, state, proposals: [] };
    mount(`/normalized-descriptions?aiRun=${runId}`);
    const review = await screen.findByRole("region", {
      name: "Review ChatGPT suggestions",
    });
    expect(
      within(review).getByRole("button", { name: "Cancel request" }),
    ).toBeVisible();
    expect(
      within(review).queryByRole("button", { name: /retry/i }),
    ).not.toBeInTheDocument();
  },
);

it("closes the active run and returns to run history", async () => {
  const user = userEvent.setup();
  mount(`/normalized-descriptions?tab=chatgpt&aiRun=${runId}`);
  await user.click(await screen.findByRole("button", { name: "Close run" }));
  expect(
    await screen.findByRole("button", {
      name: "Request suggestions in Catalog",
    }),
  ).toBeVisible();
  expect(
    screen.queryByRole("region", { name: "Review ChatGPT suggestions" }),
  ).not.toBeInTheDocument();
  expect(screen.getByLabelText("Current URL")).toHaveTextContent("tab=chatgpt");
  expect(screen.getByLabelText("Current URL")).not.toHaveTextContent("aiRun");
});
