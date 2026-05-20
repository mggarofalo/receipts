import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import AdminUsers from "./AdminUsers";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAuth", () => ({
  useAuth: vi.fn(() => ({
    user: { email: "admin@example.com" },
  })),
}));

vi.mock("@/hooks/useUsers", () => ({
  useUsers: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
  useCreateUser: vi.fn(() => ({ mutateAsync: vi.fn(), isPending: false })),
  useUpdateUser: vi.fn(() => ({ mutateAsync: vi.fn(), isPending: false })),
  useDeleteUser: vi.fn(() => ({ mutateAsync: vi.fn(), isPending: false })),
  useResetUserPassword: vi.fn(() => ({
    mutateAsync: vi.fn(),
    isPending: false,
  })),
}));

vi.mock("@/hooks/useServerPagination", () => ({
  useServerPagination: vi.fn(() => ({
    offset: 0,
    limit: 20,
    currentPage: 1,
    pageSize: 20,
    totalPages: vi.fn(() => 1),
    setPage: vi.fn(),
    setPageSize: vi.fn(),
    resetPage: vi.fn(),
  })),
}));

vi.mock("@/hooks/useServerSort", () => ({
  useServerSort: vi.fn(() => ({
    sortBy: "email",
    sortDirection: "asc",
    toggleSort: vi.fn(),
  })),
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

describe("AdminUsers", () => {
  it("renders the page heading", () => {
    renderWithProviders(<AdminUsers />);
    expect(
      screen.getByRole("heading", { name: /^users$/i }),
    ).toBeInTheDocument();
  });

  it("renders the Create User button", () => {
    renderWithProviders(<AdminUsers />);
    expect(
      screen.getByRole("button", { name: /new user/i }),
    ).toBeInTheDocument();
  });

  it("renders loading skeleton when data is loading", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: true,
    }));

    const { container } = renderWithProviders(<AdminUsers />);
    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();
  });

  it("renders empty state when no users exist", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getByText(/no users found/i)).toBeInTheDocument();
  });

  it("renders user table when users exist", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getByText("test@example.com")).toBeInTheDocument();
  });

  it("opens create user dialog when Create User button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithProviders(<AdminUsers />);

    await user.click(screen.getByRole("button", { name: /new user/i }));

    expect(
      screen.getByText(/create a new user account/i),
    ).toBeInTheDocument();
  });

  it("opens edit user dialog when Edit button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    expect(
      screen.getByText(/update user details/i),
    ).toBeInTheDocument();
  });

  it("opens reset password dialog when Reset PW button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /reset pw/i }));

    expect(
      screen.getByRole("heading", { name: /reset password/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/set a new password/i),
    ).toBeInTheDocument();
  });

  it("disables Disable button for the current user", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "admin@example.com",
          firstName: "Admin",
          lastName: "User",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getByRole("button", { name: /disable/i })).toBeDisabled();
  });

  it("disables Disable button for already disabled users", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "2",
          email: "disabled@example.com",
          firstName: "Disabled",
          lastName: "User",
          roles: ["User"],
          isDisabled: true,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getByRole("button", { name: /disable/i })).toBeDisabled();
    expect(screen.getByText("Disabled")).toBeInTheDocument();
  });

  it("shows user name formatted from first and last name", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getByText("John Doe")).toBeInTheDocument();
  });

  it("shows dash when user has no first or last name", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: null,
          lastName: null,
          roles: ["User"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: null,
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    // The name column should show "-"
    const cells = screen.getAllByRole("cell");
    expect(cells[0].textContent).toBe("-");
  });

  it("renders pagination when totalPages > 1", async () => {
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 20,
      currentPage: 1,
      pageSize: 20,
      totalPages: vi.fn(() => 2),
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage: vi.fn(),
    });

    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: Array.from({ length: 20 }, (_, i) => ({
        id: String(i + 1),
        email: `user${i + 1}@example.com`,
        firstName: `User`,
        lastName: `${i + 1}`,
        roles: ["User"],
        isDisabled: false,
        createdAt: "2024-01-01",
        lastLoginAt: null,
      })),
      total: 25,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    expect(screen.getAllByText("1/2")).toHaveLength(2);
    const prevButtons = screen.getAllByRole("button", { name: /previous page/i });
    const nextButtons = screen.getAllByRole("button", { name: /next page/i });
    expect(prevButtons).toHaveLength(2);
    expect(nextButtons).toHaveLength(2);
    prevButtons.forEach((btn) => expect(btn).toBeDisabled());
    nextButtons.forEach((btn) => expect(btn).not.toBeDisabled());
  });

  it("advances page when Next button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockSetPage = vi.fn();
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 20,
      currentPage: 1,
      pageSize: 20,
      totalPages: vi.fn(() => 2),
      setPage: mockSetPage,
      setPageSize: vi.fn(),
      resetPage: vi.fn(),
    });

    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: Array.from({ length: 20 }, (_, i) => ({
        id: String(i + 1),
        email: `user${i + 1}@example.com`,
        firstName: `User`,
        lastName: `${i + 1}`,
        roles: ["User"],
        isDisabled: false,
        createdAt: "2024-01-01",
        lastLoginAt: null,
      })),
      total: 25,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    const nextButtons = screen.getAllByRole("button", { name: /next page/i });
    await user.click(nextButtons[0]);

    expect(mockSetPage).toHaveBeenCalledWith(2, 25);
  });

  it("closes edit dialog when dialog is dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /edit/i }));
    expect(screen.getByText(/update user details/i)).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByText(/update user details/i)).not.toBeInTheDocument();
    });
  });

  it("closes reset password dialog when dismissed", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /reset pw/i }));
    expect(screen.getByRole("heading", { name: /reset password/i })).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await vi.waitFor(() => {
      expect(screen.queryByRole("heading", { name: /reset password/i })).not.toBeInTheDocument();
    });
  });

  it("opens deactivate dialog and calls deleteUser on confirm", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutateAsync = vi.fn().mockResolvedValue(undefined);
    const { useUsers, useDeleteUser } = await import("@/hooks/useUsers");
    vi.mocked(useDeleteUser).mockReturnValue({
      mutateAsync: mockMutateAsync,
      isPending: false,
    } as unknown as ReturnType<typeof useDeleteUser>);
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "2",
          email: "other@example.com",
          firstName: "Other",
          lastName: "User",
          roles: ["User"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /disable/i }));

    expect(screen.getByRole("heading", { name: /deactivate user/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /deactivate/i }));

    await vi.waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledWith("2");
    });
  });

  it("submits create user form with correct payload", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutateAsync = vi.fn().mockResolvedValue(undefined);
    const { useCreateUser } = await import("@/hooks/useUsers");
    vi.mocked(useCreateUser).mockReturnValue({
      mutateAsync: mockMutateAsync,
      isPending: false,
    } as unknown as ReturnType<typeof useCreateUser>);

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /new user/i }));

    await user.type(screen.getByPlaceholderText("name@example.com"), "new@example.com");
    await user.type(screen.getByPlaceholderText("At least 8 characters"), "password123");

    const submitButtons = screen.getAllByRole("button", { name: /new user/i });
    const dialogSubmit = submitButtons.find((btn) => btn.closest("[role='dialog']") !== null);
    await user.click(dialogSubmit!);

    await vi.waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledWith(
        expect.objectContaining({
          email: "new@example.com",
          password: "password123",
          role: "User",
        }),
      );
    });
  });

  it("submits edit user form with correct payload", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutateAsync = vi.fn().mockResolvedValue(undefined);
    const { useUsers, useUpdateUser } = await import("@/hooks/useUsers");
    vi.mocked(useUpdateUser).mockReturnValue({
      mutateAsync: mockMutateAsync,
      isPending: false,
    } as unknown as ReturnType<typeof useUpdateUser>);
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /edit/i }));

    const emailInput = screen.getByDisplayValue("test@example.com");
    await user.clear(emailInput);
    await user.type(emailInput, "updated@example.com");

    await user.click(screen.getByRole("button", { name: /save changes/i }));

    await vi.waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledWith(
        expect.objectContaining({
          userId: "1",
          body: expect.objectContaining({ email: "updated@example.com" }),
        }),
      );
    });
  });

  it("submits reset password form", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockMutateAsync = vi.fn().mockResolvedValue(undefined);
    const { useUsers, useResetUserPassword } = await import("@/hooks/useUsers");
    vi.mocked(useResetUserPassword).mockReturnValue({
      mutateAsync: mockMutateAsync,
      isPending: false,
    } as unknown as ReturnType<typeof useResetUserPassword>);
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "test@example.com",
          firstName: "John",
          lastName: "Doe",
          roles: ["Admin"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: "2024-01-15",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByRole("button", { name: /reset pw/i }));

    await user.type(screen.getByPlaceholderText("At least 8 characters"), "newpassword123");
    await user.click(screen.getByRole("button", { name: /^reset password$/i }));

    await vi.waitFor(() => {
      expect(mockMutateAsync).toHaveBeenCalledWith({
        userId: "1",
        newPassword: "newpassword123",
      });
    });
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
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "click@example.com",
          firstName: "Click",
          lastName: "Test",
          roles: ["User"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: null,
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    await user.click(screen.getByText("click@example.com"));

    expect(mockSetFocusedIndex).toHaveBeenCalledWith(0);
  });

  it("shows just first name when last name is null", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "jane@example.com",
          firstName: "Jane",
          lastName: null,
          roles: ["User"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: null,
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    const cells = screen.getAllByRole("cell");
    expect(cells[0].textContent).toBe("Jane");
  });

  it("shows secondary badge variant for non-Admin role", async () => {
    const { useUsers } = await import("@/hooks/useUsers");
    vi.mocked(useUsers).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          email: "user@example.com",
          firstName: "Regular",
          lastName: "User",
          roles: ["User"],
          isDisabled: false,
          createdAt: "2024-01-01",
          lastLoginAt: null,
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AdminUsers />);
    const roleBadge = screen.getByText("User", { selector: "[data-slot='badge']" });
    expect(roleBadge).toBeInTheDocument();
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    const { act } = await import("@testing-library/react");
    renderWithProviders(<AdminUsers />);

    act(() => {
      window.dispatchEvent(new Event("shortcut:new-item"));
    });

    await screen.findByRole("heading", { name: /new user/i });
    expect(
      screen.getByRole("heading", { name: /new user/i }),
    ).toBeInTheDocument();
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithProviders(<AdminUsers />, {
      route: { pathname: "/admin/users", state: { openNew: true } },
    });

    await screen.findByRole("heading", { name: /new user/i });
    expect(
      screen.getByRole("heading", { name: /new user/i }),
    ).toBeInTheDocument();
  });
});
