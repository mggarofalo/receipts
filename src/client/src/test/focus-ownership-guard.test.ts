/// <reference types="node" />
import { readFileSync, readdirSync } from "node:fs";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const testDirectory = dirname(fileURLToPath(import.meta.url));
const uiDirectory = resolve(testDirectory, "../components/ui");

const calendarProxyUtilities = new Set([
  "has-focus:border-ring",
  "has-focus:ring-ring/50",
  "has-focus:ring-[3px]",
]);

type SourceToken = {
  line: number;
  token: string;
  propertyName?: string;
};

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
  const sections: string[] = [];
  let sectionStart = 0;
  let bracketDepth = 0;
  for (let index = 0; index < token.length; index += 1) {
    if (token[index] === "[") bracketDepth += 1;
    if (token[index] === "]") bracketDepth -= 1;
    if (token[index] === ":" && bracketDepth === 0) {
      sections.push(token.slice(sectionStart, index));
      sectionStart = index + 1;
    }
  }
  sections.push(token.slice(sectionStart));

  const utility = sections.at(-1)?.replace(/^!|!$/g, "") ?? "";
  if (/^outline-(?:none|hidden)$/.test(utility)) return true;
  if (/^\[outline(?:-[^:]+)?:none\]$/.test(utility)) return true;

  const isIndicatorUtility =
    /^(?:ring|border|outline)(?:-|$)/.test(utility) ||
    /^\[(?:box-shadow|outline(?:-[^:]+)?|border(?:-[^:]+)?):/.test(
      utility,
    );
  if (!isIndicatorUtility) return false;

  return sections.slice(0, -1).some(
    (variant) =>
      /^(?:focus|focus-visible|focus-within|has-focus|has-focus-visible|has-focus-within)$/.test(
        variant,
      ) ||
      /^(?:group|peer|in)-focus(?:-visible|-within)?(?:\/[^:]+)?$/.test(
        variant,
      ) ||
      /^(?:group|peer)-has-focus(?:-visible|-within)?(?:\/[^:]+)?$/.test(
        variant,
      ) ||
      variant.includes(":focus") ||
      variant.includes("focused=true"),
  );
}

