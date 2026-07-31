/**
 * ESLint rule: form field rows must wrap, never shrink.
 *
 * The bug this prevents: a row of inputs laid out with `grid grid-cols-N`
 * divides the container into fixed tracks. As the viewport narrows the tracks
 * narrow with it — they never wrap — so a control whose min-content width
 * exceeds its track renders at that intrinsic width, spills out of its own
 * box, crosses the row gap, and lands on top of the neighbouring field.
 *
 * The sanctioned pattern preserves both the gap and each field's minimum
 * usable width, and overflows *vertically* instead:
 *
 *   <div className="flex flex-wrap gap-4">
 *     <FormField … render={() => <FormItem className="min-w-[200px] flex-1">…} />
 *     <FormField … render={() => <FormItem className="min-w-[110px]">…} />
 *   </div>
 *
 * Runtime counterpart: src/client/tests/visual/field-overlap.spec.ts sweeps
 * real viewport widths and asserts no two controls ever intersect. This rule
 * is the compile-time half — it stops the pattern being written at all.
 */

const FIELD_COMPONENTS = new Set(["FormField", "FormItem"]);

/** Strip Tailwind variant prefixes (`sm:`, `hover:`, …), ignoring colons inside `[]`. */
function baseUtility(token) {
  let depth = 0;
  let lastColon = -1;
  for (let i = 0; i < token.length; i++) {
    const ch = token[i];
    if (ch === "[") depth++;
    else if (ch === "]") depth--;
    else if (ch === ":" && depth === 0) lastColon = i;
  }
  return lastColon === -1 ? token : token.slice(lastColon + 1);
}

/** Pull every statically-known string out of a className expression. */
function collectStrings(expr, out) {
  if (!expr) return;
  switch (expr.type) {
    case "Literal":
      if (typeof expr.value === "string") out.push(expr.value);
      break;
    case "TemplateLiteral":
      for (const q of expr.quasis) out.push(q.value.cooked ?? "");
      for (const e of expr.expressions) collectStrings(e, out);
      break;
    // cn(...) / clsx(...) and friends
    case "CallExpression":
      for (const a of expr.arguments) collectStrings(a, out);
      break;
    case "LogicalExpression":
      collectStrings(expr.left, out);
      collectStrings(expr.right, out);
      break;
    case "ConditionalExpression":
      collectStrings(expr.consequent, out);
      collectStrings(expr.alternate, out);
      break;
    case "ArrayExpression":
      for (const e of expr.elements) collectStrings(e, out);
      break;
    case "ObjectExpression":
      for (const p of expr.properties) {
        if (p.type === "Property" && p.key) {
          if (p.key.type === "Literal" && typeof p.key.value === "string") out.push(p.key.value);
          else if (p.key.type === "Identifier") out.push(p.key.name);
        }
      }
      break;
    default:
      break;
  }
}

function elementName(el) {
  const name = el.openingElement?.name;
  if (!name) return "";
  if (name.type === "JSXIdentifier") return name.name;
  if (name.type === "JSXMemberExpression") return name.property?.name ?? "";
  return "";
}

/** → { attr, tokens } for an element's className, or null when absent/dynamic-only. */
function readClassName(el) {
  const attr = el.openingElement?.attributes?.find(
    (a) => a.type === "JSXAttribute" && a.name?.name === "className",
  );
  if (!attr || !attr.value) return null;

  const parts = [];
  if (attr.value.type === "Literal") collectStrings(attr.value, parts);
  else if (attr.value.type === "JSXExpressionContainer") collectStrings(attr.value.expression, parts);

  const tokens = parts
    .join(" ")
    .split(/\s+/)
    .filter(Boolean)
    .map(baseUtility);
  return { attr, tokens };
}

/** Field components rendered directly inside `el` (through conditionals/maps too). */
function fieldChildren(el) {
  const found = [];

  const visitExpr = (expr) => {
    if (!expr) return;
    switch (expr.type) {
      case "JSXElement":
        visitNode(expr);
        break;
      case "LogicalExpression":
        visitExpr(expr.right);
        break;
      case "ConditionalExpression":
        visitExpr(expr.consequent);
        visitExpr(expr.alternate);
        break;
      case "CallExpression":
        // `items.map(x => <FormField …/>)` — one source position, but it
        // renders as many siblings, so it counts as a multi-field row.
        for (const arg of expr.arguments) {
          if (arg.type === "ArrowFunctionExpression" || arg.type === "FunctionExpression") {
            visitExpr(arg.body?.type === "BlockStatement" ? null : arg.body);
          }
        }
        break;
      default:
        break;
    }
  };

  const visitNode = (child) => {
    if (!child) return;
    if (child.type === "JSXElement") {
      if (FIELD_COMPONENTS.has(elementName(child))) found.push(child);
      return;
    }
    if (child.type === "JSXExpressionContainer") visitExpr(child.expression);
  };

  for (const child of el.children ?? []) visitNode(child);
  return found;
}

function isMultiColumnGrid(tokens) {
  return tokens.some((t) => {
    if (!t.startsWith("grid-cols-")) return false;
    const value = t.slice("grid-cols-".length);
    if (value === "1") return false; // a single column can't collide
    return true; // numeric >1, or an arbitrary track list we can't verify
  });
}

/** @type {import("eslint").Rule.RuleModule} */
export default {
  meta: {
    type: "problem",
    docs: {
      description:
        "Form field rows must use `flex flex-wrap` with a per-field `min-w-[…]`, so fields wrap to a new row instead of shrinking into each other.",
    },
    schema: [],
    messages: {
      gridRow:
        "Field row uses `{{cls}}`. Grid tracks shrink but never wrap, so a control wider than its track overflows onto the next field. Use `flex flex-wrap gap-4` and give each field a `min-w-[…]` instead.",
      missingMinWidth:
        "`<FormItem>` in a wrapping field row needs a `min-w-[…]` so the row breaks to a new line instead of squeezing this field. Add e.g. `className=\"min-w-[200px] flex-1\"`.",
    },
  },

  create(context) {
    const sourceCode = context.sourceCode ?? context.getSourceCode();

    return {
      JSXElement(node) {
        // --- Rule A: no multi-column grid for rows holding 2+ fields.
        const cls = readClassName(node);
        if (cls && isMultiColumnGrid(cls.tokens) && fieldChildren(node).length >= 2) {
          context.report({
            node: cls.attr,
            messageId: "gridRow",
            data: { cls: cls.tokens.filter((t) => t.startsWith("grid-cols-")).join(" ") },
          });
        }

        // --- Rule B: every field in a wrapping multi-field row declares a min width.
        if (elementName(node) !== "FormItem") return;

        const ancestors = sourceCode.getAncestors(node);
        let row = null;
        for (let i = ancestors.length - 1; i >= 0; i--) {
          const a = ancestors[i];
          if (a.type !== "JSXElement") continue;
          const c = readClassName(a);
          if (!c) continue;
          if (c.tokens.includes("flex-wrap") || c.tokens.some((t) => t.startsWith("grid-cols-"))) {
            row = { el: a, tokens: c.tokens };
            break;
          }
        }
        if (!row || !row.tokens.includes("flex-wrap")) return;
        if (fieldChildren(row.el).length < 2) return;

        const own = readClassName(node);
        if (own && own.tokens.some((t) => t.startsWith("min-w-"))) return;

        context.report({ node: node.openingElement, messageId: "missingMinWidth" });
      },
    };
  },
};
