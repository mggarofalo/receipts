import { RequestFailure } from "@/components/RequestFailure";
import { useRef } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useFieldHistory } from "@/hooks/useFieldHistory";
import { useAllCategories } from "@/hooks/useCategories";
import { subcategoryNameHistory } from "@/lib/field-history";
import { Checkbox } from "@/components/ui/checkbox";
import { Combobox } from "@/components/ui/combobox";
import { Button } from "@/components/ui/button";
import { SubmitButton } from "@/components/ui/submit-button";
import { Input } from "@/components/ui/input";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";

const subcategorySchema = z.object({
  name: z.string().min(1, "Name is required"),
  categoryId: z.string().min(1, "Category is required"),
  description: z.string().optional(),
  isActive: z.boolean(),
});

type SubcategoryFormValues = z.infer<typeof subcategorySchema>;

interface SubcategoryFormProps {
  mode: "create" | "edit";
  defaultValues?: Partial<SubcategoryFormValues>;
  onSubmit: (values: SubcategoryFormValues) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
}

export function SubcategoryForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
}: SubcategoryFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { options: subcategoryNameOptions, add: addSubcategoryName } =
    useFieldHistory(subcategoryNameHistory);
  const categoriesQuery = useAllCategories();
  const { data: categories } = categoriesQuery;

  const categoryOptions =
    (
      categories as
        | { id: string; name: string; isActive: boolean }[]
        | undefined
    )
      ?.filter((c) => c.isActive)
      .map((c) => ({
        value: c.id,
        label: c.name,
      })) ?? [];

  const form = useForm<SubcategoryFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(subcategorySchema) as any,
    defaultValues: {
      name: "",
      categoryId: "",
      description: "",
      isActive: true,
      ...defaultValues,
    },
  });

  return (
    <Form {...form}>
      <form
        ref={formRef}
        onSubmit={form.handleSubmit((values) => {
          addSubcategoryName(values.name);
          onSubmit(values);
        })}
        className="space-y-4"
      >
        <FormField
          control={form.control}
          name="name"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Name</FormLabel>
              <FormControl>
                <Combobox
                  options={subcategoryNameOptions}
                  value={field.value}
                  onValueChange={field.onChange}
                  placeholder="Select or type a name..."
                  searchPlaceholder="Search names..."
                  emptyMessage="Type to add a new subcategory"
                  allowCustom
                  aria-required="true"
                />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="categoryId"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Category</FormLabel>
              <FormControl>
                <Combobox
                  options={categoryOptions}
                  value={field.value}
                  onValueChange={field.onChange}
                  placeholder="Select a category..."
                  searchPlaceholder="Search categories..."
                  emptyMessage={
                    categoriesQuery.isError
                      ? "Category choices unavailable."
                      : categoriesQuery.isLoading
                        ? "Loading category choices…"
                        : "No categories found."
                  }
                />
              </FormControl>
              <FormMessage />
              {categoriesQuery.isError && (
                <RequestFailure
                  message="Category choices unavailable. Cached choices and your selection are retained."
                  retry={() => {
                    void categoriesQuery.refetch();
                  }}
                  isRetrying={categoriesQuery.isFetching}
                />
              )}
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="description"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Description (optional)</FormLabel>
              <FormControl>
                <Input {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="isActive"
          render={({ field }) => (
            <FormItem>
              <div className="flex items-center gap-2">
                <FormControl>
                  <Checkbox
                    checked={field.value}
                    onCheckedChange={field.onChange}
                  />
                </FormControl>
                <FormLabel>Active</FormLabel>
              </div>
              <FormMessage />
            </FormItem>
          )}
        />

        <div className="flex justify-end gap-2 pt-4">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <SubmitButton
            isSubmitting={isSubmitting ?? false}
            label={
              mode === "create" ? "Create Subcategory" : "Update Subcategory"
            }
            loadingLabel="Saving..."
          />
        </div>
      </form>
    </Form>
  );
}
