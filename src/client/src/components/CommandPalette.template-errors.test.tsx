vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://palette-template-errors.test"),
);
import { useState } from "react";
import {
  act,
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { CommandPalette } from "./CommandPalette";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";
let templateFailure: boolean;
let templateRequests: URLSearchParams[];
let templateTotal: number;
let templates: {
  id: string;
  name: string;
  description: string;
  defaultCategory: string;
}[];
const server = setupServer(
  http.get("*/api/item-templates", ({ request }) => {
    const params = new URL(request.url).searchParams;
    templateRequests.push(params);
    const q = params.get("q");
    const data = q
      ? templates.filter((template) =>
          template.name.toLowerCase().includes(q.toLowerCase()),
        )
      : templates;
    return templateFailure
      ? HttpResponse.json(
          { status: 503, detail: "Template catalog unavailable" },
          { status: 503 },
        )
      : HttpResponse.json({
          data,
          total: templateTotal,
          offset: 0,
          limit: 500,
        });
  }),
  http.get("*/api/:entity", () =>
    HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
  ),
);
let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
function Palette() {
  const [open, setOpen] = useState(true);
  return (
    <>
      <button onClick={() => setOpen(true)}>Reopen palette</button>
      <CommandPalette open={open} onOpenChange={setOpen} />
    </>
  );
}
function renderPalette() {
  queryClient = createAppQueryClient();
  router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        { path: "/", element: <Palette /> },
        { path: "/error/500", element: <h1>Global server error route</h1> },
        { path: "/receipts/new", element: <h1>New receipt destination</h1> },
        {
          path: "/item-templates",
          element: <h1>Template catalog destination</h1>,
        },
      ],
    },
  ]);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  templateFailure = false;
  templateRequests = [];
  templates = [];
  templateTotal = 0;
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "refresh",
  );
});
afterEach(() => {
  cleanup();
  router?.dispose();
  queryClient?.clear();
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});
it("keeps a template failure visible outside cmdk filtering, preserves the query and retries without closing", async () => {
  templateFailure = true;
  renderPalette();
  const user = userEvent.setup();
  await user.type(
    screen.getByPlaceholderText(/type a command or search/i),
    "quux-unmatched",
  );
  const failure = await screen.findByRole("alert");
  expect(failure).toHaveTextContent(/template.*unavailable/i);
  expect(screen.getByPlaceholderText(/type a command or search/i)).toHaveValue(
    "quux-unmatched",
  );
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText(/^No matches\./)).not.toBeInTheDocument();
  expect(
    document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
  ).toHaveLength(0);
  templateFailure = false;
  templates = [
    {
      id: "found",
      name: "quux-unmatched template",
      description: "History",
      defaultCategory: "Food",
    },
  ];
  templateTotal = 1;
  await user.click(within(failure).getByRole("button", { name: "Retry" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(await screen.findByText("quux-unmatched template")).toBeVisible();
  expect(screen.getByPlaceholderText(/type a command or search/i)).toHaveValue(
    "quux-unmatched",
  );
  expect(templateRequests).toHaveLength(2);
});
it("preserves other command navigation when the template catalog is successfully empty", async () => {
  renderPalette();
  const user = userEvent.setup();
  await user.type(
    screen.getByPlaceholderText(/type a command or search/i),
    "new receipt",
  );
  await waitFor(() => expect(templateRequests).toHaveLength(1));
  await user.click(await screen.findByText("New Receipt"));
  expect(
    await screen.findByRole("heading", { name: "New receipt destination" }),
  ).toBeVisible();
});

it("retains cached matching templates with an honest failure notice and usable command navigation", async () => {
  templates = [
    {
      id: "milk",
      name: "Milk template",
      description: "Daily milk",
      defaultCategory: "Food",
    },
  ];
  templateTotal = 1;
  renderPalette();
  const user = userEvent.setup();
  await user.type(
    screen.getByPlaceholderText(/type a command or search/i),
    "milk",
  );
  expect(await screen.findByText("Milk template")).toBeVisible();
  templateFailure = true;
  await act(async () => {
    await queryClient.invalidateQueries({ queryKey: ["itemTemplates"] });
  });
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /template.*unavailable/i,
  );
  expect(screen.getByText("Milk template")).toBeVisible();
  expect(screen.getByPlaceholderText(/type a command or search/i)).toHaveValue(
    "milk",
  );
  const userInput = screen.getByPlaceholderText(/type a command or search/i);
  await user.clear(userInput);
  await user.type(userInput, "new receipt");
  await screen.findByRole("alert");
  await user.click(screen.getByText("New Receipt"));
  expect(
    await screen.findByRole("heading", { name: "New receipt destination" }),
  ).toBeVisible();
});

it("hides obsolete template Retry while input debounces or clears and does not revive it after closing", async () => {
  templateFailure = true;
  renderPalette();
  const user = userEvent.setup();
  const input = screen.getByPlaceholderText(/type a command or search/i);
  await user.type(input, "quux");
  await screen.findByRole("alert");
  const initialReads = templateRequests.length;
  await user.type(input, "-changed");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(templateRequests).toHaveLength(initialReads);
  await screen.findByRole("alert");
  await user.clear(input);
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Retry" }),
  ).not.toBeInTheDocument();
  const readsBeforeClose = templateRequests.length;
  await user.keyboard("{Escape}");
  expect(
    screen.queryByPlaceholderText(/type a command or search/i),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Reopen palette" }));
  expect(screen.getByPlaceholderText(/type a command or search/i)).toHaveValue(
    "",
  );
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(templateRequests).toHaveLength(readsBeforeClose);
});

it.each(["herbal", "grocery", "template"])(
  "preserves the %s template token without sending incompatible name-only server q",
  async (token) => {
    templates = [
      {
        id: "tea",
        name: "Peppermint",
        description: "Herbal infusion",
        defaultCategory: "Grocery",
      },
    ];
    templateTotal = 1;
    renderPalette();
    const user = userEvent.setup();
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      token,
    );
    expect(await screen.findByText("Peppermint")).toBeVisible();
    expect(templateRequests).toHaveLength(1);
    expect(templateRequests[0].has("q")).toBe(false);
    expect(templateRequests[0].get("limit")).toBe("500");
  },
);

