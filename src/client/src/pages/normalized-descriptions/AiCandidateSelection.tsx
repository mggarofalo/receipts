import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Link } from "react-router";
import { useAiActions, useAiCandidates } from "@/hooks/useNormalizationAi";
import { useServerPagination } from "@/hooks/useServerPagination";
import { Pagination } from "@/components/Pagination";
import { RequestFailure } from "@/components/RequestFailure";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { extractErrorMessage } from "@/lib/problem-details";
import { formatDate } from "@/lib/format";

const searchSchema = z.object({ q: z.string().trim().max(200) });
const requestSchema = z.object({ model: z.string().min(1, "Choose a model.") });

export function AiCandidateSelection({
  models,
  busy,
  onStarted,
}: {
  models: string[];
  busy: boolean;
  onStarted: (id: string) => void;
}) {
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<string[]>([]);
  const [intent, setIntent] = useState<{
    fingerprint: string;
    id: string;
  } | null>(null);
  const pagination = useServerPagination({
    tableId: "normalization-ai-candidates",
  });
  const candidates = useAiCandidates(
    search,
    pagination.offset,
    pagination.limit,
    true,
  );
  const { start } = useAiActions((run) => {
    setSelected([]);
    setIntent(null);
    onStarted(run.id);
  });
  const searchForm = useForm({
    resolver: zodResolver(searchSchema),
    defaultValues: { q: "" },
  });
  const requestForm = useForm({
    resolver: zodResolver(requestSchema),
    defaultValues: { model: "" },
  });
  const selectedModel = useWatch({
    control: requestForm.control,
    name: "model",
  });
  const displayedModels =
    selectedModel && !models.includes(selectedModel)
      ? [selectedModel, ...models]
      : models;
  const items = candidates.data?.items ?? [];
  const total = candidates.data?.total ?? 0;
  return (
    <section className="space-y-4" aria-label="Select items for ChatGPT">
      <h2 className="card-title">Select items</h2>
      <p className="text-sm text-muted-foreground">
        Choose up to 50 unresolved or pending-review items. Active, curated
        links are excluded.
      </p>
      <form
        className="flex gap-2"
        onSubmit={searchForm.handleSubmit(({ q }) => {
          setSearch(q);
          setSelected([]);
          pagination.resetPage();
        })}
      >
        <Input
          aria-label="Find items for normalization"
          {...searchForm.register("q")}
          placeholder="Search item descriptions"
        />
        <Button type="submit" variant="outline">
          Search items
        </Button>
      </form>
      {searchForm.formState.errors.q && (
        <p role="alert">Use at most 200 characters.</p>
      )}
      <p role="status">
        {selected.length} items selected
        {selected.length >
        items.filter((item) => selected.includes(item.id)).length
          ? " (including other pages)"
          : ""}
      </p>
      <Button
        type="button"
        variant="ghost"
        onClick={() => setSelected([])}
        disabled={!selected.length || start.isPending}
      >
        Clear selection
      </Button>
      <form
        className="space-y-3 rounded-lg border p-4"
        onSubmit={(event) => {
          void requestForm.handleSubmit(({ model }) => {
            const fingerprint = JSON.stringify([model, [...selected].sort()]);
            const request =
              intent?.fingerprint === fingerprint
                ? intent
                : { fingerprint, id: crypto.randomUUID() };
            setIntent(request);
            start.mutate({
              clientRequestId: request.id,
              itemIds: selected,
              model,
            });
          })(event);
        }}
      >
        <label className="block space-y-1">
          <span>ChatGPT model</span>
          <select
            className="h-9 max-w-full rounded-md border bg-background px-3"
            aria-label="ChatGPT model"
            {...requestForm.register("model")}
            value={selectedModel}
            disabled={busy || start.isPending}
          >
            <option value="">Choose a model</option>
            {displayedModels.map((model) => (
              <option key={model} value={model}>
                {model}
              </option>
            ))}
          </select>
        </label>
        <p className="text-sm text-muted-foreground">
          Requesting suggestions sends the selected item descriptions and
          candidate normalized names to OpenAI using your connected ChatGPT
          account. Separate store, price, receipt date and payment fields are
          not sent. This uses your plan allowance; available models are a
          catalog, not proof of account access. Nothing is applied
          automatically.
        </p>
        {start.isError && (
          <p role="alert" className="text-destructive">
            {extractErrorMessage(start.error)}
          </p>
        )}
        <Button
          type="submit"
          disabled={
            busy ||
            !selected.length ||
            !models.includes(selectedModel) ||
            start.isPending
          }
        >
          {start.isPending
            ? "Requesting..."
            : `Request suggestions for ${selected.length} items`}
        </Button>
      </form>
      {candidates.isError ? (
        <RequestFailure
          message="Items could not be loaded."
          retry={() => void candidates.refetch()}
          isRetrying={candidates.isFetching}
        />
      ) : candidates.isPending ? (
        <p role="status">Loading items...</p>
      ) : (
        <>
          {items.length === 0 ? (
            <p>No unresolved items match this search.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Select</TableHead>
                  <TableHead>Item</TableHead>
                  <TableHead>Store</TableHead>
                  <TableHead>Receipt date</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {items.map((item) => (
                  <TableRow key={item.id}>
                    <TableCell>
                      <Checkbox
                        aria-label={`Select ${item.description}`}
                        checked={selected.includes(item.id)}
                        disabled={
                          start.isPending ||
                          (!selected.includes(item.id) && selected.length >= 50)
                        }
                        onCheckedChange={(checked) =>
                          setSelected((current) =>
                            checked === true
                              ? [...current, item.id]
                              : current.filter((id) => id !== item.id),
                          )
                        }
                      />
                    </TableCell>
                    <TableCell>{item.description}</TableCell>
                    <TableCell>{item.location}</TableCell>
                    <TableCell>
                      <Link
                        className="underline"
                        to={`/receipts/${item.receiptId}`}
                      >
                        {formatDate(item.date)}
                      </Link>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          <Pagination
            currentPage={pagination.currentPage}
            totalItems={total}
            pageSize={pagination.pageSize}
            totalPages={pagination.totalPages(total)}
            onPageChange={(page) => pagination.setPage(page, total)}
            onPageSizeChange={pagination.setPageSize}
            disabled={candidates.isFetching}
          />
        </>
      )}
    </section>
  );
}
