import { useSearchParams } from "react-router";
import { ChatGptTab } from "./normalized-descriptions/ChatGptTab";
import { PageHead } from "@/components/primitives";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { MaintenanceTab } from "./normalized-descriptions/MaintenanceTab";
import { RegistryTab } from "./normalized-descriptions/RegistryTab";
import { ReviewQueueTab } from "./normalized-descriptions/ReviewQueueTab";
import { SettingsTab } from "./normalized-descriptions/SettingsTab";
import { CatalogTab } from "./normalized-descriptions/CatalogTab";

type TabKey =
  | "review"
  | "registry"
  | "settings"
  | "maintenance"
  | "chatgpt"
  | "catalog";

export default function NormalizedDescriptions() {
  usePageTitle("Normalized Descriptions");
  const { isAdmin } = usePermission();
  const [params, setParams] = useSearchParams();
  const requestedTab = params.get("tab");
  const knownTabs: TabKey[] = [
    "review",
    "registry",
    "settings",
    "maintenance",
    "chatgpt",
    "catalog",
  ];
  const tab =
    requestedTab && knownTabs.includes(requestedTab as TabKey)
      ? requestedTab
      : params.has("catalogOperation")
        ? "catalog"
        : params.has("aiRun")
          ? "chatgpt"
          : "review";

  return (
    <>
      <PageHead
        title="Normalized Descriptions"
        sub="Build consistent product names for search and reports, and maintain mappings for new purchases"
      />
      <Tabs
        value={tab}
        onValueChange={(value) =>
          setParams((previous) => {
            const next = new URLSearchParams(previous);
            next.set("tab", value);
            return next;
          })
        }
        className="space-y-4"
      >
        <TabsList className="h-auto flex-wrap">
          {isAdmin() && <TabsTrigger value="catalog">Catalog</TabsTrigger>}
          <TabsTrigger value="review">Review Queue</TabsTrigger>
          <TabsTrigger value="registry">Registry</TabsTrigger>
          {isAdmin() && <TabsTrigger value="chatgpt">ChatGPT</TabsTrigger>}
          {isAdmin() && <TabsTrigger value="settings">Settings</TabsTrigger>}
          {isAdmin() && (
            <TabsTrigger value="maintenance">Maintenance</TabsTrigger>
          )}
        </TabsList>
        {isAdmin() && (
          <TabsContent value="catalog">
            <CatalogTab />
          </TabsContent>
        )}
        <TabsContent value="review">
          <ReviewQueueTab />
        </TabsContent>
        {isAdmin() && (
          <TabsContent value="chatgpt">
            <ChatGptTab />
          </TabsContent>
        )}
        <TabsContent value="registry">
          <RegistryTab />
        </TabsContent>
        {isAdmin() && (
          <TabsContent value="settings">
            <SettingsTab />
          </TabsContent>
        )}
        {isAdmin() && (
          <TabsContent value="maintenance">
            <MaintenanceTab />
          </TabsContent>
        )}
      </Tabs>
    </>
  );
}
