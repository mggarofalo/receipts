import { screen } from "@testing-library/react";
import { renderWithQueryClient } from "@/test/test-utils";
import BackupRestore from "./BackupRestore";

vi.mock("@/hooks/useSessionMutation", async () => {
  const { useMutation } = await import("@tanstack/react-query");
  return { useSessionMutation: useMutation };
});

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@tanstack/react-query", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@tanstack/react-query")>();
  return {
    ...actual,
    useMutation: vi.fn(() => ({
      mutate: vi.fn(),
      mutateAsync: vi.fn(),
      isPending: false,
    })),
  };
});

vi.mock("@/lib/toast", () => ({
  showSuccess: vi.fn(),
  showError: vi.fn(),
}));

vi.mock("@/lib/auth", async (importOriginal) => ({
  ...await importOriginal<typeof import("@/lib/auth")>(),
  getAccessToken: vi.fn(() => "mock-token"),
}));

describe("BackupRestore", () => {
  it("renders the page heading", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByRole("heading", { name: /backup & restore/i }),
    ).toBeInTheDocument();
  });

  it("renders the description text", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByText(/export or import a portable sqlite backup/i),
    ).toBeInTheDocument();
  });

  it("renders the Export Backup card", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByText(/export backup/i, {
        selector: "[data-slot='card-title']",
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/download a sqlite backup of your database/i),
    ).toBeInTheDocument();
  });

  it("renders the Import Backup card", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByText(/import backup/i, {
        selector: "[data-slot='card-title']",
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/upload a previously exported sqlite backup/i),
    ).toBeInTheDocument();
  });

  it("renders the Export Backup button", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByRole("button", { name: /export backup/i }),
    ).toBeInTheDocument();
  });

  it("renders the Import Backup button as disabled when no file is selected", () => {
    renderWithQueryClient(<BackupRestore />);
    const importBtn = screen.getByRole("button", { name: /import backup/i });
    expect(importBtn).toBeDisabled();
  });

  it("renders the file input for uploading backups", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(document.getElementById("backup-file")).toBeInTheDocument();
  });

  it("renders the info alert about what backups include", () => {
    renderWithQueryClient(<BackupRestore />);
    expect(
      screen.getByText(/backups include your receipts/i),
    ).toBeInTheDocument();
  });

  it("enables Import Backup button when a file is selected", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    const importBtn = screen.getByRole("button", { name: /import backup/i });
    expect(importBtn).not.toBeDisabled();
  });

  it("shows selected file name and size after file selection", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test-data"], "my-backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    expect(
      screen.getByText(/selected: my-backup\.sqlite/i),
    ).toBeInTheDocument();
  });

  it("opens confirmation dialog when Import Backup is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(
      screen.getByRole("heading", { name: /confirm import/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/importing a backup will update existing records/i),
    ).toBeInTheDocument();
  });

  it("import confirmation uses alertdialog role", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();
  });

  it("import alertdialog cancel button closes the dialog", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    await vi.waitFor(() => {
      expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    });
  });

  it("import alertdialog confirm button calls mutate", async () => {
    const mockMutate = vi.fn();
    const { useMutation } = await import("@tanstack/react-query");
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: mockMutate,
      mutateAsync: vi.fn(),
      isPending: false,
    })) as unknown as typeof useMutation);

    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(screen.getByRole("alertdialog")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /confirm import/i }));

    expect(mockMutate).toHaveBeenCalled();
  });

  it("closes confirmation dialog when Cancel is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(
      screen.getByRole("heading", { name: /confirm import/i }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));

    await vi.waitFor(() => {
      expect(
        screen.queryByRole("heading", { name: /confirm import/i }),
      ).not.toBeInTheDocument();
    });
  });

  it("calls mutate when import is confirmed", async () => {
    const mockMutate = vi.fn();
    const { useMutation } = await import("@tanstack/react-query");
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: mockMutate,
      mutateAsync: vi.fn(),
      isPending: false,
    })) as unknown as typeof useMutation);

    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    await user.click(screen.getByRole("button", { name: /confirm import/i }));

    // The import mutation is the second useMutation call
    expect(mockMutate).toHaveBeenCalled();
  });

  it("calls export mutation when Export Backup is clicked", async () => {
    const mockMutate = vi.fn().mockResolvedValue(undefined);
    const { useMutation } = await import("@tanstack/react-query");
    vi.mocked(useMutation).mockImplementation((() => ({
      mutate: vi.fn(),
      mutateAsync: mockMutate,
      isPending: false,
    })) as unknown as typeof useMutation);

    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    await user.click(screen.getByRole("button", { name: /export backup/i }));

    expect(mockMutate).toHaveBeenCalled();
  });

  it("shows Exporting state when export mutation is pending", async () => {
    const { useMutation } = await import("@tanstack/react-query");
    let callCount = 0;
    vi.mocked(useMutation).mockImplementation((() => {
      callCount++;
      if (callCount === 1) {
        // export mutation
        return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: true };
      }
      // import mutation
      return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false };
    }) as unknown as typeof useMutation);

    renderWithQueryClient(<BackupRestore />);

    const exportBtn = screen.getByRole("button", { name: /exporting/i });
    expect(exportBtn).toBeDisabled();
  });

  it("shows Importing state when import mutation is pending", async () => {
    const { useMutation } = await import("@tanstack/react-query");
    let callCount = 0;
    vi.mocked(useMutation).mockImplementation((() => {
      callCount++;
      if (callCount === 1) {
        // export mutation
        return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false };
      }
      // import mutation
      return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: true };
    }) as unknown as typeof useMutation);

    renderWithQueryClient(<BackupRestore />);

    const importBtn = screen.getByRole("button", { name: /importing/i });
    expect(importBtn).toBeDisabled();
  });

  // Export success/error notifications are owned by useBackupExport and are
  // covered using the real hook in useBackup.test.ts.

  it("shows success toast when import mutation succeeds", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const { showSuccess } = await import("@/lib/toast");

    let callCount = 0;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => {
      callCount++;
      if (callCount === 1) {
        // export mutation
        return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false };
      }
      // import mutation
      return {
        mutate: vi.fn(() => {
          if (opts?.onSuccess) {
            opts.onSuccess(
              { totalCreated: 10, totalUpdated: 5 },
              undefined,
              undefined,
            );
          }
        }),
        mutateAsync: vi.fn(),
        isPending: false,
      };
    }) as unknown as typeof useMutation);

    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));
    await user.click(screen.getByRole("button", { name: /confirm import/i }));

    await vi.waitFor(() => {
      expect(showSuccess).toHaveBeenCalledWith(
        "Import complete: 10 created, 5 updated.",
      );
    });
  });

  it("shows error toast when import mutation fails", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");
    const { showError } = await import("@/lib/toast");

    let callCount = 0;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(useMutation).mockImplementation(((opts: any) => {
      callCount++;
      if (callCount === 1) {
        // export mutation
        return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false };
      }
      // import mutation
      return {
        mutate: vi.fn(() => {
          if (opts?.onError) {
            opts.onError(
              new Error("Invalid or corrupt backup file."),
              undefined,
              undefined,
            );
          }
        }),
        mutateAsync: vi.fn(),
        isPending: false,
      };
    }) as unknown as typeof useMutation);

    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));
    await user.click(screen.getByRole("button", { name: /confirm import/i }));

    await vi.waitFor(() => {
      expect(showError).toHaveBeenCalledWith("Invalid or corrupt backup file.");
    });
  });

  it("shows file details in the confirmation dialog", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test-data-content"], "my-backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(screen.getByText(/file: my-backup\.sqlite/i)).toBeInTheDocument();
  });

  it("closes confirmation dialog via Escape key", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(
      screen.getByRole("heading", { name: /confirm import/i }),
    ).toBeInTheDocument();

    await user.keyboard("{Escape}");

    await vi.waitFor(() => {
      expect(
        screen.queryByRole("heading", { name: /confirm import/i }),
      ).not.toBeInTheDocument();
    });
  });

  it("accepts .sqlite files in the file input", () => {
    renderWithQueryClient(<BackupRestore />);
    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    expect(fileInput.accept).toBe(".sqlite,.db");
  });

  it("does not close confirmation dialog via Escape while import is pending", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useMutation } = await import("@tanstack/react-query");

    // Start with isPending = false so the user can open the dialog,
    // then switch to isPending = true to simulate an in-flight import
    let importPending = false;
    let callCount = 0;
    vi.mocked(useMutation).mockImplementation((() => {
      callCount++;
      if (callCount % 2 === 1) {
        // export mutation (odd calls)
        return { mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false };
      }
      // import mutation (even calls) - pending state is dynamic
      return {
        mutate: vi.fn(() => {
          importPending = true;
        }),
        mutateAsync: vi.fn(),
        get isPending() {
          return importPending;
        },
      };
    }) as unknown as typeof useMutation);

    const { rerender } = renderWithQueryClient(<BackupRestore />);

    const fileInput = document.getElementById(
      "backup-file",
    ) as HTMLInputElement;
    const testFile = new File(["test"], "backup.sqlite", {
      type: "application/octet-stream",
    });
    await user.upload(fileInput, testFile);

    // Open the confirmation dialog
    await user.click(screen.getByRole("button", { name: /import backup/i }));

    expect(
      screen.getByRole("heading", { name: /confirm import/i }),
    ).toBeInTheDocument();

    // Click Confirm Import (this triggers mutate, setting importPending = true)
    await user.click(screen.getByRole("button", { name: /confirm import/i }));

    // Re-render to pick up the pending state change
    // Reset mock call count for the re-render
    callCount = 0;
    rerender(<BackupRestore />);

    // The dialog should still be visible after pressing Escape because import is pending
    await user.keyboard("{Escape}");

    // Dialog should remain open since import is pending
    expect(
      screen.getByRole("heading", { name: /confirm import/i }),
    ).toBeInTheDocument();
  });
});
