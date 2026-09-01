/// <reference types="node" />
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const cssPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../index.css",
);
const css = readFileSync(cssPath, "utf8");

describe("receipt table action visibility", () => {
  it("reveals compact row actions when keyboard focus is within the row", () => {
    const rule = css.match(
      /\.tbl\s+tbody\s+tr:focus-within\s+\.row-actions\s*\{([^}]*)\}/,
    );

    expect(
      rule,
      "missing the receipt row :focus-within reveal rule",
    ).not.toBeNull();
    expect(rule?.[1]).toMatch(/opacity\s*:\s*1\s*;/);
  });

  it("fits the table with border-box cells and bounded utility columns without ellipses", () => {
    expect(css).toMatch(
      /\.receipts-table\s*\{[^}]*table-layout\s*:\s*fixed\s*;/s,
    );
    expect(css).toMatch(
      /\.receipts-table th,\s*\.receipts-table td\s*\{[^}]*box-sizing\s*:\s*border-box\s*;/s,
    );
    const cardRule = css.match(/\.receipts-table-card\s*\{([^}]*)\}/);
    expect(cardRule?.[1]).toMatch(/container-type\s*:\s*inline-size\s*;/);
    expect(cardRule?.[1]).not.toMatch(
      /overflow-x\s*:\s*(?:auto|scroll|clip)\s*;/,
    );
    expect(css).toMatch(
      /\.receipts-table \.receipt-select\s*\{[^}]*width\s*:\s*44px\s*;[^}]*overflow\s*:\s*visible\s*;[^}]*text-overflow\s*:\s*clip\s*;/s,
    );
    expect(css).toMatch(
      /\.receipts-table \.receipt-actions\s*\{[^}]*width\s*:\s*92px\s*;[^}]*overflow\s*:\s*visible\s*;[^}]*text-overflow\s*:\s*clip\s*;/s,
    );
    expect(css).toMatch(
      /\.receipts-table col:nth-child\(3\)\s*\{\s*width\s*:\s*18%\s*;/,
    );
    expect(css).toMatch(
      /\.receipts-table col:nth-child\(6\)\s*\{\s*width\s*:\s*20%\s*;/,
    );
    expect(css).toMatch(
      /\.receipts-table \.row-actions\s*\{[^}]*flex-wrap\s*:\s*nowrap\s*;/s,
    );
  });

  it("collapses columns by table-container width and preserves the compact mobile grid", () => {
    const contentsRule = css.match(
      /@container\s*\(max-width:\s*1200px\)\s*\{([\s\S]*?)\n\}/,
    )?.[1];
    const secondaryRule = css.match(
      /@container\s*\(max-width:\s*900px\)\s*\{([\s\S]*?)\n\}/,
    )?.[1];
    const mobileRule = css.match(
      /@media\s*\(max-width:\s*640px\)\s*\{([\s\S]*?)\n\}/,
    )?.[1];

    expect(contentsRule).toMatch(
      /\.receipts-table col\.receipt-col-contents\s*\{[^}]*width\s*:\s*0\s*;[^}]*visibility\s*:\s*collapse\s*;/,
    );
    expect(contentsRule).not.toMatch(
      /\.receipts-table \.receipt-col-contents\s*\{[^}]*display\s*:\s*none/,
    );
    expect(secondaryRule).toMatch(
      /\.receipts-table col\.receipt-col-secondary\s*\{[^}]*width\s*:\s*0\s*;[^}]*visibility\s*:\s*collapse\s*;/,
    );
    expect(secondaryRule).not.toMatch(
      /\.receipts-table \.receipt-col-secondary\s*\{[^}]*display\s*:\s*none/,
    );
    expect(mobileRule).toMatch(
      /\.receipts-table \.receipt-col-secondary\s*\{[^}]*display\s*:\s*none\s*;/,
    );
    expect(css).toMatch(
      /\.receipts-table tr\s*\{\s*grid-template-columns\s*:\s*auto minmax\(0, 1fr\) auto\s*;/,
    );
  });

  it("rounds the visible header background to the receipts card", () => {
    expect(css).toMatch(
      /\.receipts-table thead th:first-child\s*\{[^}]*border-top-left-radius\s*:\s*var\(--radius\)\s*;/s,
    );
    expect(css).toMatch(
      /\.receipts-table thead th:last-child\s*\{[^}]*border-top-right-radius\s*:\s*var\(--radius\)\s*;/s,
    );
  });

  it("removes the final receipt divider in table and mobile-grid layouts", () => {
    expect(css).toMatch(
      /\.receipts-table tbody tr:last-child > td\s*\{[^}]*border-bottom\s*:\s*0\s*;/s,
    );
    expect(css).toMatch(
      /@media\s*\(max-width:\s*640px\)[\s\S]*?\.receipts-table tbody tr:last-child\s*\{[^}]*border-bottom\s*:\s*0\s*;/,
    );
  });

  it("gives the top and bottom pagination wrappers explicit breathing room", () => {
    expect(css).toMatch(
      /\.receipts-pagination-top\s*\{[^}]*margin-bottom\s*:\s*12px\s*;/s,
    );
    expect(css).toMatch(
      /\.receipts-pagination-bottom\s*\{[^}]*margin-top\s*:\s*12px\s*;/s,
    );
  });
});
