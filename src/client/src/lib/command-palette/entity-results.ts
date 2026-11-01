import type { ComponentType, SVGProps } from "react";
import {
  Building2,
  CreditCard,
  Package,
  Receipt,
  Tag,
  Users,
} from "lucide-react";
import { useAccounts } from "@/hooks/useAccounts";
import { useCards } from "@/hooks/useCards";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import {
  categoryPaths,
  type CurrentClassification,
} from "@/lib/category-paths";
import { useReceipts } from "@/hooks/useReceipts";
import { useReceiptItems } from "@/hooks/useReceiptItems";
import { useUsers } from "@/hooks/useUsers";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";

/** API list endpoints reject any page larger than 500. */
const SMALL_LIMIT = 500;
const LARGE_LIMIT = 200;

export interface EntityResultItem {
  id: string;
  label: string;
  /** Secondary descriptor shown in muted text (e.g. card code, location date). */
  meta?: string;
  /** Concatenated search tokens handed to cmdk's value matcher. */
  searchValue: string;
  /** Target path on select. */
  href: string;
}

export interface EntityResultGroup {
  id: string;
  heading: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  items: EntityResultItem[];
  failure?: { message: string; retry: () => void; isRetrying: boolean };
  isLoading?: boolean;
  scopeNotice?: string;
}

interface AccountLike {
  id: string;
  name: string;
}
interface CardLike {
  id: string;
  name: string;
  cardCode?: string | null;
}
interface ReceiptLike {
  id: string;
  location: string;
  date?: string | null;
}
interface ReceiptItemLike {
  id: string;
  receiptId: string;
  description: string;
  receiptItemCode?: string | null;
  currentClassification?: CurrentClassification | null;
}
interface UserLike {
  userId: string;
  email: string;
  firstName?: string | null;
  lastName?: string | null;
}

function tokens(...parts: Array<string | null | undefined>): string {
  return parts
    .filter((p): p is string => Boolean(p))
    .join(" ")
    .toLowerCase();
}

function itemCategoryLabel(item: ReceiptItemLike): string {
  const classification = item.currentClassification;
  if (classification) {
    if (classification.categoryId === null) return "Unclassified";
    if (!classification.recordedPath.length) return "Unresolved category path";
    return classification.recordedPath.map((node) => node.name).join(" / ");
  }
  return "Unresolved category";
}

