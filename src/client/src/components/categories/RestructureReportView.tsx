import type { RestructureReport } from "@/hooks/useRestructurePlans";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

const TOP_TRANSITIONS = 25;

function Counts({ rows }: { rows: [string, number][] }) {
  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4">
      {rows.map(([label, value]) => (
        <div key={label} className="flex justify-between gap-2">
          <dt className="text-muted-foreground">{label}</dt>
          <dd className="font-mono tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** What a restructure plan does (or did) to this deployment: counts, plus the largest category moves. */
export function RestructureReportView({ report }: { report: RestructureReport }) {
  const n = Number;
  const rules = Object.entries(report.items.byRule).sort(([, a], [, b]) => b - a);
  return (
    <div className="space-y-5">
      <section aria-labelledby="restructure-categories">
        <h3 id="restructure-categories" className="mb-2 font-medium">
          Categories
        </h3>
        <Counts
          rows={[
            ["Created", n(report.taxonomy.created)],
            ["Renamed", n(report.taxonomy.renamed)],
            ["Moved", n(report.taxonomy.moved)],
            ["Defined", n(report.taxonomy.defined)],
            ["Archived", n(report.taxonomy.archived)],
            ["Restored", n(report.taxonomy.restored)],
            ["Active before", n(report.taxonomy.activeBefore)],
            ["Active after", n(report.taxonomy.activeAfter)],
          ]}
        />
      </section>
      <section aria-labelledby="restructure-purchases">
        <h3 id="restructure-purchases" className="mb-2 font-medium">
          Purchases
        </h3>
        <Counts
          rows={[
            ["Reassigned", n(report.items.reassigned)],
            ["Left without a category", n(report.items.cleared)],
            ["Unchanged", n(report.items.unchanged)],
            ["Classified since the plan", n(report.items.conflicts)],
            ["No longer here", n(report.items.missingFromDeployment)],
          ]}
        />
        {rules.length > 0 && (
          <p className="mt-2 text-sm text-muted-foreground">
            By rule:{" "}
            {rules.map(([rule, count]) => `${rule} ${count}`).join(" · ")}
          </p>
        )}
      </section>
      <section aria-labelledby="restructure-budget">
        <h3 id="restructure-budget" className="mb-2 font-medium">
          Budget
        </h3>
        <p className="text-sm">
          Purchases keep their envelopes. Envelope merges move{" "}
          {n(report.budget.itemsChangingEnvelope)} purchases, of which{" "}
          {n(report.budget.itemsChangingYnabCategory)} change YNAB category.
        </p>
        {report.envelopeMerges.length > 0 && (
          <ul className="mt-1 list-disc pl-5 text-sm">
            {report.envelopeMerges.map((merge) => (
              <li key={`${merge.source}→${merge.target}`}>
                {merge.source} → {merge.target}: {n(merge.items)} purchases,{" "}
                {n(merge.receipts)} receipts, {n(merge.defaults)} defaults
              </li>
            ))}
          </ul>
        )}
        <Counts
          rows={[
            ["Defaults kept", n(report.defaults.kept)],
            ["Defaults moved", n(report.defaults.moved)],
            ["Defaults removed", n(report.defaults.removed)],
            ["Defaults set", n(report.defaults.set)],
            ["Mappings re-approved", n(report.mappings.reapproved)],
            ["Mappings re-pointed", n(report.mappings.repointed)],
            ["Mappings revoked", n(report.mappings.revoked)],
          ]}
        />
      </section>
      {report.occasions.length > 0 && (
        <section aria-labelledby="restructure-occasions">
          <h3 id="restructure-occasions" className="mb-2 font-medium">
            Occasion tags
          </h3>
          <Counts
            rows={report.occasions.map((occasion) => [
              `${occasion.name}${occasion.created ? " (new)" : ""}`,
              n(occasion.items),
            ])}
          />
        </section>
      )}
      {report.transitions.length > 0 && (
        <section aria-labelledby="restructure-moves">
          <h3 id="restructure-moves" className="mb-2 font-medium">
            Largest moves
          </h3>
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>From</TableHead>
                  <TableHead>To</TableHead>
                  <TableHead className="text-right">Purchases</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {report.transitions.slice(0, TOP_TRANSITIONS).map((move) => (
                  <TableRow key={`${move.fromCategoryId}→${move.toCategoryId}`}>
                    <TableCell>{move.fromPath}</TableCell>
                    <TableCell>{move.toPath}</TableCell>
                    <TableCell className="text-right font-mono tabular-nums">
                      {n(move.count)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          {report.transitions.length > TOP_TRANSITIONS && (
            <p className="mt-1 text-sm text-muted-foreground">
              {report.transitions.length - TOP_TRANSITIONS} smaller moves not shown.
            </p>
          )}
        </section>
      )}
    </div>
  );
}
