import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { DateInput } from "@/components/ui/date-input";
import {
  EMPTY_PURCHASE_FILTERS,
  purchaseFiltersSchema,
  type PurchaseFilters as Filters,
} from "@/lib/purchase-table";

/** Draft filters are applied together, so typing never fires partial-range requests. */
export function PurchaseFilters({
  initialValues,
  onApply,
}: {
  initialValues: Filters;
  onApply: (values: Filters) => void;
}) {
  const {
    register,
    control,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<Filters>({
    resolver: zodResolver(purchaseFiltersSchema),
    defaultValues: initialValues,
  });
  return (
    <form
      aria-label="Purchase filters"
      noValidate
      onSubmit={handleSubmit(onApply)}
      className="space-y-3 rounded-lg border bg-card p-4"
    >
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <label className="space-y-1 text-sm">
          Store contains
          <Input
            {...register("location")}
            placeholder="Any store"
            aria-invalid={!!errors.location}
          />
          {errors.location && (
            <span role="alert">{errors.location.message}</span>
          )}
        </label>
        {(["startDate", "endDate"] as const).map((name) => (
          <div key={name} className="space-y-1 text-sm">
            <label htmlFor={`purchase-${name}`}>
              {name === "startDate" ? "From date" : "To date"}
            </label>
            <Controller
              name={name}
              control={control}
              render={({ field }) => (
                <DateInput
                  id={`purchase-${name}`}
                  value={field.value}
                  onChange={field.onChange}
                  aria-invalid={!!errors[name]}
                />
              )}
            />
            {errors[name] && <span role="alert">{errors[name].message}</span>}
          </div>
        ))}
        {(["minUnitPrice", "maxUnitPrice"] as const).map((name) => (
          <label key={name} className="space-y-1 text-sm">
            {name === "minUnitPrice"
              ? "Minimum unit price"
              : "Maximum unit price"}
            <Input
              {...register(name)}
              type="number"
              min="0"
              step="any"
              placeholder="Any price"
              aria-invalid={!!errors[name]}
            />
            {errors[name] && <span role="alert">{errors[name].message}</span>}
          </label>
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button type="submit" size="sm">
          Apply filters
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => {
            reset(EMPTY_PURCHASE_FILTERS);
            onApply(EMPTY_PURCHASE_FILTERS);
          }}
        >
          Clear filters
        </Button>
        <span className="text-xs text-muted-foreground">
          Dates and prices include both endpoints.
        </span>
      </div>
    </form>
  );
}
