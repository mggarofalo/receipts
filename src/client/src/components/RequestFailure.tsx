import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";

/** Inline failure ownership: keep the surrounding feature mounted and offer retry. */
export function RequestFailure({
  message,
  retry,
  isRetrying = false,
}: {
  message: string;
  retry: () => void;
  isRetrying?: boolean;
}) {
  return (
    <Alert variant="destructive">
      <AlertDescription className="flex items-center justify-between gap-3">
        <span>{message}</span>
        <Button type="button" variant="outline" size="sm" disabled={isRetrying} onClick={retry}>
          {isRetrying ? "Retrying…" : "Retry"}
        </Button>
      </AlertDescription>
    </Alert>
  );
}
