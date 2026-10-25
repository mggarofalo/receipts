vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://entry-shortlist.test"));
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { QueryClientProvider } from "@tanstack/react-query";
import { createQueryClient, renderWithProviders } from "@/test/test-utils";
import { ReceiptNormalizationShortlist } from "./ReceiptNormalizationShortlist";

const source = {
  location: "Market",
  description: "BF FRANKS",
  itemCode: "0012",
};
const candidate = (name: string, index = 1) => ({
  id: `11111111-1111-4111-8111-${String(index).padStart(12, "0")}`,
  revision: "22222222-2222-4222-8222-222222222222",
  canonicalName: name,
  displayLabel: null,
  channels: ["lexical", "semantic"],
});
const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
function setup(
  props: Partial<
    React.ComponentProps<typeof ReceiptNormalizationShortlist>
  > = {},
) {
  const onChoose = vi.fn();
  const queryClient = createQueryClient();
  const view = renderWithProviders(
    <QueryClientProvider client={queryClient}>
      <ReceiptNormalizationShortlist
        source={source}
        onChoose={onChoose}
        {...props}
      />
    </QueryClientProvider>,
  );
  return { ...view, onChoose, queryClient };
}

it("preserves bounded server ranking and emits the chosen identity revision without writes", async () => {
  const candidates = [
    "Z best match",
    "A next match",
    "Third",
    "Fourth",
    "Fifth",
    "Sixth",
  ].map(candidate);
  let requested: URL | undefined;
  server.use(
    http.get("*/api/receipt-items/normalization-candidates", ({ request }) => {
      requested = new URL(request.url);
      return HttpResponse.json(candidates);
    }),
  );
  const { onChoose } = setup();
  const buttons = await screen.findAllByRole("button", {
    name: /^Choose suggested/,
  });
  expect(buttons.map((button) => button.textContent)).toEqual(
    candidates
      .slice(0, 5)
      .map((item) => `Choose suggested ${item.canonicalName}`),
  );
  expect(requested?.searchParams.get("location")).toBe(source.location);
  expect(requested?.searchParams.get("description")).toBe(source.description);
  expect(requested?.searchParams.get("itemCode")).toBe("0012");
  expect(screen.getAllByText(/Shared words/).length).toBeGreaterThan(0);
  expect(screen.queryByText(/Similar description/)).not.toBeInTheDocument();
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "More products (1)" }));
  expect(
    screen.getByRole("button", { name: "Choose suggested Sixth" }),
  ).toBeVisible();
  expect(onChoose).not.toHaveBeenCalled();
  await userEvent.setup().click(buttons[1]);
  expect(onChoose).toHaveBeenCalledExactlyOnceWith(candidates[1]);
});

it("offers local retry after a failed candidate lookup and then shows current suggestions", async () => {
  let attempts = 0;
  server.use(
    http.get("*/api/receipt-items/normalization-candidates", () =>
      ++attempts === 1
        ? HttpResponse.json(
            { status: 503, detail: "Suggestions unavailable" },
            { status: 503 },
          )
        : HttpResponse.json([candidate("Recovered product")]),
    ),
  );
  setup();
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Product suggestions unavailable. You can search or continue entering items.",
  );
  await userEvent.setup().click(screen.getByRole("button", { name: "Retry" }));
  expect(
    await screen.findByRole("button", {
      name: "Choose suggested Recovered product",
    }),
  ).toBeEnabled();
  expect(attempts).toBe(2);
});

it("never publishes a late previous-store candidate after the source changes", async () => {
  let release!: () => void;
  let started!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  const entered = new Promise<void>((resolve) => {
    started = resolve;
  });
  server.use(
    http.get(
      "*/api/receipt-items/normalization-candidates",
      async ({ request }) => {
        if (new URL(request.url).searchParams.get("location") === "Market") {
          started();
          await held;
          return HttpResponse.json([candidate("Obsolete product")]);
        }
        return HttpResponse.json([candidate("Current product")]);
      },
    ),
  );
  const { rerender, queryClient, onChoose } = setup();
  await entered;
  rerender(
    <QueryClientProvider client={queryClient}>
      <ReceiptNormalizationShortlist
        source={{ ...source, location: "Other store" }}
        onChoose={onChoose}
      />
    </QueryClientProvider>,
  );
  expect(
    await screen.findByRole("button", {
      name: "Choose suggested Current product",
    }),
  ).toBeEnabled();
  await act(async () => {
    release();
    await held;
  });
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(
    screen.queryByRole("button", { name: "Choose suggested Obsolete product" }),
  ).not.toBeInTheDocument();
  await userEvent
    .setup()
    .click(
      screen.getByRole("button", { name: "Choose suggested Current product" }),
    );
  expect(onChoose).toHaveBeenCalledExactlyOnceWith(
    candidate("Current product"),
  );
});

it("abstains from semantic-only suggestions and empty matches", async () => {
  server.use(
    http.get("*/api/receipt-items/normalization-candidates", () =>
      HttpResponse.json([
        { ...candidate("Unrelated juice"), channels: ["semantic"] },
      ]),
    ),
  );
  const { queryClient, onChoose } = setup();
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(
    screen.queryByRole("region", { name: "Suggested products" }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: /Choose suggested/ }),
  ).not.toBeInTheDocument();
  expect(onChoose).not.toHaveBeenCalled();
});
