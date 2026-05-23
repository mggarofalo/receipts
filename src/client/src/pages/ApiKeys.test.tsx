import { screen } from "@testing-library/react";
import { renderWithQueryClient } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import ApiKeys from "./ApiKeys";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useListKeyboardNav", () => ({
  useListKeyboardNav: vi.fn(() => ({
    focusedId: null,
    focusedIndex: -1,
    setFocusedIndex: vi.fn(),
    tableRef: { current: null },
    containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
    getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
  })),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    roles: ["User"],
    hasRole: (role: string) => role === "User",
    isAdmin: () => false,
  })),
}));

vi.mock("@tanstack/react-query", async (importOriginal) => {
  const actual =
    await importOriginal<typeof import("@tanstack/react-query")>();
  return {
    ...actual,
    useQuery: vi.fn(() => ({
      data: [],
      isLoading: false,
    })),
    useMutation: vi.fn(() => ({
      mutate: vi.fn(),
      isPending: false,
    })),
    useQueryClient: vi.fn(() => ({
      invalidateQueries: vi.fn(),
    })),
  };
});

vi.mock("@/lib/toast", () => ({
  showSuccess: vi.fn(),
  showError: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    DELETE: vi.fn(),
  },
}));

describe("ApiKeys", () => {
  it("renders the page heading", () => {
    renderWithQueryClient(<ApiKeys />);
    expect(
      screen.getByRole("heading", { name: /api keys/i }),
    ).toBeInTheDocument();
  });

  it("renders the New API Key button", () => {
    renderWithQueryClient(<ApiKeys />);
    expect(
      screen.getByRole("button", { name: /new api key/i }),
    ).toBeInTheDocument();
  });

  it("renders the description text", () => {
    renderWithQueryClient(<ApiKeys />);
    expect(
      screen.getByText(/manage api keys for programmatic access/i),
    ).toBeInTheDocument();
  });

  it("renders the Your API Keys card", () => {
    renderWithQueryClient(<ApiKeys />);
    expect(screen.getByText(/your api keys/i)).toBeInTheDocument();
  });

  it("renders empty state when no API keys exist", () => {
    renderWithQueryClient(<ApiKeys />);
    expect(
      screen.getByText(/no api keys yet/i),
    ).toBeInTheDocument();
  });

  it("renders table with API keys when data exists", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    expect(screen.getByText("Test Key")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /revoke/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when the New API Key button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<ApiKeys />);

    await user.click(
      screen.getByRole("button", { name: /new api key/i }),
    );

    expect(
      screen.getByRole("heading", { name: /create api key/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByPlaceholderText(/paperless integration/i),
    ).toBeInTheDocument();
  });

  it("shows revoked badge for revoked keys", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-2",
          name: "Revoked Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: true,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    expect(screen.getByText("Revoked")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /revoke/i })).not.toBeInTheDocument();
  });

  it("shows expired badge for expired keys", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-3",
          name: "Expired Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: "2020-01-01T00:00:00Z",
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    expect(screen.getByText("Expired")).toBeInTheDocument();
  });

  it("opens revoke confirmation dialog when Revoke button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /revoke/i }));

    expect(
      screen.getByRole("heading", { name: /revoke api key/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/this action cannot be undone/i),
    ).toBeInTheDocument();
  });

  it("calls mutate when revoke is confirmed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useQuery, useMutation } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: mockMutate,
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    // Now click the destructive Revoke button in the dialog
    const dialogButtons = screen.getAllByRole("button", { name: /revoke/i });
    const confirmButton = dialogButtons.find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    expect(confirmButton).not.toBeNull();
    await user.click(confirmButton!);
    expect(mockMutate).toHaveBeenCalledWith("key-1");
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithQueryClient(<ApiKeys />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /create api key/i });
    expect(
      screen.getByRole("heading", { name: /create api key/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithQueryClient(<ApiKeys />, {
      route: { pathname: "/api-keys", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /create api key/i });
    expect(
      screen.getByRole("heading", { name: /create api key/i }),
    ).toBeInTheDocument();
  });

  it("shows loading skeleton when data is loading", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [],
      isLoading: true,
    }));

    const { container } = renderWithQueryClient(<ApiKeys />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("formats dates correctly in the table", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-06-15T00:00:00Z",
          lastUsedAt: "2024-07-20T00:00:00Z",
          expiresAt: "2025-06-15T00:00:00Z",
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    // Dates should be formatted as locale date strings, not raw ISO
    expect(screen.queryByText("2024-06-15T00:00:00Z")).not.toBeInTheDocument();
  });

  it("shows created key dialog when create mutation succeeds", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess({ rawKey: "test-secret-key-123" }, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "My Key");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    // The created key dialog should appear with the key
    expect(await screen.findByRole("heading", { name: /api key created/i })).toBeInTheDocument();
    expect(screen.getByText(/save this key now/i)).toBeInTheDocument();
    expect(screen.getByDisplayValue("test-secret-key-123")).toBeInTheDocument();
  });

  it("copies key to clipboard when Copy to Clipboard button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const originalClipboard = navigator.clipboard;
    const mockWriteText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText: mockWriteText },
      writable: true,
      configurable: true,
    });

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess({ rawKey: "copy-me-key" }, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "Copy Test");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await screen.findByRole("heading", { name: /api key created/i });
    await user.click(screen.getByRole("button", { name: /copy to clipboard/i }));

    expect(mockWriteText).toHaveBeenCalledWith("copy-me-key");

    Object.defineProperty(navigator, "clipboard", {
      value: originalClipboard,
      writable: true,
      configurable: true,
    });
  });

  it("cancels revoke dialog when Cancel button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    // Dialog should be open
    expect(screen.getByRole("heading", { name: /revoke api key/i })).toBeInTheDocument();

    // Click Cancel
    await user.click(screen.getByRole("button", { name: /cancel/i }));

    // Dialog should close
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /revoke api key/i })).not.toBeInTheDocument();
    });
  });

  it("shows create form submission with mutation", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useMutation } = await import("@tanstack/react-query");
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: mockMutate,
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "New API Key");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await vi.waitFor(() => {
      expect(mockMutate).toHaveBeenCalledWith(expect.objectContaining({ name: "New API Key" }));
    });
  });

  it("formats date with null value as dash", async () => {
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    // lastUsedAt and expiresAt are null — should show "-"
    const cells = screen.getAllByRole("cell");
    const dashCells = cells.filter((c) => c.textContent === "-");
    expect(dashCells.length).toBeGreaterThanOrEqual(2);
  });

  it("calls setFocusedIndex when a table row is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockSetFocusedIndex = vi.fn();
    const { useListKeyboardNav } = await import("@/hooks/useListKeyboardNav");
    vi.mocked(useListKeyboardNav).mockReturnValue({
      focusedId: null,
      focusedIndex: -1,
      setFocusedIndex: mockSetFocusedIndex,
      tableRef: { current: null },
      containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
      getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
    });
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Click Row Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByText("Click Row Key"));

    expect(mockSetFocusedIndex).toHaveBeenCalledWith(0);
  });

  it("shows error toast when clipboard copy fails", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const { showError } = await import("@/lib/toast");
    const originalClipboard = navigator.clipboard;

    Object.defineProperty(navigator, "clipboard", {
      value: { writeText: vi.fn().mockRejectedValue(new Error("copy failed")) },
      writable: true,
      configurable: true,
    });

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess({ rawKey: "fail-copy-key" }, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "Fail Copy");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await screen.findByRole("heading", { name: /api key created/i });
    await user.click(screen.getByRole("button", { name: /copy to clipboard/i }));

    await vi.waitFor(() => {
      expect(showError).toHaveBeenCalledWith("Failed to copy to clipboard.");
    });

    Object.defineProperty(navigator, "clipboard", {
      value: originalClipboard,
      writable: true,
      configurable: true,
    });
  });

  it("shows error toast when create mutation fails", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const { showError } = await import("@/lib/toast");

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn(() => {
        if (opts?.onError) {
          opts.onError(new Error("fail"), undefined, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "Error Key");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await vi.waitFor(() => {
      expect(showError).toHaveBeenCalledWith("Failed to create API key.");
    });
  });

  it("shows error toast when revoke mutation fails", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery, useMutation } = await import("@tanstack/react-query");
    const { showError } = await import("@/lib/toast");

    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Revoke Fail Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn(() => {
        if (opts?.onError) {
          opts.onError(new Error("fail"), undefined, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    const dialogButtons = screen.getAllByRole("button", { name: /revoke/i });
    const confirmButton = dialogButtons.find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    expect(confirmButton).not.toBeNull();
    await user.click(confirmButton!);
    await vi.waitFor(() => {
      expect(showError).toHaveBeenCalledWith("Failed to revoke API key.");
    });
  });

  it("closes created key dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess({ rawKey: "dismiss-key" }, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "Dismiss Test");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await screen.findByRole("heading", { name: /api key created/i });
    await user.keyboard("{Escape}");

    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /api key created/i })).not.toBeInTheDocument();
    });
  });

  it("does not show created key dialog when create mutation returns no data", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess(undefined, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "No Data Key");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /api key created/i })).not.toBeInTheDocument();
    });
  });

  it("shows success toast when revoke mutation succeeds", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery, useMutation } = await import("@tanstack/react-query");
    const { showSuccess } = await import("@/lib/toast");

    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [{
        id: "key-1", name: "Success Revoke Key", createdAt: "2024-01-01T00:00:00Z",
        lastUsedAt: null, expiresAt: null, isRevoked: false,
      }],
      isLoading: false,
    }));

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn(() => {
        if (opts?.onSuccess) {
          opts.onSuccess(undefined, undefined, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    const dialogButtons = screen.getAllByRole("button", { name: /revoke/i });
    const confirmButton = dialogButtons.find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    await user.click(confirmButton!);

    await vi.waitFor(() => {
      expect(showSuccess).toHaveBeenCalledWith("API key revoked.");
    });
  });

  it("closes revoke dialog via Escape key", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [{
        id: "key-1", name: "Esc Key", createdAt: "2024-01-01T00:00:00Z",
        lastUsedAt: null, expiresAt: null, isRevoked: false,
      }],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));
    expect(screen.getByRole("heading", { name: /revoke api key/i })).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /revoke api key/i })).not.toBeInTheDocument();
    });
  });

  it("does not show bypass checkbox for non-admin users", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));

    expect(screen.queryByLabelText(/bypass rate limiting/i)).not.toBeInTheDocument();
  });

  it("shows bypass checkbox for admin users", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue({
      roles: ["Admin"],
      hasRole: (role: string) => role === "Admin",
      isAdmin: () => true,
    });
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));

    expect(screen.getByLabelText(/bypass rate limiting/i)).toBeInTheDocument();
  });

  it("highlights focused row with bg-accent class", async () => {
    const { useListKeyboardNav } = await import("@/hooks/useListKeyboardNav");
    vi.mocked(useListKeyboardNav).mockReturnValue({
      focusedId: "key-1",
      focusedIndex: 0,
      setFocusedIndex: vi.fn(),
      tableRef: { current: null },
      containerProps: { role: "grid" as const, tabIndex: 0, "aria-label": "list", "aria-activedescendant": undefined },
      getRowProps: (id: string) => ({ id: `list-row-${id}`, role: "row" as const }),
    });
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [{
        id: "key-1", name: "Focused Key", createdAt: "2024-01-01T00:00:00Z",
        lastUsedAt: null, expiresAt: null, isRevoked: false,
      }],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    const row = screen.getByText("Focused Key").closest("tr");
    expect(row?.className).toContain("bg-accent");
  });

  it("selects text in created key input when clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const mockSelect = vi.fn();

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => ({
      mutate: vi.fn((values: unknown) => {
        if (opts?.onSuccess) {
          opts.onSuccess({ rawKey: "select-key" }, values, undefined);
        }
      }),
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));
    await user.type(screen.getByPlaceholderText(/paperless integration/i), "Select Test");
    await user.click(screen.getByRole("button", { name: /create key/i }));

    await screen.findByRole("heading", { name: /api key created/i });

    const keyInput = screen.getByDisplayValue("select-key");
    // Mock select on the input element
    (keyInput as HTMLInputElement).select = mockSelect;
    await user.click(keyInput);

    expect(mockSelect).toHaveBeenCalled();
  });

  it("revoke confirmation uses alertdialog role", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
  });

  it("revoke alertdialog cancel button closes the dialog", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    await vi.waitFor(() => {
      expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    });
  });

  it("revoke alertdialog confirm button calls mutate", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutate = vi.fn();
    const { useQuery, useMutation } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "key-1",
          name: "Test Key",
          createdAt: "2024-01-01T00:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          isRevoked: false,
        },
      ],
      isLoading: false,
    }));
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: mockMutate,
      isPending: false,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
    const confirmButton = screen.getAllByRole("button", { name: /^revoke$/i }).find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    expect(confirmButton).not.toBeNull();
    await user.click(confirmButton!);
    expect(mockMutate).toHaveBeenCalledWith("key-1");
  });

  it("shows Creating state when create mutation is pending", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: vi.fn(),
      isPending: true,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    await user.click(screen.getByRole("button", { name: /new api key/i }));

    expect(screen.getByRole("button", { name: /creating/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /creating/i })).toBeDisabled();
  });

  it("shows Revoking state when revoke mutation is pending", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useQuery, useMutation } = await import("@tanstack/react-query");
    vi.mocked(useQuery).mockReturnValue(mockQueryResult({
      data: [{
        id: "key-1", name: "Pending Revoke", createdAt: "2024-01-01T00:00:00Z",
        lastUsedAt: null, expiresAt: null, isRevoked: false,
      }],
      isLoading: false,
    }));
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: vi.fn(),
      isPending: true,
    })) as unknown as typeof useMutation);

    renderWithQueryClient(<ApiKeys />);
    // Click the table Revoke button to open the dialog
    await user.click(screen.getByRole("button", { name: /^revoke$/i }));

    // The confirm button in the dialog should show "Revoking..."
    const dialogButtons = screen.getAllByRole("button", { name: /revoking/i });
    const confirmButton = dialogButtons.find(
      (btn) => btn.closest("[role='alertdialog']") !== null,
    );
    expect(confirmButton).not.toBeNull();
    expect(confirmButton).toBeDisabled();
  });
});
