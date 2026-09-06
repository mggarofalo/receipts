import { useState } from "react";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { clearTokens, setTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { showError, showSuccess } from "@/lib/toast";
import { useDashboardEarliestReceiptYear } from "@/hooks/useDashboard";
import { renderWithProviders } from "@/test/test-utils";
import { server } from "@/test/msw/server";
import BackupRestore from "./BackupRestore";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://backup-transfer.test"));
vi.mock("@/lib/toast", () => ({ showSuccess: vi.fn(), showError: vi.fn() }));
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const clients: ReturnType<typeof createAppQueryClient>[] = [];
const release: Array<() => void> = [];
let refreshes: number;
let authorizations: Array<string | null>;
let imported: boolean;
let timeoutSpy: ReturnType<typeof vi.spyOn>;
const createObjectURL = URL.createObjectURL;
const revokeObjectURL = URL.revokeObjectURL;
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  vi.clearAllMocks();
  timeoutSpy = vi.spyOn(AbortSignal, "timeout");
  setTokens("current-access", "valid-refresh");
  refreshes = 0;
  imported = false;
  authorizations = [];
  URL.createObjectURL = vi.fn(() => "blob:backup");
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
  server.use(
    http.post("*/api/auth/refresh", () => {
      refreshes++;
      return HttpResponse.json({
        accessToken: "renewed-access",
        refreshToken: "renewed-refresh",
      });
    }),
    http.post("*/api/backup/:operation", ({ request, params }) => {
      authorizations.push(request.headers.get("Authorization"));
      if (request.headers.get("Authorization") === "Bearer expired-access")
        return HttpResponse.json({ status: 401 }, { status: 401 });
      if (params.operation === "export")
        return new HttpResponse(new Uint8Array([0, 83, 81, 76, 255]), {
          headers: {
            "Content-Type": "application/octet-stream",
            "Content-Disposition": 'attachment; filename="portable.sqlite"',
          },
        });
      imported = true;
      return HttpResponse.json({ totalCreated: 0, totalUpdated: 0 });
    }),
  );
});
afterEach(() => {
  release.splice(0).forEach((done) => done());
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  clearTokens();
  server.resetHandlers();
  vi.restoreAllMocks();
  URL.createObjectURL = createObjectURL;
  URL.revokeObjectURL = revokeObjectURL;
});
function Year() {
  const query = useDashboardEarliestReceiptYear();
  return (
    <output aria-label="Earliest receipt year">
      {query.data?.year ?? "Loading"}
    </output>
  );
}
function renderPage(withYear = false) {
  const client = createAppQueryClient();
  clients.push(client);
  renderWithProviders(
    <QueryClientProvider client={client}>
      {withYear && <Year />}
      <BackupRestore />
    </QueryClientProvider>,
  );
  return client;
}
async function startImport() {
  const user = userEvent.setup();
  await user.upload(
    screen.getByLabelText("Select backup file"),
    new File(["portable SQLite bytes"], "portable.sqlite"),
  );
  await user.click(screen.getByRole("button", { name: "Import Backup" }));
  await user.click(screen.getByRole("button", { name: "Confirm Import" }));
}
it.each(["export", "import"] as const)(
  "refreshes expired authentication before completing an actual %s action",
  async (operation) => {
    setTokens("expired-access", "valid-refresh");
    renderPage();
    if (operation === "import") await startImport();
    else
      await userEvent
        .setup()
        .click(screen.getByRole("button", { name: "Export Backup" }));
    await waitFor(() => expect(showSuccess).toHaveBeenCalledOnce());
    expect(refreshes).toBe(1);
    expect(timeoutSpy).toHaveBeenCalledWith(300_000);
    expect(authorizations).toEqual([
      "Bearer expired-access",
      "Bearer renewed-access",
    ]);
    expect(showError).not.toHaveBeenCalled();
    if (operation === "import")
      expect(
        (screen.getByLabelText("Select backup file") as HTMLInputElement).value,
      ).toBe("");
    else expect(URL.createObjectURL).toHaveBeenCalledOnce();
  },
);
it("repairs a pending first Infinity-fresh year read after a successful zero-counter restore", async () => {
  const firstRead = deferred();
  const firstStarted = deferred();
  release.push(firstRead.resolve);
  let reads = 0;
  server.use(
    http.get("*/api/dashboard/earliest-receipt-year", async () => {
      const capturedYear = imported ? 2020 : 2026;
      if (++reads === 1) {
        firstStarted.resolve();
        await firstRead.promise;
      }
      return HttpResponse.json({ year: capturedYear });
    }),
  );
  renderPage(true);
  await firstStarted.promise;
  await startImport();
  await waitFor(() =>
    expect(showSuccess).toHaveBeenCalledWith(
      "Import complete: 0 created, 0 updated.",
    ),
  );
  await act(async () => firstRead.resolve());
  await waitFor(() =>
    expect(screen.getByLabelText("Earliest receipt year")).toHaveTextContent(
      "2020",
    ),
  );
  expect(reads).toBe(2);
});

it("repairs an unfinished year read after navigating away and restoring before returning", async () => {
  const firstRead = deferred();
  const firstStarted = deferred();
  release.push(firstRead.resolve);
  let reads = 0;
  server.use(
    http.get("*/api/dashboard/earliest-receipt-year", async () => {
      const capturedYear = imported ? 2020 : 2026;
      if (++reads === 1) {
        firstStarted.resolve();
        await firstRead.promise;
      }
      return HttpResponse.json({ year: capturedYear });
    }),
  );
  function Navigation() {
    const [dashboard, setDashboard] = useState(true);
    return (
      <>
        <button onClick={() => setDashboard(!dashboard)}>Switch page</button>
        {dashboard ? <Year /> : <BackupRestore />}
      </>
    );
  }
  const client = createAppQueryClient();
  clients.push(client);
  renderWithProviders(
    <QueryClientProvider client={client}>
      <Navigation />
    </QueryClientProvider>,
  );
  const user = userEvent.setup();
  await firstStarted.promise;
  await user.click(screen.getByRole("button", { name: "Switch page" }));
  await startImport();
  await waitFor(() => expect(showSuccess).toHaveBeenCalledOnce());
  await act(async () => firstRead.resolve());
  await user.click(screen.getByRole("button", { name: "Switch page" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Earliest receipt year")).toHaveTextContent(
      "2020",
    ),
  );
  expect(reads).toBe(2);
});

it("retains the selected file after a local503 and resets it only after a deliberate successful retry", async () => {
  let attempts = 0;
  server.use(
    http.post("*/api/backup/import", () =>
      ++attempts === 1
        ? HttpResponse.json({ status: 503 }, { status: 503 })
        : HttpResponse.json({ totalCreated: 1, totalUpdated: 2 }),
    ),
  );
  renderPage();
  await startImport();
  await waitFor(() =>
    expect(showError).toHaveBeenCalledWith("Import failed (503)."),
  );
  const input = screen.getByLabelText("Select backup file") as HTMLInputElement;
  expect(input.files?.[0]?.name).toBe("portable.sqlite");
  expect(showError).toHaveBeenCalledOnce();
  expect(showSuccess).not.toHaveBeenCalled();
  expect(
    screen.queryByRole("button", { name: "Confirm Import" }),
  ).not.toBeInTheDocument();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Import Backup" }));
  await user.click(screen.getByRole("button", { name: "Confirm Import" }));
  await waitFor(() =>
    expect(showSuccess).toHaveBeenCalledWith(
      "Import complete: 1 created, 2 updated.",
    ),
  );
  expect(input.value).toBe("");
  expect(attempts).toBe(2);
});
