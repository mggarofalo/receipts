import { useMemo, useRef } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { useFormShortcuts } from "@/hooks/useFormShortcuts";
import { useAllAccounts } from "@/hooks/useAccounts";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { SubmitButton } from "@/components/ui/submit-button";
import { Input } from "@/components/ui/input";
import { Combobox } from "@/components/ui/combobox";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Trash2 } from "lucide-react";

const cardSchema = z.object({
  cardCode: z.string().min(1, "Card code is required"),
  name: z.string().min(1, "Name is required"),
  isActive: z.boolean(),
  accountId: z.string().min(1, "Account is required"),
});

type CardFormValues = z.infer<typeof cardSchema>;

interface CardFormProps {
  mode: "create" | "edit";
  defaultValues?: CardFormValues;
  onSubmit: (values: CardFormValues, event?: unknown) => void;
  onCancel: () => void;
  isSubmitting?: boolean;
  onDelete?: () => void;
  isDeleting?: boolean;
  isAdmin?: boolean;
}

export function CardForm({
  mode,
  defaultValues,
  onSubmit,
  onCancel,
  isSubmitting,
  onDelete,
  isDeleting,
  isAdmin,
}: CardFormProps) {
  const formRef = useRef<HTMLFormElement>(null);
  useFormShortcuts({ formRef });
  const { data: accounts } = useAllAccounts(true);

  const accountOptions = useMemo(() => {
    const active = (accounts as { id: string; name: string }[] | undefined) ?? [];
    return active.map((a) => ({ value: a.id, label: a.name }));
  }, [accounts]);

  const form = useForm<CardFormValues>({
    resolver: zodResolver(cardSchema),
    defaultValues: defaultValues ?? {
      cardCode: "",
      name: "",
      isActive: true,
      accountId: "",
    },
  });

  const handleSubmit = (values: CardFormValues, event?: unknown) => {
    onSubmit(values, event);
  };

  return (
    <Form {...form}>
      <form ref={formRef} onSubmit={form.handleSubmit(handleSubmit)} className="space-y-4">
        <FormField
          control={form.control}
          name="cardCode"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Card Code</FormLabel>
              <FormControl>
                <Input aria-required="true" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="name"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Name</FormLabel>
              <FormControl>
                <Input aria-required="true" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />

        <FormField
          control={form.control}
          name="accountId"
          render={({ field }) => (
            <FormItem>
              <FormLabel required>Account</FormLabel>
              <FormControl>
                <Combobox
                  options={accountOptions}
                  value={field.value ?? ""}
                  onValueChange={field.onChange}
                  placeholder="Select an account..."
                  searchPlaceholder="Search accounts..."
                  emptyMessage="No active accounts."
                  aria-label="Account"
                  aria-required="true"
                />
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

        <div className="flex items-center pt-4">
          {mode === "edit" && isAdmin && onDelete && (
            <AlertDialog>
              <AlertDialogTrigger asChild>
                <Button
                  type="button"
                  variant="destructive"
                  size="sm"
                  disabled={isDeleting}
                >
                  <Trash2 className="mr-2 h-4 w-4" />
                  {isDeleting ? "Deleting..." : "Delete"}
                </Button>
              </AlertDialogTrigger>
              <AlertDialogContent>
                <AlertDialogHeader>
                  <AlertDialogTitle>Delete Card?</AlertDialogTitle>
                  <AlertDialogDescription>
                    This will permanently delete this card. This action
                    cannot be undone.
                  </AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                  <AlertDialogCancel>Cancel</AlertDialogCancel>
                  <AlertDialogAction
                    variant="destructive"
                    onClick={onDelete}
                  >
                    Delete
                  </AlertDialogAction>
                </AlertDialogFooter>
              </AlertDialogContent>
            </AlertDialog>
          )}
          <div className="ml-auto flex gap-2">
            <Button type="button" variant="outline" onClick={onCancel}>
              Cancel
            </Button>
            <SubmitButton
              isSubmitting={isSubmitting ?? false}
              label={mode === "create" ? "Create Card" : "Update Card"}
              loadingLabel="Saving..."
            />
          </div>
        </div>
      </form>
    </Form>
  );
}
