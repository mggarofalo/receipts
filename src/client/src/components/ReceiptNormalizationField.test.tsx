import { useState } from "react";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { createQueryClient, renderWithProviders } from "@/test/test-utils";
import client from "@/lib/api-client";
import {
  normalizationSourceKey,
  type ReceiptNormalizationChoice,
} from "@/lib/receipt-normalization";
import { ReceiptNormalizationField } from "./ReceiptNormalizationField";

vi.mock("@/lib/api-client", () => ({
  default: { POST: vi.fn(), GET: vi.fn() },
}));
const source = {
  location: "Market A",
  description: "BF FRANKS",
  itemCode: "123",
};
const rowId = "11111111-1111-4111-8111-111111111111";
const targetId = "22222222-2222-4222-8222-222222222222";
const revision = "33333333-3333-4333-8333-333333333333";
const preview = {
  sourceFingerprint: "observed-source",
  excluded: false,
  sourceAmbiguous: false,
  candidates: [{ id: targetId, revision, name: "Beef franks 8 count" }],
  total: 1,
  offset: 0,
  limit: 10,
};
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(client.GET).mockResolvedValue({
    data: [
      {
        id: targetId,
        revision,
        canonicalName: "Beef franks 8 count",
        displayLabel: null,
        channels: ["lexical"],
      },
    ],
    response: new Response(),
  } as never);
});

it("does not attach an automatic known-source intent while the editor is disabled", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: {
      ...preview,
      mappingId: rowId,
      mappingRevision: revision,
      targetId,
      targetRevision: revision,
      targetName: "Beef franks",
    },
    response: new Response(),
  } as never);
  const changed = vi.fn();
  const settledCache = createQueryClient();
  renderWithProviders(
    <QueryClientProvider client={settledCache}>
      <ReceiptNormalizationField
        source={source}
        clientRowId={rowId}
        onChange={changed}
        disabled
      />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(client.POST).toHaveBeenCalled());
  await waitFor(() => expect(settledCache.isFetching()).toBe(0));
  expect(changed).not.toHaveBeenCalled();
  expect(screen.getByRole("button", { name: "Choose product" })).toBeDisabled();
});

function renderField() {
  const observed = vi.fn();
  const queryClient = createQueryClient();
  function Draft() {
    const [choice, setChoice] = useState<ReceiptNormalizationChoice>();
    return (
      <ReceiptNormalizationField
        source={source}
        clientRowId={rowId}
        value={choice}
        onChange={(value) => {
          observed(value);
          setChoice(value);
        }}
      />
    );
  }
  const view = renderWithProviders(
    <QueryClientProvider client={queryClient}>
      <Draft />
    </QueryClientProvider>,
  );
  return { ...view, observed, queryClient };
}

it("chooses an existing identity for this purchase without creating or linking anything before receipt save", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  expect(
    screen.queryByLabelText("Search all product names"),
  ).not.toBeInTheDocument();
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^Choose suggested/ })).not.toBeInTheDocument();
  expect(client.GET).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.click(
    await screen.findByRole("button", {
      name: "Choose suggested Beef franks 8 count",
    }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      label: "Beef franks 8 count",
      intent: expect.objectContaining({
        kind: "confirmExisting",
        clientRowId: rowId,
        sourceFingerprint: "observed-source",
        targetId,
        expectedTargetRevision: revision,
        rememberSource: false,
      }),
    }),
  );
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(screen.getByText(/Selected for this purchase/)).toHaveTextContent("Beef franks 8 count");
  expect(client.POST).toHaveBeenCalledWith(
    "/api/receipt-items/normalization-preview",
    expect.anything(),
  );
  expect(client.POST).toHaveBeenCalledTimes(1);
});

it("stages New product and use it together with an explicit remembered scope, without a catalog write", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await screen.findByRole("button", {
    name: "Choose suggested Beef franks 8 count",
  });
  await user.click(
    screen.getByRole("checkbox", {
      name: /Remember this exact description at Market A/,
    }),
  );
  await user.type(
    screen.getByLabelText("New product name"),
    " Turkey franks 8 count ",
  );
  await user.click(
    screen.getByRole("button", { name: "New product and use it" }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      intent: expect.objectContaining({
        kind: "confirmNew",
        newName: "Turkey franks 8 count",
        rememberSource: true,
        clientRowId: rowId,
        sourceFingerprint: "observed-source",
      }),
    }),
  );
  expect(client.POST).toHaveBeenCalledWith(
    "/api/receipt-items/normalization-preview",
    expect.anything(),
  );
  expect(client.POST).toHaveBeenCalledTimes(1);
});

it("does not commit an unfinished new-product draft when choices are closed", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.type(
    screen.getByLabelText("New product name"),
    "Unconfirmed product",
  );
  await user.click(screen.getByRole("button", { name: "Close" }));
  expect(observed).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  expect(screen.getByLabelText("New product name")).toHaveValue("");
});

