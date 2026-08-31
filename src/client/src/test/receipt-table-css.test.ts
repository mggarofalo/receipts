/// <reference types="node" />
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const cssPath = resolve(dirname(fileURLToPath(import.meta.url)), "../index.css");
const css = readFileSync(cssPath, "utf8");

describe("receipt table action visibility", () => {
  it("reveals compact row actions when keyboard focus is within the row", () => {
    const rule = css.match(
      /\.tbl\s+tbody\s+tr:focus-within\s+\.row-actions\s*\{([^}]*)\}/,
    );

    expect(rule, "missing the receipt row :focus-within reveal rule").not.toBeNull();
    expect(rule?.[1]).toMatch(/opacity\s*:\s*1\s*;/);
  });
});
