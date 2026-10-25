import type { ComponentType, SVGProps } from "react";
import {
  Building2,
  CreditCard,
  Package,
  Receipt,
  Tag,
  Tags,
  Users,
} from "lucide-react";
import { useAccounts } from "@/hooks/useAccounts";
import { useCards } from "@/hooks/useCards";
import { useCategories } from "@/hooks/useCategories";
import { useSubcategories } from "@/hooks/useSubcategories";
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
interface CategoryLike {
  id: string;
  name: string;
  description?: string | null;
}
interface SubcategoryLike {
  id: string;
  name: string;
  description?: string | null;
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
  category?: string | null;
  subcategory?: string | null;
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
  const categories = useCategories(
    0,
    SMALL_LIMIT,
    undefined,
    undefined,
    undefined,
    referenceOptions,
  );
  const subcategories = useSubcategories(
    0,
    SMALL_LIMIT,
    undefined,
    undefined,
    undefined,
    referenceOptions,
  );
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

    const categoryData = (categories.data as CategoryLike[] | undefined) ?? [];
    if (categoryData.length) {
      groups.push({
        id: "categories",
        heading: "Categories",
        icon: Tag,
        items: categoryData.map((c) => ({
          id: c.id,
          label: c.name,
          meta: c.description ?? undefined,
          searchValue: tokens("category", c.name, c.description),
          href: "/categories",
        })),
      });
    }

    const subcategoryData =
      (subcategories.data as SubcategoryLike[] | undefined) ?? [];
    if (subcategoryData.length) {
      groups.push({
        id: "subcategories",
        heading: "Subcategories",
        icon: Tags,
        items: subcategoryData.map((s) => ({
          id: s.id,
          label: s.name,
          meta: s.description ?? undefined,
          searchValue: tokens("subcategory", s.name, s.description),
          href: "/subcategories",
        })),
      });
    }

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
        items: receiptItemData.map((i) => ({
          id: i.id,
          label: i.description,
          meta:
            [i.receiptItemCode, i.category].filter(Boolean).join(" · ") ||
            undefined,
          searchValue: tokens(
            "item",
            i.description,
            i.receiptItemCode,
            i.category,
            i.subcategory,
          ),
          href: `/receipts/${i.receiptId}`,
        })),
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
      { id: "categories", heading: "Categories", icon: Tag, query: categories },
      {
        id: "subcategories",
        heading: "Subcategories",
        icon: Tags,
        query: subcategories,
      },
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