it.each(["Escape", "Close"])(
  "%s cancels an unfinished edit and preserves the staged product choice",
  async (dismiss) => {
    vi.mocked(client.POST).mockResolvedValue({ data: preview, response: new Response() } as never);
    const { observed } = renderField();
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Choose product" }));
    await user.click(await screen.findByRole("button", { name: "Choose suggested Beef franks 8 count" }));
    const chosen = observed.mock.calls[0][0];
    await user.click(screen.getByRole("button", { name: "Change product" }));
    await user.type(screen.getByLabelText("New product name"), "Unconfirmed replacement");
    await user.click(screen.getByRole("checkbox", { name: /Remember this exact description/ }));
    if (dismiss === "Escape") await user.keyboard("{Escape}");
    else await user.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Change product" })).toHaveFocus());
    expect(observed).toHaveBeenCalledTimes(1);
    expect(observed).toHaveBeenLastCalledWith(chosen);
    expect(screen.getByText(/Selected for this purchase/)).toHaveTextContent("Beef franks 8 count");
    await user.keyboard("{Enter}");
    expect(screen.getByRole("dialog", { name: "Choose a product" })).toBeVisible();
    expect(screen.getByLabelText("New product name")).toHaveValue("");
    expect(screen.getByRole("checkbox", { name: /Remember this exact description/ })).not.toBeChecked();
  },
);

it("refreshes available choices without removing an already staged purchase choice", async () => {
  vi.mocked(client.POST).mockResolvedValue({ data: preview, response: new Response() } as never);
  const { observed, queryClient } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.click(await screen.findByRole("button", { name: "Choose suggested Beef franks 8 count" }));
  const chosen = observed.mock.calls[0][0];
  await user.click(screen.getByRole("button", { name: "Change product" }));
  await user.click(screen.getByRole("button", { name: "Refresh product choices" }));
  await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(queryClient.isFetching()).toBe(0));
  expect(observed).toHaveBeenCalledTimes(1);
  expect(observed).toHaveBeenLastCalledWith(chosen);
  await user.keyboard("{Escape}");
  expect(screen.getByText(/Selected for this purchase/)).toHaveTextContent("Beef franks 8 count");
});

it("disables product mutations while preview evidence is loading and still permits cancellation", async () => {
  vi.mocked(client.POST).mockImplementation(() => new Promise(() => {}));
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  expect(await screen.findByRole("button", { name: "Choose suggested Beef franks 8 count" })).toBeDisabled();
  await user.type(screen.getByLabelText("New product name"), "Unconfirmed product");
  expect(screen.getByRole("button", { name: "New product and use it" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Exclude from product grouping" })).toBeDisabled();
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(observed).not.toHaveBeenCalled();
});

it("preserves a reviewed one-off purchase when the exact-source mapping names a different product", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: {
      ...preview,
      mappingId: rowId,
      mappingRevision: revision,
      targetId,
      targetRevision: revision,
      targetName: "Beef franks 8 count",
    },
    response: new Response(),
  } as never);
  const changed = vi.fn();
  const settledCache = createQueryClient();
  renderWithProviders(
    <QueryClientProvider client={settledCache}>
      <ReceiptNormalizationField
        source={source}
        clientRowId={rowId}
        onChange={changed}
        currentAssignment={{
          sourceKey: normalizationSourceKey(source),
          state: "verified",
          name: "Turkey franks",
        }}
      />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(client.POST).toHaveBeenCalled());
  await waitFor(() => expect(settledCache.isFetching()).toBe(0));
  await screen.findByText(/Turkey franks/);
  expect(changed).not.toHaveBeenCalled();
});

it("does not submit the enclosing receipt when Enter is used in an unfinished product draft", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const submit = vi.fn((event: React.FormEvent) => event.preventDefault());
  const changed = vi.fn();
  const settledCache = createQueryClient();
  renderWithProviders(
    <QueryClientProvider client={settledCache}>
      <form onSubmit={submit}>
        <ReceiptNormalizationField
          source={source}
          clientRowId={rowId}
          onChange={changed}
        />
        <button type="submit">Save receipt</button>
      </form>
    </QueryClientProvider>,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.type(
    screen.getByLabelText("New product name"),
    "Unfinished{Enter}",
  );
  expect(submit).not.toHaveBeenCalled();
  expect(changed).not.toHaveBeenCalled();
});

it("keeps review-later usable when lookup fails, without pretending the receipt is verified", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    error: { detail: "Offline" },
    response: new Response(null, { status: 503 }),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await screen.findByText(/Product lookup is unavailable/);
  expect(
    screen.getByRole("button", { name: "New product and use it" }),
  ).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Review later" }));
  expect(observed).toHaveBeenLastCalledWith(undefined);
  expect(screen.queryByText(/Recognized:/)).not.toBeInTheDocument();
});

it("retains the observed mapping revision after refetch rather than silently advancing a confirmed choice", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: {
      ...preview,
      mappingId: "mapping",
      mappingRevision: "revision-before",
      targetId,
      targetRevision: revision,
      targetName: "Beef franks 8 count",
    },
    response: new Response(),
  } as never);
  const { observed, queryClient } = renderField();
  await waitFor(() => expect(observed).toHaveBeenCalledTimes(1));
  expect(observed.mock.calls[0][0].intent.expectedMappingRevision).toBe(
    "revision-before",
  );
  vi.mocked(client.POST).mockResolvedValue({
    data: {
      ...preview,
      mappingId: "mapping",
      mappingRevision: "revision-after",
      targetId,
      targetRevision: revision,
      targetName: "Beef franks 8 count",
    },
    response: new Response(),
  } as never);
  await act(async () => {
    await queryClient.invalidateQueries();
  });
  expect(observed).toHaveBeenCalledTimes(1);
});

