import { RuleTester } from "eslint";
import tsparser from "@typescript-eslint/parser";
import rule from "./field-row-layout.js";

const ruleTester = new RuleTester({
  languageOptions: {
    parser: tsparser,
    ecmaVersion: 2022,
    sourceType: "module",
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
});

ruleTester.run("field-row-layout", rule, {
  valid: [
    // The sanctioned pattern.
    {
      code: `
        const A = () => (
          <div className="flex flex-wrap gap-4">
            <FormField render={() => <FormItem className="min-w-[200px] flex-1"><Input /></FormItem>} />
            <FormField render={() => <FormItem className="min-w-[110px]"><Input /></FormItem>} />
          </div>
        );`,
    },
    // A single-column grid can't collide.
    {
      code: `
        const A = () => (
          <div className="grid grid-cols-1 gap-4">
            <FormField render={() => <FormItem><Input /></FormItem>} />
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
    },
    // Grid layout that holds no fields is none of this rule's business —
    // e.g. the page shell splitting form column from sidebar.
    {
      code: `
        const A = () => (
          <div className="grid grid-cols-[minmax(0,1fr)_360px] gap-6">
            <section />
            <aside />
          </div>
        );`,
    },
    // A lone field in a wrapping row needs no min-width: nothing to collide with.
    {
      code: `
        const A = () => (
          <div className="flex flex-wrap gap-4">
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
    },
    // Non-field flex-wrap rows (badges, chips) are untouched.
    {
      code: `
        const A = () => (
          <div className="flex flex-wrap gap-2">
            <Badge />
            <Badge />
          </div>
        );`,
    },
  ],

  invalid: [
    // The exact shape that produced the Location/Date overlap.
    {
      code: `
        const A = () => (
          <div className="grid grid-cols-2 gap-4">
            <FormField render={() => <FormItem><Input /></FormItem>} />
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
      errors: [{ messageId: "gridRow" }],
    },
    // Responsive multi-column counts too — it collides above the breakpoint.
    {
      code: `
        const A = () => (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <FormField render={() => <FormItem><Input /></FormItem>} />
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
      errors: [{ messageId: "gridRow" }],
    },
    // Arbitrary track lists are unverifiable, so they're rejected for field rows.
    {
      code: `
        const A = () => (
          <div className="grid grid-cols-[1fr_auto_auto] gap-4">
            <FormField render={() => <FormItem><Input /></FormItem>} />
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
      errors: [{ messageId: "gridRow" }],
    },
    // Right container, but fields can still be squeezed without a min width.
    {
      code: `
        const A = () => (
          <div className="flex flex-wrap gap-4">
            <FormField render={() => <FormItem className="flex-1"><Input /></FormItem>} />
            <FormField render={() => <FormItem className="flex-1"><Input /></FormItem>} />
          </div>
        );`,
      errors: [{ messageId: "missingMinWidth" }, { messageId: "missingMinWidth" }],
    },
    // className built through cn() is still inspected.
    {
      code: `
        const A = () => (
          <div className={cn("grid grid-cols-2 gap-4", className)}>
            <FormField render={() => <FormItem><Input /></FormItem>} />
            <FormField render={() => <FormItem><Input /></FormItem>} />
          </div>
        );`,
      errors: [{ messageId: "gridRow" }],
    },
  ],
});
