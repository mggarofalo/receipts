import { Badge } from "@/components/ui/badge";
import { AlertTriangle, CheckCircle, Clock, XCircle, Minus } from "lucide-react";
import type { ReceiptYnabSyncStatusValue } from "@/hooks/useYnab";

const STATUS_CONFIG: Record<
  ReceiptYnabSyncStatusValue,
  { label: string; variant: "default" | "secondary" | "destructive" | "outline"; icon: typeof CheckCircle }
> = {
  synced: { label: "Synced", variant: "default", icon: CheckCircle },
  pending: { label: "Pending", variant: "secondary", icon: Clock },
  failed: { label: "Failed", variant: "destructive", icon: XCircle },
  unknown: { label: "Needs Review", variant: "destructive", icon: AlertTriangle },
  notSynced: { label: "Not Synced", variant: "outline", icon: Minus },
};

interface YnabSyncBadgeProps {
  status: ReceiptYnabSyncStatusValue | undefined;
}

export function YnabSyncBadge({ status }: YnabSyncBadgeProps) {
  if (!status) return null;

  const config = STATUS_CONFIG[status];
  const Icon = config.icon;

  return (
    <Badge variant={config.variant} aria-label={`YNAB sync status: ${config.label}`}>
      <Icon className="h-3 w-3" />
      {config.label}
    </Badge>
  );
}
