import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const normalize = (file) => path.resolve(file).replaceAll("\\", "/");

/** A conservative source graph, not proof about arbitrary runtime reflection. */
export function auditProject({ root, entryPoints }) {
  root = normalize(root);
  const config = ts.readConfigFile(
    path.join(root, "tsconfig.app.json"),
    ts.sys.readFile,
  );
  if (config.error)
    throw new Error(
      ts.flattenDiagnosticMessageText(config.error.messageText, "\n"),
    );
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, root);
  if (parsed.errors.some((error) => error.code !== 18003)) {
    throw new Error(
      parsed.errors
        .map((error) =>
          ts.flattenDiagnosticMessageText(error.messageText, "\n"),
        )
        .join("\n"),
    );
  }
  const program = ts.createProgram(parsed.fileNames, parsed.options);
  const checker = program.getTypeChecker();
  const relative = (file) => path.posix.relative(root, normalize(file));
  const production = (file) =>
    normalize(file.fileName).startsWith(root + "/src/") &&
    !file.isDeclarationFile &&
    !/\.test\.[cm]?[tj]sx?$|\/test\//.test(normalize(file.fileName));
  const files = program.getSourceFiles().filter(production);
  const graph = new Map();
  const unknownEdges = [];
  const dynamicModules = new Set();
  for (const file of files) {
    const dependencies = [];
    function visit(node) {
      let specifier;
      let dynamic = false;
      if (
        (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
        node.moduleSpecifier &&
        ts.isStringLiteral(node.moduleSpecifier)
      ) {
        specifier = node.moduleSpecifier.text;
      }
      if (ts.isCallExpression(node)) {
        if (node.expression.kind === ts.SyntaxKind.ImportKeyword) {
          dynamic = true;
          if (ts.isStringLiteral(node.arguments[0]))
            specifier = node.arguments[0].text;
          else unknownEdges.push(`${relative(file.fileName)}: computed import`);
        } else if (
          ts.isIdentifier(node.expression) &&
          node.expression.text === "require"
        ) {
          unknownEdges.push(
            `${relative(file.fileName)}: require requires explicit analysis`,
          );
        } else if (
          ts.isPropertyAccessExpression(node.expression) &&
          ["glob", "globEager"].includes(node.expression.name.text)
        ) {
          unknownEdges.push(
            `${relative(file.fileName)}: glob requires explicit analysis`,
          );
        }
      }
      if (specifier) {
        const resolved = ts.resolveModuleName(
          specifier,
          file.fileName,
          parsed.options,
          ts.sys,
        ).resolvedModule;
        if (resolved) {
          const target = normalize(resolved.resolvedFileName);
          dependencies.push(target);
          if (dynamic) dynamicModules.add(target);
        } else if (
          !(
            specifier.startsWith(".") &&
            /\.(css|svg|png|jpg|jpeg)$/.test(specifier) &&
            fs.existsSync(path.resolve(path.dirname(file.fileName), specifier))
          )
        ) {
          unknownEdges.push(
            `${relative(file.fileName)}: unresolved ${specifier}`,
          );
        }
      }
      ts.forEachChild(node, visit);
    }
    visit(file);
    graph.set(normalize(file.fileName), dependencies);
  }
  const reachable = new Set();
  function walk(file) {
    if (reachable.has(file)) return;
    reachable.add(file);
    if (
      file.startsWith(root + "/src/") &&
      !file.endsWith(".d.ts") &&
      !graph.has(file)
    )
      unknownEdges.push(`unanalysed source: ${relative(file)}`);
    for (const dependency of graph.get(file) ?? []) walk(dependency);
  }
  for (const entry of entryPoints) {
    const file = normalize(path.join(root, entry));
    if (!graph.has(file)) unknownEdges.push(`missing entry point: ${entry}`);
    else walk(file);
  }
  const resolveAlias = (symbol) =>
    symbol && symbol.flags & ts.SymbolFlags.Alias
      ? checker.getAliasedSymbol(symbol)
      : symbol;
  const referenced = new Set();
  function preserveExports(symbol, seen = new Set()) {
    symbol = resolveAlias(symbol);
    if (!symbol || seen.has(symbol)) return;
    seen.add(symbol);
    for (const exported of checker.getExportsOfModule(symbol)) {
      const target = resolveAlias(exported);
      referenced.add(target);
      if (target.flags & ts.SymbolFlags.Module) preserveExports(target, seen);
    }
  }
  for (const file of files.filter((file) =>
    reachable.has(normalize(file.fileName)),
  )) {
    if (dynamicModules.has(normalize(file.fileName)))
      preserveExports(checker.getSymbolAtLocation(file));
    for (const statement of file.statements) {
      if (
        ts.isImportDeclaration(statement) &&
        statement.importClause?.namedBindings &&
        ts.isNamespaceImport(statement.importClause.namedBindings)
      ) {
        preserveExports(
          checker.getSymbolAtLocation(
            statement.importClause.namedBindings.name,
          ),
        );
      }
    }
  }
  for (const file of files.filter((file) =>
    reachable.has(normalize(file.fileName)),
  )) {
    function visit(node) {
      if (ts.isIdentifier(node)) {
        const symbol = resolveAlias(
          ts.isShorthandPropertyAssignment(node.parent)
            ? checker.getShorthandAssignmentValueSymbol(node.parent)
            : checker.getSymbolAtLocation(node),
        );
        if (
          symbol &&
          !symbol.declarations?.some(
            (declaration) => declaration.name === node,
          ) &&
          !ts.isImportSpecifier(node.parent) &&
          !ts.isExportSpecifier(node.parent)
        ) {
          referenced.add(symbol);
          if (symbol.flags & ts.SymbolFlags.Module) preserveExports(symbol);
        }
      }
      ts.forEachChild(node, visit);
    }
    visit(file);
  }
  const unusedExports = [];
  for (const file of files.filter((file) =>
    reachable.has(normalize(file.fileName)),
  )) {
    // A dynamic import exposes a namespace object. Keep every export conservatively.
    if (dynamicModules.has(normalize(file.fileName))) continue;
    const module = checker.getSymbolAtLocation(file);
    if (!module) continue;
    for (const exported of checker.getExportsOfModule(module)) {
      if (!referenced.has(resolveAlias(exported)))
        unusedExports.push({
          file: relative(file.fileName),
          name: exported.name,
        });
    }
  }
  return {
    entryPoints,
    unreachableFiles: files
      .filter((file) => !reachable.has(normalize(file.fileName)))
      .map((file) => relative(file.fileName))
      .sort(),
    unusedExports: unusedExports.sort((left, right) =>
      `${left.file}:${left.name}`.localeCompare(`${right.file}:${right.name}`),
    ),
    unknownEdges: unknownEdges.sort(),
  };
}

if (
  process.argv[1] &&
  normalize(process.argv[1]) === normalize(fileURLToPath(import.meta.url))
) {
  const rootArgument = process.argv.indexOf("--root");
  if (rootArgument >= 0 && !process.argv[rootArgument + 1])
    throw new Error("--root requires a client directory");
  const root =
    rootArgument >= 0
      ? path.resolve(process.argv[rootArgument + 1])
      : fileURLToPath(new URL("..", import.meta.url));
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const entryPoints = [
    ...html.matchAll(
      /<script\b[^>]*\btype="module"[^>]*\bsrc="\/([^"?]+)"[^>]*>/g,
    ),
  ].map((match) => match[1]);
  if (entryPoints.length === 0)
    throw new Error("No application entry points found in index.html");
  const report = auditProject({ root, entryPoints });
  console.log(JSON.stringify(report, null, 2));
  // Symbol candidates need human review (e.g. explicit testing infrastructure).
  // File reachability and unknown edges are a deterministic, enforceable gate.
  if (
    process.argv.includes("--check") &&
    (report.unreachableFiles.length || report.unknownEdges.length)
  )
    process.exitCode = 1;
}
