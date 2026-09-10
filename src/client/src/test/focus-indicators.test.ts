/// <reference types="node" />
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const cssPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../index.css",
);
const css = readFileSync(cssPath, "utf8");

describe("focus indicators", () => {
  it("defines a single global focus-visible rule", () => {
    const cssWithoutComments = css.replace(/\/\*[\s\S]*?\*\//g, "");
    const focusVisibleSelectors =
      cssWithoutComments.match(/[^{}]*:focus-visible[^{}]*\{/g) ?? [];

    expect(focusVisibleSelectors.map((selector) => selector.trim())).toEqual([
      ":focus-visible {",
    ]);
    expect(css).toMatch(
      /:focus-visible\s*\{[^}]*outline:\s*2px solid var\(--accent\);[^}]*outline-offset:\s*2px;/s,
    );
  });
});
