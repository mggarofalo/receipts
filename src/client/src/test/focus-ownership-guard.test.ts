/// <reference types="node" />
import { readFileSync, readdirSync } from "node:fs";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const testDirectory = dirname(fileURLToPath(import.meta.url));
const uiDirectory = resolve(testDirectory, "../components/ui");

const calendarProxyAllowlist = new Set([
  "calendar.tsx:has-focus:border-ring",
  "calendar.tsx:has-focus:ring-ring/50",
  "calendar.tsx:has-focus:ring-[3px]",
]);

function productionSourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return productionSourceFiles(path);
    if (![".ts", ".tsx"].includes(extname(entry.name))) return [];
    if (/\.(?:test|spec|stories)\.[^.]+$/.test(entry.name)) return [];
    if (entry.name.endsWith(".d.ts")) return [];
    return [path];
  });
}

function ownsOrSuppressesFocusIndicator(token: string): boolean {
  if (/(?:^|:|!)outline-(?:none|hidden)$/.test(token)) return true;

  const focusOwnedUtility =
    /(?:^|:)(?:focus|focus-visible|focus-within|has-focus):(?:[^\s:]+:)*(?:ring(?:-|$)|border(?:-|$)|outline(?:-|$))/;
  if (focusOwnedUtility.test(token)) return true;

  return (
    token.includes("focused=true") &&
    /:(?:ring(?:-|$)|border(?:-|$)|outline(?:-|$))/.test(token)
  );
}

function sourceTokens(path: string, source: string) {
  const sourceFile = ts.createSourceFile(
    path,
    source,
    ts.ScriptTarget.Latest,
    true,
    path.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const tokens: Array<{ line: number; token: string }> = [];

  function visit(node: ts.Node) {
    if (ts.isStringLiteralLike(node)) {
      const startLine = sourceFile.getLineAndCharacterOfPosition(node.getStart()).line;
      node.text.split("\n").forEach((line, lineOffset) => {
        line.split(/\s+/).forEach((token) => {
          if (token) tokens.push({ line: startLine + lineOffset + 1, token });
        });
      });
    }
    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return tokens;
}

describe("shadcn focus ownership guard", () => {
  it.each([
    "focus:ring-2",
    "focus-visible:border-ring",
    "dark:focus-visible:ring-ring/50",
    "focus-visible:outline-1",
    "focus:outline-none",
    "outline-hidden",
    "has-focus:ring-[3px]",
    "group-data-[focused=true]/day:border-ring",
  ])("recognizes forbidden focus utility %s", (utility) => {
    expect(ownsOrSuppressesFocusIndicator(utility)).toBe(true);
  });

  it.each([
    "ring-1",
    "border-ring",
    "aria-invalid:ring-destructive/20",
    "data-[state=active]:ring-2",
    "outline-ring/50",
  ])("allows non-focus decorative utility %s", (utility) => {
    expect(ownsOrSuppressesFocusIndicator(utility)).toBe(false);
  });

  it("keeps focus indication owned by the global rule", () => {
    const violations = productionSourceFiles(uiDirectory).flatMap((path) => {
      const source = readFileSync(path, "utf8");
      const file = relative(uiDirectory, path);

      return sourceTokens(path, source)
        .filter(({ token }) => ownsOrSuppressesFocusIndicator(token))
        .filter(({ token }) => !calendarProxyAllowlist.has(`${file}:${token}`))
        .map(({ line, token }) => `${file}:${line} ${token}`);
    });

    expect(
      violations,
      "Global :focus-visible in index.css owns focus indication. " +
        "shadcn CLI regeneration can reintroduce forbidden component ring, " +
        "border, or outline-suppression utilities; remove these tokens or " +
        "document a narrowly scoped exception.",
    ).toEqual([]);
  });
});