it("shows the bounded catalog scope outside filtering even when none of the first 500 templates match", async () => {
  templates = Array.from({ length: 500 }, (_, index) => ({
    id: `tea-${index}`,
    name: `Tea ${index}`,
    description: "Herbal infusion",
    defaultCategory: "Grocery",
  }));
  templateTotal = 501;
  renderPalette();
  const user = userEvent.setup();
  await user.type(
    screen.getByPlaceholderText(/type a command or search/i),
    "unmatched-outside-page",
  );
  expect(
    await screen.findByText(
      "Template search covers the first 500 of 501 templates.",
    ),
  ).toBeVisible();
  expect(screen.queryByText("Tea 0")).not.toBeInTheDocument();
  expect(templateRequests).toHaveLength(1);
  expect(templateRequests[0].get("limit")).toBe("500");
  expect(templateRequests[0].has("q")).toBe(false);
});

it("shows loading rather than a successful-empty claim until the initial template read settles", async () => {
  let readStarted!: () => void;
  const started = new Promise<void>((resolve) => {
    readStarted = resolve;
  });
  let finishRead!: () => void;
  const heldRead = new Promise<void>((resolve) => {
    finishRead = resolve;
  });
  server.use(
    http.get("*/api/item-templates", async () => {
      readStarted();
      await heldRead;
      return HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 });
    }),
  );
  renderPalette();
  const user = userEvent.setup();
  try {
    await user.type(
      screen.getByPlaceholderText(/type a command or search/i),
      "unmatched-pending",
    );
    await started;
    expect(await screen.findByText("Loading template results…")).toBeVisible();
    expect(screen.queryByText(/^No matches\./)).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  } finally {
    finishRead();
  }
  expect(await screen.findByText(/^No matches\./)).toBeVisible();
  expect(screen.getByPlaceholderText(/type a command or search/i)).toHaveValue(
    "unmatched-pending",
  );
});