export function useEntityResults({
  isAdmin,
  open,
  query = "",
}: {
  isAdmin: boolean;
  open: boolean;
  /**
   * Current palette input. When non-empty, the debounced value is sent to the
   * backend `q=` filter on receipts and receipt items so matches against
   * entities older than the LARGE_LIMIT page still surface.
   */
  query?: string;
}): EntityResultGroup[] {
  const debouncedQuery = useDebouncedValue(query, 200);
  const rawSearch = query.trim() || undefined;
  const entitySearch = debouncedQuery.trim() || undefined;
  // Wait for the debounced value before enabling. Enabling from the raw input
  // would issue an unfiltered 500-row request on the first keystroke and then
  // immediately replace it with the real server search.
  const hasQuery =
    open && rawSearch !== undefined && rawSearch === entitySearch;
  const referenceOptions = { enabled: hasQuery, q: entitySearch };
  const accounts = useAccounts(
    0,
    SMALL_LIMIT,
    undefined,
    undefined,
    undefined,
    referenceOptions,
  );
  const cards = useCards(
    0,
    SMALL_LIMIT,
    undefined,
    undefined,
    undefined,
    referenceOptions,
  );
  const categories = useCategoryTaxonomy(hasQuery);
  const receipts = useReceipts(
    0,
    LARGE_LIMIT,
    null,
    null,
    null,
    null,
    entitySearch,
    { enabled: hasQuery },
  );
  const receiptItems = useReceiptItems(
    0,
    LARGE_LIMIT,
    null,
    null,
    entitySearch,
    { enabled: hasQuery },
  );
  const users = useUsers(0, SMALL_LIMIT, undefined, undefined, {
    enabled: isAdmin && hasQuery,
  });

  if (!hasQuery) return [];
  {
    const groups: EntityResultGroup[] = [];

    const accountData = (accounts.data as AccountLike[] | undefined) ?? [];
    if (accountData.length) {
      groups.push({
        id: "accounts",
        heading: "Accounts",
        icon: Building2,
        items: accountData.map((a) => ({
          id: a.id,
          label: a.name,
          searchValue: tokens("account", a.name),
          href: "/accounts",
        })),
      });
    }

    const cardData = (cards.data as CardLike[] | undefined) ?? [];
    if (cardData.length) {
      groups.push({
        id: "cards",
        heading: "Cards",
        icon: CreditCard,
        items: cardData.map((c) => ({
          id: c.id,
          label: c.name,
          meta: c.cardCode ?? undefined,
          searchValue: tokens("card", c.name, c.cardCode),
          href: "/cards",
        })),
      });
    }

    const categoryData = categoryPaths(categories.data?.nodes ?? []);
    groups.push({
      id: "categories",
      heading: "Categories",
      icon: Tag,
      items: categoryData.map((entry) => ({
        id: entry.node.id,
        label: entry.label,
        searchValue: tokens("category", entry.label),
        href: "/categories",
      })),
      isLoading: categories.isLoading,
      ...(categories.isError
        ? {
            items: [],
            failure: {
              message: "Categories search unavailable.",
              retry: () => {
                void categories.refetch();
              },
              isRetrying: categories.isFetching,
            },
          }
        : {}),
    });

    const receiptData = (receipts.data as ReceiptLike[] | undefined) ?? [];
    if (receiptData.length) {
      groups.push({
        id: "receipts",
        heading: "Receipts",
        icon: Receipt,
        items: receiptData.map((r) => ({
          id: r.id,
          label: r.location,
          meta: r.date ?? undefined,
          searchValue: tokens("receipt", r.location, r.date),
          href: `/receipts/${r.id}`,
        })),
      });
    }

    const receiptItemData =
      (receiptItems.data as ReceiptItemLike[] | undefined) ?? [];
    if (receiptItemData.length) {
      groups.push({
        id: "receipt-items",
        heading: "Receipt Items",
        icon: Package,
        items: receiptItemData.map((i) => {
          const categoryLabel = itemCategoryLabel(i);
          return {
            id: i.id,
            label: i.description,
            meta: [i.receiptItemCode, categoryLabel]
              .filter(Boolean)
              .join(" · "),
            searchValue: tokens(
              "item",
              i.description,
              i.receiptItemCode,
              categoryLabel,
            ),
            href: `/receipts/${i.receiptId}`,
          };
        }),
      });
    }

    if (isAdmin) {
      const userData = (users.data as UserLike[] | undefined) ?? [];
      if (userData.length) {
        groups.push({
          id: "users",
          heading: "Users",
          icon: Users,
          items: userData.map((u) => {
            const fullName = [u.firstName, u.lastName]
              .filter(Boolean)
              .join(" ");
            return {
              id: u.userId,
              label: fullName || u.email,
              meta: fullName ? u.email : undefined,
              searchValue: tokens("user", fullName, u.email),
              href: "/admin/users",
            };
          }),
        });
      }
    }

    const sources = [
      { id: "accounts", heading: "Accounts", icon: Building2, query: accounts },
      { id: "cards", heading: "Cards", icon: CreditCard, query: cards },
      { id: "receipts", heading: "Receipts", icon: Receipt, query: receipts },
      {
        id: "receipt-items",
        heading: "Receipt Items",
        icon: Package,
        query: receiptItems,
      },
      ...(isAdmin
        ? [{ id: "users", heading: "Users", icon: Users, query: users }]
        : []),
    ];
    for (const source of sources) {
      let group = groups.find((candidate) => candidate.id === source.id);
      if (!group) {
        group = {
          id: source.id,
          heading: source.heading,
          icon: source.icon,
          items: [],
        };
        groups.push(group);
      }
      group.isLoading = source.query.isLoading;
      if (source.query.isError) {
        // A failed refresh must not masquerade as a current, complete result.
        group.items = [];
        group.failure = {
          message: `${source.heading} search unavailable.`,
          retry: () => {
            void source.query.refetch();
          },
          isRetrying: source.query.isFetching,
        };
      } else if (source.query.total > group.items.length) {
        group.scopeNotice =
          source.id === "users"
            ? `User search covers the first ${group.items.length} of ${source.query.total} users.`
            : `${source.heading}: showing the first ${group.items.length} of ${source.query.total} matches. Refine your search.`;
      }
    }
    return groups;
  }
}
