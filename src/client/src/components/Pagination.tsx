import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PAGE_SIZE_OPTIONS } from "@/lib/page-size";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

interface PaginationProps {
  currentPage: number;
  totalItems: number;
  pageSize: number;
  totalPages: number;
  onPageChange: (page: number) => void;
  onPageSizeChange?: (size: number) => void;
  pageSizeOptions?: number[];
  disabled?: boolean;
}

export function Pagination({
  currentPage,
  totalItems,
  pageSize,
  totalPages,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = PAGE_SIZE_OPTIONS,
  disabled = false,
}: PaginationProps) {
  const start =
    totalItems === 0 || (currentPage - 1) * pageSize >= totalItems
      ? 0
      : (currentPage - 1) * pageSize + 1;
  const end = Math.min(currentPage * pageSize, totalItems);

  return (
    <div
      className="flex flex-wrap items-center justify-between gap-3"
      aria-label="Table pagination"
    >
      <p className="text-sm text-muted-foreground">
        Showing {start}-{start === 0 ? 0 : end} of {totalItems}
      </p>
      <div className="flex items-center gap-2">
        {onPageSizeChange && (
          <Select
            value={String(pageSize)}
            disabled={disabled}
            onValueChange={(v) => onPageSizeChange(Number(v))}
          >
            <SelectTrigger
              aria-label="Rows per page"
              className="h-8 w-[130px] text-xs"
            >
              <SelectValue />
              <span aria-hidden="true">per page</span>
            </SelectTrigger>
            <SelectContent>
              {pageSizeOptions.map((size) => (
                <SelectItem key={size} value={String(size)}>
                  {size}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <Button
          variant="outline"
          size="sm"
          className="h-8 w-8 p-0"
          disabled={disabled || currentPage <= 1}
          onClick={() => onPageChange(currentPage - 1)}
          aria-label="Previous page"
        >
          <ChevronLeft className="h-4 w-4" />
        </Button>
        <span className="text-sm text-muted-foreground">
          {currentPage}/{Math.max(1, totalPages)}
        </span>
        <Button
          variant="outline"
          size="sm"
          className="h-8 w-8 p-0"
          disabled={disabled || currentPage >= totalPages}
          onClick={() => onPageChange(currentPage + 1)}
          aria-label="Next page"
        >
          <ChevronRight className="h-4 w-4" />
        </Button>
      </div>
    </div>
  );
}
