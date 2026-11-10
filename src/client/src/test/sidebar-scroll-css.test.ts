/// <reference types="node" />
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const cssPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../index.css",
);
const css = readFileSync(cssPath, "utf8");

/** Body of the top-level rule whose selector is exactly `selector`. */
function ruleBody(selector: string): string | undefined {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return css.match(new RegExp(`(?:^|\\n)${escaped}\\s*\\{([^}]*)\\}`))?.[1];
}

describe("sidebar scroll layout", () => {
  // RECEIPTS-1114: a viewport-pinned sidebar with no scroll region cut off
  // the nav entries below the fold.
  it("pins the sidebar to the viewport as a flex column", () => {
    const sidebar = ruleBody(".sidebar");

    expect(sidebar, "missing the .sidebar rule").toBeDefined();
    expect(sidebar).toMatch(/position\s*:\s*sticky\s*;/);
    expect(sidebar).toMatch(/height\s*:\s*100vh\s*;/);
    expect(sidebar).toMatch(/display\s*:\s*flex\s*;/);
    expect(sidebar).toMatch(/flex-direction\s*:\s*column\s*;/);
  });

  it("lets the nav region shrink and scroll inside the sidebar", () => {
    const nav = ruleBody(".sidebar-nav");

    expect(nav, "missing the .sidebar-nav rule").toBeDefined();
    expect(nav).toMatch(/(?:^|[;\s])flex\s*:\s*1\s*;/);
    expect(nav).toMatch(/min-height\s*:\s*0\s*;/);
    expect(nav).toMatch(/overflow-y\s*:\s*auto\s*;/);
  });

  it("insets the scroll region so focus rings on its edge items stay visible", () => {
    const nav = ruleBody(".sidebar-nav");

    expect(nav).toMatch(/margin\s*:\s*0\s+-4px\s*;/);
    expect(nav).toMatch(/padding\s*:\s*0\s+4px\s+4px\s*;/);
    expect(nav).toMatch(/scroll-padding-block\s*:\s*4px\s*;/);
  });
});
