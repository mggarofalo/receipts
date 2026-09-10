// @vitest-environment node

import type { UserConfig } from "vite";

import viteConfig from "../../vite.config";

const proxyContexts = Object.keys(
  ((viteConfig as UserConfig).server?.proxy ?? {}) as Record<string, unknown>,
);

function matchesConfiguredProxy(path: string): boolean {
  return proxyContexts.some((context) =>
    context.startsWith("^") ? new RegExp(context).test(path) : path.startsWith(context),
  );
}

describe("Vite development proxy routes", () => {
  it.each([
    ["/api", true],
    ["/api?version=1", true],
    ["/api/receipts", true],
    ["/api-keys", false],
    ["/hubs", true],
    ["/hubs/entities", true],
  ])("matches %s: %s", (path, expected) => {
    expect(matchesConfiguredProxy(path)).toBe(expected);
  });
});