it("prevents an unconditional remembered rule for a source with conflicting recorded identities", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: { ...preview, sourceAmbiguous: true },
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await screen.findByRole("button", {
    name: "Choose suggested Beef franks 8 count",
  });
  expect(
    screen.getByRole("checkbox", { name: /Remember this exact description/ }),
  ).toBeDisabled();
  await user.click(
    screen.getByRole("button", {
      name: "Choose suggested Beef franks 8 count",
    }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      intent: expect.objectContaining({ rememberSource: false }),
    }),
  );
});

it("does not show the alphabetical catalog until an explicit search is entered", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await screen.findByRole("button", {
    name: "Choose suggested Beef franks 8 count",
  });
  expect(
    screen.queryByRole("button", { name: "Use Beef franks 8 count" }),
  ).not.toBeInTheDocument();
  await user.type(screen.getByLabelText("Search all product names"), "Beef");
  expect(
    await screen.findByRole("button", { name: "Use Beef franks 8 count" }),
  ).toBeEnabled();
});

it("clears a product choice immediately when receipt source changes", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const changed = vi.fn();
  const cache = createQueryClient();
  const choice = {
    sourceKey: normalizationSourceKey(source),
    label: "Beef franks",
    intent: {
      kind: "confirmExisting" as const,
      targetId,
      expectedTargetRevision: revision,
      clientRowId: rowId,
      sourceFingerprint: "observed-source",
      rememberSource: false,
    },
  };
  const { rerender } = renderWithProviders(
    <QueryClientProvider client={cache}>
      <ReceiptNormalizationField
        source={source}
        clientRowId={rowId}
        value={choice}
        onChange={changed}
      />
    </QueryClientProvider>,
  );
  rerender(
    <QueryClientProvider client={cache}>
      <ReceiptNormalizationField
        source={{ ...source, description: "Turkey" }}
        clientRowId={rowId}
        value={choice}
        onChange={changed}
      />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(changed).toHaveBeenCalledWith(undefined));
  expect(
    screen.queryByText(/Selected for this purchase/),
  ).not.toBeInTheDocument();
});

it("supports explicitly remembering each staged choice and exclusion", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.click(
    screen.getByRole("checkbox", { name: /Remember this exact description/ }),
  );
  await user.click(
    await screen.findByRole("button", {
      name: "Choose suggested Beef franks 8 count",
    }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      intent: expect.objectContaining({
        kind: "confirmExisting",
        rememberSource: true,
      }),
    }),
  );
  await user.click(screen.getByRole("button", { name: "Change product" }));
  expect(screen.getByRole("checkbox", { name: /Remember this exact description/ })).not.toBeChecked();
  await user.click(screen.getByRole("checkbox", { name: /Remember this exact description/ }));
  await user.click(
    screen.getByRole("button", { name: "Exclude from product grouping" }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      intent: expect.objectContaining({
        kind: "exclude",
        sourceFingerprint: "observed-source",
        rememberSource: true,
      }),
    }),
  );
});

it("reopens alternatives after choosing a product and replaces the staged choice", async () => {
  const replacementId = "44444444-4444-4444-8444-444444444444";
  vi.mocked(client.POST).mockResolvedValue({
    data: preview,
    response: new Response(),
  } as never);
  vi.mocked(client.GET).mockResolvedValue({
    data: [
      {
        id: targetId,
        revision,
        canonicalName: "Beef franks 8 count",
        channels: ["lexical"],
      },
      {
        id: replacementId,
        revision,
        canonicalName: "Turkey franks 8 count",
        channels: ["lexical"],
      },
    ],
    response: new Response(),
  } as never);
  const { observed } = renderField();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Choose product" }));
  await user.click(
    await screen.findByRole("button", {
      name: "Choose suggested Beef franks 8 count",
    }),
  );
  expect(
    screen.queryByRole("button", {
      name: "Choose suggested Turkey franks 8 count",
    }),
  ).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Change product" }));
  await user.click(
    await screen.findByRole("button", {
      name: "Choose suggested Turkey franks 8 count",
    }),
  );
  expect(observed).toHaveBeenLastCalledWith(
    expect.objectContaining({
      label: "Turkey franks 8 count",
      intent: expect.objectContaining({
        kind: "confirmExisting",
        targetId: replacementId,
        expectedTargetRevision: revision,
        clientRowId: rowId,
        sourceFingerprint: "observed-source",
        rememberSource: false,
      }),
    }),
  );
  expect(
    screen.getByText(/Selected for this purchase/),
  ).toHaveTextContent("Selected for this purchase: Turkey franks 8 count");
  expect(client.POST).toHaveBeenCalledTimes(1);
});
