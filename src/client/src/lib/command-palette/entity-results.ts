import { useMemo } from "react";
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
import { useItemTemplates } from "@/hooks/useItemTemplates";
import { useReceipts } from "@/hooks/useReceipts";
import { useReceiptItems } from "@/hooks/useReceiptItems";
import { useUsers } from "@/hooks/useUsers";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";

/** Effectively "all" for small reference datasets; capped for large ones. */
const SMALL_LIMIT = 1000;
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
}

interface AccountLike { id: string; name: string }
interface CardLike { id: string; name: string; cardCode?: string | null }
interface CategoryLike { id: string; name: string; description?: string | null }
interface SubcategoryLike { id: string; name: string; description?: string | null }
interface ItemTemplateLike { id: string; name: string; description?: string | null; defaultCategory?: string | null }
interface ReceiptLike { id: string; location: string; date?: string | null }
interface ReceiptItemLike { id: string; receiptId: string; description: string; receiptItemCode?: string | null; category?: string | null; subcategory?: string | null }
interface UserLike { userId: string; email: string; firstName?: string | null; lastName?: string | null }

function tokens(...parts: Array<string | null | undefined>): string {
  return parts
    .filter((p): p is string => Boolean(p))
    .join(" ")
    .toLowerCase();
}

export function useEntityResults({
  isAdmin,
  query = "",
}: {
  isAdmin: boolean;
  /**
   * Current palette input. When non-empty, the debounced value is sent to the
   * backend `q=` filter on receipts and receipt items so matches against
   * entities older than the LARGE_LIMIT page still surface.
   */
  query?: string;
}): EntityResultGroup[] {
  const debouncedQuery = useDebouncedValue(query, 200);
  const entitySearch = debouncedQuery.trim() || undefined;
  // Nothing renders until the user types (CommandPalette only shows entity
  // groups once `query` is non-empty), so mounting these on palette open
  // would fire 7-8 list queries the UI never displays. Gate on the raw
  // (non-debounced) input so the fetch starts on the very first keystroke
  // rather than waiting out the debounce window.
  const hasQuery = query.trim().length > 0;
  const accounts = useAccounts(0, SMALL_LIMIT, undefined, undefined, undefined, { enabled: hasQuery });
  const cards = useCards(0, SMALL_LIMIT, undefined, undefined, undefined, { enabled: hasQuery });
  const categories = useCategories(0, SMALL_LIMIT, undefined, undefined, undefined, { enabled: hasQuery });
  const subcategories = useSubcategories(0, SMALL_LIMIT, undefined, undefined, undefined, { enabled: hasQuery });
  const itemTemplates = useItemTemplates(0, SMALL_LIMIT, undefined, undefined, { enabled: hasQuery });
  const receipts = useReceipts(0, LARGE_LIMIT, null, null, null, null, entitySearch, { enabled: hasQuery });
  const receiptItems = useReceiptItems(0, LARGE_LIMIT, null, null, entitySearch, { enabled: hasQuery });
  const users = useUsers(0, SMALL_LIMIT, undefined, undefined, { enabled: isAdmin && hasQuery });

  return useMemo<EntityResultGroup[]>(() => {
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

    const itemTemplateData =
      (itemTemplates.data as ItemTemplateLike[] | undefined) ?? [];
    if (itemTemplateData.length) {
      groups.push({
        id: "item-templates",
        heading: "Item Templates",
        icon: Package,
        items: itemTemplateData.map((t) => ({
          id: t.id,
          label: t.name,
          meta: t.defaultCategory ?? t.description ?? undefined,
          searchValue: tokens("template", t.name, t.description, t.defaultCategory),
          href: "/item-templates",
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
          meta: [i.receiptItemCode, i.category].filter(Boolean).join(" · ") || undefined,
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
            const fullName = [u.firstName, u.lastName].filter(Boolean).join(" ");
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

    return groups;
  }, [
    accounts.data,
    cards.data,
    categories.data,
    subcategories.data,
    itemTemplates.data,
    receipts.data,
    receiptItems.data,
    users.data,
    isAdmin,
  ]);
}
