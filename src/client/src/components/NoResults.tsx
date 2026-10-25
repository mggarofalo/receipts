import { Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useSearchHistory } from "@/hooks/useSearchHistory";

interface NoResultsProps {
  searchTerm: string;
  onClearSearch: () => void;
  onSelectSuggestion?: (term: string) => void;
  entityName?: string;
}

export function NoResults({
  searchTerm,
  onClearSearch,
  onSelectSuggestion,
  entityName = "results",
}: NoResultsProps) {
  const { history } = useSearchHistory();
  const suggestions = history.filter((h) => h !== searchTerm).slice(0, 5);

  return (
    <div role="status" className="flex flex-col items-center py-12 text-center">
      <Search className="mb-3 h-10 w-10 text-muted-foreground/50" />
      <p className="text-sm text-muted-foreground">
        No {entityName} match &ldquo;
        <span className="font-medium text-foreground">{searchTerm}</span>
        &rdquo;
      </p>
      <Button
        variant="outline"
        size="sm"
        className="mt-3"
        onClick={onClearSearch}
      >
        Clear search
      </Button>

      {suggestions.length > 0 && onSelectSuggestion && (
        <div className="mt-4">
          <p className="mb-2 text-xs text-muted-foreground">Recent searches</p>
          <div className="flex flex-wrap justify-center gap-1.5">
            {suggestions.map((s) => (
              <Button
                key={s}
                variant="secondary"
                size="sm"
                className="h-7 text-xs"
                onClick={() => onSelectSuggestion(s)}
              >
                {s}
              </Button>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
