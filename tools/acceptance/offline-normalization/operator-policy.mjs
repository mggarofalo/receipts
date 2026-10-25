import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();

// This projects operator preferences, never semantic answers. Inspection must come
// from the production inspect command; source canonicalization is not duplicated.
export function buildPolicy(sourceBytes, inspection, bindings, eligibleKeys) {
  const sourceHash = sha256(sourceBytes);
  if (inspection.InspectionVersion !== 1 || inspection.SourceSha256 !== sourceHash)
    throw new Error('Inspection is not bound to the exact source bytes');
  if (!Array.isArray(eligibleKeys) || eligibleKeys.length === 0 ||
      new Set(eligibleKeys).size !== eligibleKeys.length)
    throw new Error('Eligible fixture keys must be explicit, nonempty and unique');
  const scopes = eligibleKeys.map(key => {
    const id = bindings.items?.[key];
    const matches = inspection.Observations.filter(o => o.EntityKind === 'item' && o.EntityId === id);
    if (!id || matches.length !== 1) throw new Error('Eligible key must bind exactly one inspected item');
    const { Source, SourceHash } = matches[0];
    if (!Source || typeof Source.Location !== 'string' || typeof Source.Description !== 'string' ||
        !(Source.ItemCode === null || typeof Source.ItemCode === 'string') ||
        !/^[A-F0-9]{64}$/.test(SourceHash)) throw new Error('Invalid inspected source tuple');
    return { Source: { Location: Source.Location, Description: Source.Description, ItemCode: Source.ItemCode }, SourceHash };
  });
  if (new Set(scopes.map(s => s.SourceHash)).size !== scopes.length)
    throw new Error('Eligible source scopes must be unique');
  scopes.sort((a, b) => a.SourceHash.localeCompare(b.SourceHash, 'en'));
  return {
    PolicyVersion: 1,
    Classification: 'Synthetic scenario operator policy; not a claim about the actual data owner’s preferences',
    Attribution: 'Selected by the test author/root from the preregistered synthetic fixture',
    SourceSha256: sourceHash,
    Policy: 'Independently review these exact scopes for future reuse. Remember a scope only when product identity and whole-scope consistency are supported by A and independently reviewed. Eligibility does not approve an identity or compel remembering. All other choices are one-off. Do not generalize across location, description or item code.',
    EligibleScopes: scopes,
  };
}

async function main() {
  const args = Object.fromEntries(process.argv.slice(2).map(arg => {
    const index = arg.indexOf('=');
    if (index < 1) throw new Error('Arguments must be key=value');
    return [arg.slice(0, index), arg.slice(index + 1)];
  }));
  if (Object.keys(args).some(key => !['source', 'inspection', 'bindings', 'eligible', 'out'].includes(key)) ||
      ['source', 'inspection', 'bindings', 'eligible', 'out'].some(key => !args[key]))
    throw new Error('Required: source= inspection= bindings= eligible=<comma-separated fixture keys> out=');
  const [source, inspection, bindings] = await Promise.all([
    readFile(args.source), readFile(args.inspection, 'utf8'), readFile(args.bindings, 'utf8'),
  ]);
  const policy = buildPolicy(source, JSON.parse(inspection.replace(/^\uFEFF/, '')),
    JSON.parse(bindings.replace(/^\uFEFF/, '')), args.eligible.split(','));
  const bytes = Buffer.from(JSON.stringify(policy, null, 2) + '\n', 'utf8');
  await writeFile(args.out, bytes, { flag: 'wx' });
  process.stdout.write(`Synthetic operator policy written; SHA-256 ${sha256(bytes)}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await main();
