import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { auditProject } from "./audit-dead-code.mjs";

function fixture(files, run) {
  const root = mkdtempSync(path.join(tmpdir(), "receipts-reachability-"));
  try {
    const contents = {
      "tsconfig.app.json": JSON.stringify({
        compilerOptions: {
          noLib: true,
          module: "esnext",
          moduleResolution: "bundler",
          baseUrl: ".",
          paths: { "@/*": ["src/*"] },
        },
        include: ["src"],
      }),
      ...files,
    };
    for (const [file, content] of Object.entries(contents)) {
      const target = path.join(root, file);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, content);
    }
    run(auditProject({ root, entryPoints: ["src/main.ts"] }));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("follows aliases, reexports, side effects, literal lazy imports and type-only dependencies", () => {
  fixture(
    {
      "src/main.ts":
        'import { value } from "@/barrel"; import "./effect"; import type { Shape } from "./shape"; const lazy = () => import("./lazy"); const x: Shape = { value }; lazy();',
      "src/barrel.ts": 'export { value } from "./value";',
      "src/value.ts": "export const value = 1;",
      "src/effect.ts": "globalThis.called = true;",
      "src/shape.ts": "export interface Shape { value: number }",
      "src/lazy.ts": "export default function Lazy() {}",
    },
    (result) => {
      assert.deepEqual(result.unreachableFiles, []);
      assert.deepEqual(result.unknownEdges, []);
      assert.deepEqual(result.unusedExports, []);
    },
  );
});

test("test imports and disconnected mutually recursive modules are not application roots", () => {
  fixture(
    {
      "src/main.ts": "export {};",
      "src/left.ts":
        'import { right } from "./right"; export function left() { return right(); }',
      "src/right.ts":
        'import { left } from "./left"; export function right() { return left(); }',
      "src/only-tested.ts": "export const value = 1;",
      "src/only-tested.test.ts":
        'import { value } from "./only-tested"; value;',
      "src/test/setup.ts": 'import "../left";',
    },
    (result) => {
      assert.deepEqual(result.unreachableFiles, [
        "src/left.ts",
        "src/only-tested.ts",
        "src/right.ts",
      ]);
    },
  );
});

test("identifies test-only exports while preserving referenced and type-used exports", () => {
  fixture(
    {
      "src/main.ts": 'import { live } from "./lib"; live();',
      "src/lib.ts": "export function live() {} export function dead() {}",
      "src/lib.test.ts": 'import { dead } from "./lib"; dead();',
    },
    (result) => {
      assert.deepEqual(result.unusedExports, [
        { file: "src/lib.ts", name: "dead" },
      ]);
    },
  );
});

test("reports unresolved imports and unknown loaders instead of claiming a closed graph", () => {
  fixture(
    {
      "src/main.ts":
        'import "./missing"; import(name); require(name); import.meta.glob("./*.ts");',
    },
    (result) => {
      assert.equal(result.unknownEdges.length, 4);
      assert.match(result.unknownEdges.join("\n"), /unresolved.*missing/);
      assert.match(result.unknownEdges.join("\n"), /computed import/);
      assert.match(result.unknownEdges.join("\n"), /require/);
      assert.match(result.unknownEdges.join("\n"), /glob/);
    },
  );
});

test("missing entry points are errors and declarations are not runtime deletion candidates", () => {
  fixture({ "src/env.d.ts": "declare const version: string;" }, (result) => {
    assert.deepEqual(result.unreachableFiles, []);
    assert.match(result.unknownEdges[0], /missing entry point/);
  });
});

test("namespace enumeration and lazy barrels retain their exported targets", () => {
  fixture(
    {
      "src/main.ts":
        'import * as api from "./namespace"; Object.values(api); const load = () => import("./barrel"); load();',
      "src/namespace.ts": "export function enumerated() {}",
      "src/barrel.ts": 'export { lazyTarget } from "./target";',
      "src/target.ts": "export function lazyTarget() {}",
    },
    (result) => {
      assert.deepEqual(result.unusedExports, []);
      assert.deepEqual(result.unreachableFiles, []);
    },
  );
});

test("named namespace reexports preserve dynamically selected members", () => {
  fixture(
    {
      "src/main.ts": 'import { api } from "./barrel"; api[name]();',
      "src/barrel.ts": 'export * as api from "./lib";',
      "src/lib.ts": "export function selected() {}",
    },
    (result) => assert.deepEqual(result.unusedExports, []),
  );
});

test("unanalysed local JavaScript modules block the closed graph", () => {
  fixture(
    {
      "src/main.ts": 'import "./bridge.js";',
      "src/bridge.js": 'import "./target";',
      "src/target.ts": "export const live = 1;",
    },
    (result) =>
      assert.match(result.unknownEdges.join("\n"), /unanalysed source/),
  );
});
