/// <reference types="node" />
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { render, screen } from "@testing-library/react";
import { Command, CommandInput } from "@/components/ui/command";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";

const testDirectory = dirname(fileURLToPath(import.meta.url));
const cssPath = resolve(testDirectory, "../index.css");
const css = readFileSync(cssPath, "utf8");
const calendarSource = readFileSync(
  resolve(testDirectory, "../components/ui/calendar.tsx"),
  "utf8",
);

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

  it("does not give focused calendar days a second ring or border", () => {
    expect(calendarSource).not.toMatch(
      /group-data-\[focused=true\]\/day:(?:border|ring)/,
    );
  });

  it("gives the command input enough internal clearance for the global outline", () => {
    render(
      <Command>
        <CommandInput aria-label="Search commands" />
      </Command>,
    );

    const input = screen.getByRole("combobox");
    expect(input).toHaveClass("h-full");
    expect(input.parentElement).toHaveClass("py-1");
  });

  it("gives tab triggers enough list padding for the global outline", () => {
    render(
      <Tabs defaultValue="first">
        <TabsList aria-label="Example tabs">
          <TabsTrigger value="first">First</TabsTrigger>
        </TabsList>
      </Tabs>,
    );

    const tabList = screen.getByRole("tablist", { name: "Example tabs" });
    expect(tabList).toHaveClass("p-1");
    expect(tabList).not.toHaveClass("p-[3px]");
  });
});
