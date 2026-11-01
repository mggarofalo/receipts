import { useState, type ReactNode } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod/v4";
import { zodResolver } from "@hookform/resolvers/zod";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";

// The mutation cache owns failure feedback (toasts); the rejection needs no further handling.
function ignoreHandledFailure() {}

const nameSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, "Name is required")
    .max(200, "Name must be 200 characters or fewer"),
});

type NameValues = z.infer<typeof nameSchema>;

export interface NamedLibraryItem {
  id: string;
  name: string;
  isActive: boolean;
}

interface NameFormProps {
  label: string;
  submitLabel: string;
  defaultName?: string;
  disabled?: boolean;
  onSubmit: (name: string) => Promise<unknown>;
  onCancel?: () => void;
}

function NameForm({
  label,
  submitLabel,
  defaultName = "",
  disabled,
  onSubmit,
  onCancel,
}: NameFormProps) {
  const form = useForm<NameValues>({
    resolver: zodResolver(nameSchema),
    defaultValues: { name: defaultName },
  });
  return (
    <Form {...form}>
      <form
        className="flex flex-col gap-2 sm:flex-row sm:items-end"
        onSubmit={form.handleSubmit(async ({ name }) => {
          try {
            await onSubmit(name);
          } catch {
            // The mutation cache owns failure feedback; keep the typed name.
            return;
          }
          form.reset({ name: "" });
        })}
      >
        <FormField
          control={form.control}
          name="name"
          render={({ field }) => (
            <FormItem className="w-full sm:max-w-sm">
              <FormLabel>{label}</FormLabel>
              <FormControl>
                <Input {...field} disabled={disabled} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
        <Button type="submit" disabled={disabled}>
          {submitLabel}
        </Button>
        {onCancel && (
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        )}
      </form>
    </Form>
  );
}

interface NamedLibrarySectionProps {
  title: string;
  description: string;
  noun: string;
  items: NamedLibraryItem[];
  isLoading: boolean;
  isError: boolean;
  busy: boolean;
  onCreate: (name: string) => Promise<unknown>;
  onUpdate: (item: NamedLibraryItem) => Promise<unknown>;
  onDelete: (id: string) => Promise<unknown>;
  /** A secondary line under an item's name, such as its YNAB link. */
  detail?: (item: NamedLibraryItem) => ReactNode;
}

/** Create, rename, archive and delete for a small named library list. */
export function NamedLibrarySection({
  title,
  description,
  noun,
  items,
  isLoading,
  isError,
  busy,
  onCreate,
  onUpdate,
  onDelete,
  detail,
}: NamedLibrarySectionProps) {
  const [renamingId, setRenamingId] = useState<string>();
  // The item outlives the open flag so the dialog keeps its text while it fades out.
  const [deleting, setDeleting] = useState<NamedLibraryItem>();
  const [confirmOpen, setConfirmOpen] = useState(false);
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <NameForm
          label={`New ${noun}`}
          submitLabel="Add"
          disabled={busy}
          onSubmit={onCreate}
        />
        {isLoading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : isError ? (
          <p className="text-sm text-destructive">
            The {noun} list is unavailable.
          </p>
        ) : items.length === 0 ? (
          <p className="text-sm text-muted-foreground">No {noun}s yet.</p>
        ) : (
          <ul className="divide-y rounded-md border">
            {items.map((item) => (
              <li key={item.id} className="space-y-2 p-3">
                {renamingId === item.id ? (
                  <NameForm
                    label={`Rename ${item.name}`}
                    submitLabel="Save"
                    defaultName={item.name}
                    disabled={busy}
                    onSubmit={async (name) => {
                      await onUpdate({ ...item, name });
                      setRenamingId(undefined);
                    }}
                    onCancel={() => setRenamingId(undefined)}
                  />
                ) : (
                  <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                    <div className="min-w-0 flex-1">
                      <span className="break-words text-sm font-medium">
                        {item.name}
                        {!item.isActive && (
                          <Badge variant="outline" className="ml-2">
                            Archived
                          </Badge>
                        )}
                      </span>
                      {detail && (
                        <p className="break-words text-xs text-muted-foreground">
                          {detail(item)}
                        </p>
                      )}
                    </div>
                    <div className="flex flex-wrap gap-2">
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={busy}
                        onClick={() => setRenamingId(item.id)}
                      >
                        Rename
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={busy}
                        onClick={() =>
                          onUpdate({ ...item, isActive: !item.isActive }).catch(
                            ignoreHandledFailure,
                          )
                        }
                      >
                        {item.isActive ? "Archive" : "Restore"}
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={busy}
                        onClick={() => {
                          setDeleting(item);
                          setConfirmOpen(true);
                        }}
                        aria-label={`Delete ${item.name}`}
                      >
                        Delete
                      </Button>
                    </div>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete {noun}?</AlertDialogTitle>
            <AlertDialogDescription>
              Delete &ldquo;{deleting?.name}&rdquo;? An unused {noun} is removed
              for good. One that purchases still use cannot be deleted; archive
              it instead.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (deleting) onDelete(deleting.id).catch(ignoreHandledFailure);
              }}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  );
}