function sourceTokens(path: string, source: string): SourceToken[] {
  const sourceFile = ts.createSourceFile(
    path,
    source,
    ts.ScriptTarget.Latest,
    true,
    path.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const tokens: SourceToken[] = [];

  function propertyName(node: ts.Node): string | undefined {
    let ancestor = node.parent;
    while (ancestor && !ts.isPropertyAssignment(ancestor)) {
      ancestor = ancestor.parent;
    }
    return ancestor && ts.isPropertyAssignment(ancestor)
      ? ancestor.name.getText(sourceFile).replace(/["']/g, "")
      : undefined;
  }

  function recordLiteral(node: ts.TemplateLiteralLikeNode | ts.StringLiteral) {
    const startLine = sourceFile.getLineAndCharacterOfPosition(node.getStart()).line;
    node.text.split("\n").forEach((line, lineOffset) => {
      line.split(/\s+/).forEach((token) => {
        if (token) {
          tokens.push({
            line: startLine + lineOffset + 1,
            token,
            propertyName: propertyName(node),
          });
        }
      });
    });
  }

  function visit(node: ts.Node) {
    if (ts.isTemplateExpression(node)) {
      recordLiteral(node.head);
      node.templateSpans.forEach((span) => {
        visit(span.expression);
        recordLiteral(span.literal);
      });
      return;
    }
    if (ts.isStringLiteralLike(node)) {
      recordLiteral(node);
    }
    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return tokens;
}

function focusViolations(file: string, path: string, source: string): string[] {
  const allowedOccurrences = new Map<string, number>();

  return sourceTokens(path, source)
    .filter(({ token }) => ownsOrSuppressesFocusIndicator(token))
    .filter(({ token, propertyName }) => {
      const isCalendarProxy =
        file === "calendar.tsx" &&
        propertyName === "dropdown_root" &&
        calendarProxyUtilities.has(token);
      if (!isCalendarProxy) return true;

      const occurrences = allowedOccurrences.get(token) ?? 0;
      allowedOccurrences.set(token, occurrences + 1);
      return occurrences >= 1;
    })
    .map(({ line, token }) => `${file}:${line} ${token}`);
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
    "group-focus:ring-2",
    "group-focus-visible:ring-2",
    "peer-focus:border-ring",
    "peer-focus-visible:border-ring",
    "in-focus:ring-2",
    "sm:group-focus:ring-2",
    "[&:focus-visible]:outline-2",
    "focus-visible:!ring-[3px]",
    "!outline-none",
    "outline-none!",
    "outline-hidden!",
    "focus-visible:ring-[3px]!",
    "has-focus-visible:ring-2",
    "has-focus-within:border-ring",
    "group-has-focus:ring-2",
    "group-has-focus-visible:ring-2",
    "peer-has-focus-within:border-ring",
    "[outline:none]",
    "![outline:none]",
    "[outline:none]!",
    "[&:focus-visible]:[box-shadow:0_0_0_2px_red]",
    "[&:focus]:[outline:2px_solid_red]",
    "group-focus:[border-color:red]",
  ])("recognizes forbidden focus utility %s", (utility) => {
    expect(ownsOrSuppressesFocusIndicator(utility)).toBe(true);
  });

  it.each([
    "ring-1",
    "border-ring",
    "aria-invalid:ring-destructive/20",
    "data-[state=active]:ring-2",
    "outline-ring/50",
    "[box-shadow:0_0_0_2px_red]",
    "[outline:2px_solid_red]",
    "[border-color:red]",
    "hover:[box-shadow:0_0_0_2px_red]",
  ])("allows non-focus decorative utility %s", (utility) => {
    expect(ownsOrSuppressesFocusIndicator(utility)).toBe(false);
  });

  it("scans every static segment of an interpolated template literal", () => {
    const source = `
      const classes = \`group-focus:ring-2 sm:group-focus:ring-2 has-focus-visible:ring-2 group-has-focus:ring-2 \${first} peer-focus-visible:border-ring focus-visible:!ring-[3px] focus-visible:ring-[3px]! \${second} [&:focus-visible]:outline-2 [&:focus]:[box-shadow:0_0_0_2px_red] [outline:none]!\`;
    `;

    expect(focusViolations("fixture.ts", "fixture.ts", source)).toEqual([
      "fixture.ts:2 group-focus:ring-2",
      "fixture.ts:2 sm:group-focus:ring-2",
      "fixture.ts:2 has-focus-visible:ring-2",
      "fixture.ts:2 group-has-focus:ring-2",
      "fixture.ts:2 peer-focus-visible:border-ring",
      "fixture.ts:2 focus-visible:!ring-[3px]",
      "fixture.ts:2 focus-visible:ring-[3px]!",
      "fixture.ts:2 [&:focus-visible]:outline-2",
      "fixture.ts:2 [&:focus]:[box-shadow:0_0_0_2px_red]",
      "fixture.ts:2 [outline:none]!",
    ]);
  });

  it("allows each calendar dropdown proxy utility once and only in dropdown_root", () => {
    const source = `
      const classes = {
        dropdown_root: cn("has-focus:border-ring has-focus:ring-ring/50 has-focus:ring-[3px] has-focus:border-ring"),
        day: "has-focus:ring-ring/50",
      };
    `;

    expect(focusViolations("calendar.tsx", "calendar.tsx", source)).toEqual([
      "calendar.tsx:3 has-focus:border-ring",
      "calendar.tsx:4 has-focus:ring-ring/50",
    ]);
  });

  it("keeps focus indication owned by the global rule", () => {
    const violations = productionSourceFiles(uiDirectory).flatMap((path) => {
      const source = readFileSync(path, "utf8");
      const file = relative(uiDirectory, path);

      return focusViolations(file, path, source);
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
