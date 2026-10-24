#!/usr/bin/env node

// The general drift check compares success JSON schemas. Pin problem response
// status, media type, and schema separately, including when both specs regress.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import yaml from "js-yaml";

const PROBLEM_MEDIA = "application/problem+json";
const PROBLEM_SCHEMA = "#/components/schemas/ProblemDetails";
const METHODS = new Set(["get", "put", "post", "delete", "patch", "head", "options", "trace"]);
const normalizePath = (path) => path.toLowerCase().replace(/\{[^}]+\}/g, "{_}");

export function compareProblemContracts(spec, generated) {
  const errors = [];
  let checked = 0;
  const generatedPaths = new Map(Object.entries(generated.paths ?? {}).map(([path, operations]) => [normalizePath(path), operations]));
  for (const [path, operations] of Object.entries(spec.paths ?? {})) {
    for (const [method, operation] of Object.entries(operations)) {
      if (!METHODS.has(method)) continue;
      for (const [status, response] of Object.entries(operation.responses ?? {})) {
        const content = response.content ?? {};
        if (!(PROBLEM_MEDIA in content) && !Object.values(content).some((entry) => entry.schema?.$ref === PROBLEM_SCHEMA)) continue;
        checked++;
        const label = `${method.toUpperCase()} ${path} ${status}`;
        const found = generatedPaths.get(normalizePath(path))?.[method]?.responses?.[status];
        for (const [source, candidate] of [["canonical", response], ["generated", found]]) {
          const media = Object.keys(candidate?.content ?? {});
          if (media.length !== 1 || media[0] !== PROBLEM_MEDIA) {
            errors.push(`${label}: ${source} media must be only ${PROBLEM_MEDIA}; found ${media.join(", ") || "no content"}`);
          } else if (candidate.content[PROBLEM_MEDIA].schema?.$ref !== PROBLEM_SCHEMA) {
            errors.push(`${label}: ${source} problem schema must reference ${PROBLEM_SCHEMA}`);
          }
        }
      }
    }
  }
  if (checked === 0) errors.push("No declared ProblemDetails responses found; refusing a vacuous contract check.");
  return { checked, errors };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const root = resolve(import.meta.dirname, "..");
  try {
    const spec = yaml.load(readFileSync(resolve(root, "openapi/spec.yaml"), "utf8"));
    const generated = JSON.parse(readFileSync(resolve(root, "openapi/generated/API.json"), "utf8"));
    const { checked, errors } = compareProblemContracts(spec, generated);
    if (errors.length) {
      console.error(errors.join("\n"));
      process.exitCode = 1;
    } else {
      console.log(`Problem contract: ${checked} status/media/schema declarations match.`);
    }
  } catch (error) {
    console.error(`Problem contract check failed: ${error.message}`);
    process.exitCode = 1;
  }
}
