import { describe, expect, it, vi } from "vitest";
import { COMMANDS, REPORT_COMMANDS } from "./commands";
import type { CommandContext } from "./types";

function makeCtx(overrides: Partial<CommandContext> = {}): CommandContext {
  return {
    navigate: vi.fn(),
    close: vi.fn(),
    currentPath: "/",
    setPalette: vi.fn(),
    logout: vi.fn(async () => {}),
    openShortcutsHelp: vi.fn(),
    syncYnab: vi.fn(),
    exportBackup: vi.fn(),
    confirmEmptyTrash: vi.fn(),
    ...overrides,
  };
}

describe("command registry", () => {
  it("has unique command ids", () => {
    const ids = COMMANDS.map((c) => c.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("covers all documented top-level surfaces", () => {
    const ids = new Set(COMMANDS.map((c) => c.id));
    const expected = [
      "nav:dashboard",
      "nav:receipts",
      "nav:reports",
      "nav:accounts",
      "nav:cards",
      "nav:categories",
      "nav:subcategories",
      "nav:item-templates",
      "nav:security",
      "nav:api-keys",
      "nav:ynab",
      "nav:change-password",
      "nav:users",
      "nav:audit",
      "nav:trash",
      "nav:backup",
      "create:receipt",
      "create:account",
      "create:card",
      "create:category",
      "create:subcategory",
      "create:item-template",
      "create:api-key",
      "create:user",
      "pref:shortcuts-help",
      "pref:palette-graphite",
      "pref:palette-paper",
      "pref:sign-out",
    ];
    for (const id of expected) {
      expect(ids).toContain(id);
    }
  });

  it("marks admin-only commands with requiresAdmin", () => {
    const adminOnly = COMMANDS.filter((c) => c.requiresAdmin).map((c) => c.id);
    expect(adminOnly).toEqual(
      expect.arrayContaining([
        "nav:users",
        "nav:audit",
        "nav:trash",
        "nav:backup",
        "create:user",
        "action:backup-export",
        "action:trash-empty",
      ]),
    );
  });

  it("exposes action commands outside of the admin-gated set when appropriate", () => {
    const sync = COMMANDS.find((c) => c.id === "action:ynab-sync")!;
    expect(sync.requiresAdmin).toBeFalsy();
    expect(sync.group).toBe("actions");
  });

  it("action:ynab-sync closes and delegates to syncYnab", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "action:ynab-sync")!.run(ctx);
    expect(ctx.close).toHaveBeenCalled();
    expect(ctx.syncYnab).toHaveBeenCalled();
  });

  it("action:backup-export closes and delegates to exportBackup", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "action:backup-export")!.run(ctx);
    expect(ctx.close).toHaveBeenCalled();
    expect(ctx.exportBackup).toHaveBeenCalled();
  });

  it("action:trash-empty opens the confirm dialog without closing the palette", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "action:trash-empty")!.run(ctx);
    expect(ctx.confirmEmptyTrash).toHaveBeenCalled();
    expect(ctx.close).not.toHaveBeenCalled();
  });

  it("exposes one command per documented report", () => {
    for (const report of REPORT_COMMANDS) {
      expect(COMMANDS.some((c) => c.id === `report:${report.slug}`)).toBe(true);
    }
  });

  it("navigation commands navigate and close", () => {
    const ctx = makeCtx();
    const cmd = COMMANDS.find((c) => c.id === "nav:accounts")!;
    cmd.run(ctx);
    expect(ctx.close).toHaveBeenCalled();
    expect(ctx.navigate).toHaveBeenCalledWith("/accounts");
  });

  it("palette commands call setPalette with expected values", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "pref:palette-paper")!.run(ctx);
    expect(ctx.setPalette).toHaveBeenCalledWith("paper");
  });

  it("sign-out calls logout and redirects to /login", async () => {
    const ctx = makeCtx();
    await COMMANDS.find((c) => c.id === "pref:sign-out")!.run(ctx);
    expect(ctx.logout).toHaveBeenCalled();
    expect(ctx.navigate).toHaveBeenCalledWith("/login");
  });

  it("sign-out still navigates to /login when logout throws", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    const ctx = makeCtx({ logout: vi.fn(async () => { throw new Error("network"); }) });
    await COMMANDS.find((c) => c.id === "pref:sign-out")!.run(ctx);
    expect(ctx.logout).toHaveBeenCalled();
    expect(ctx.navigate).toHaveBeenCalledWith("/login");
    errorSpy.mockRestore();
  });

  it("shortcuts-help opens the help modal", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "pref:shortcuts-help")!.run(ctx);
    expect(ctx.openShortcutsHelp).toHaveBeenCalled();
  });

  it("report commands navigate with the correct ?report slug", () => {
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "report:duplicate-detection")!.run(ctx);
    expect(ctx.navigate).toHaveBeenCalledWith(
      "/reports?report=duplicate-detection",
    );
  });

  it("create:account dispatches shortcut:new-item when already on /accounts", () => {
    const dispatchSpy = vi.spyOn(window, "dispatchEvent");
    const ctx = makeCtx({ currentPath: "/accounts" });
    COMMANDS.find((c) => c.id === "create:account")!.run(ctx);
    expect(ctx.close).toHaveBeenCalled();
    expect(ctx.navigate).not.toHaveBeenCalled();
    const event = dispatchSpy.mock.calls.find(
      ([e]) => (e as CustomEvent).type === "shortcut:new-item",
    );
    expect(event).toBeDefined();
    dispatchSpy.mockRestore();
  });

  it("create:account navigates with openNew state when on a different page", () => {
    const dispatchSpy = vi.spyOn(window, "dispatchEvent");
    const ctx = makeCtx({ currentPath: "/" });
    COMMANDS.find((c) => c.id === "create:account")!.run(ctx);
    expect(ctx.close).toHaveBeenCalled();
    expect(ctx.navigate).toHaveBeenCalledWith("/accounts", {
      state: { openNew: true },
    });
    expect(
      dispatchSpy.mock.calls.some(
        ([e]) => (e as CustomEvent).type === "shortcut:new-item",
      ),
    ).toBe(false);
    dispatchSpy.mockRestore();
  });

  it("create:user navigates to /admin/users with openNew state", () => {
    const dispatchSpy = vi.spyOn(window, "dispatchEvent");
    const ctx = makeCtx({ currentPath: "/" });
    COMMANDS.find((c) => c.id === "create:user")!.run(ctx);
    expect(ctx.navigate).toHaveBeenCalledWith("/admin/users", {
      state: { openNew: true },
    });
    dispatchSpy.mockRestore();
  });

  it("create:receipt navigates directly (no event dispatch)", () => {
    const dispatchSpy = vi.spyOn(window, "dispatchEvent");
    const ctx = makeCtx();
    COMMANDS.find((c) => c.id === "create:receipt")!.run(ctx);
    expect(ctx.navigate).toHaveBeenCalledWith("/receipts/new");
    expect(
      dispatchSpy.mock.calls.some(
        ([e]) => (e as CustomEvent).type === "shortcut:new-item",
      ),
    ).toBe(false);
    dispatchSpy.mockRestore();
  });
});
