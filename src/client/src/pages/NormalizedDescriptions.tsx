import { useSearchParams } from "react-router";
import { ChatGptTab } from "./normalized-descriptions/ChatGptTab";
import { PageHead } from "@/components/primitives";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { useState } from "react";
import { MaintenanceTab } from "./normalized-descriptions/MaintenanceTab";
import { RegistryTab } from "./normalized-descriptions/RegistryTab";
import { ReviewQueueTab } from "./normalized-descriptions/ReviewQueueTab";
import { SettingsTab } from "./normalized-descriptions/SettingsTab";

type TabKey = "review" | "registry" | "settings" | "maintenance" | "chatgpt";

export default function NormalizedDescriptions() {
  usePageTitle("Normalized Descriptions");
  const { isAdmin } = usePermission();
  const [params] = useSearchParams();
  const [tab, setTab] = useState<TabKey>(() =>
    params.has("aiRun") ? "chatgpt" : "review",
  );

  return (
    <>
      <PageHead
        title="Normalized Descriptions"
        sub="Review, merge, and configure canonical item descriptions"
      />
      <Tabs
        value={tab}
        onValueChange={(v) => setTab(v as TabKey)}
        className="space-y-4"
      >
        <TabsList className="h-auto flex-wrap">
          <TabsTrigger value="review">Review Queue</TabsTrigger>
          <TabsTrigger value="registry">Registry</TabsTrigger>
          {isAdmin() && <TabsTrigger value="chatgpt">ChatGPT</TabsTrigger>}
          {isAdmin() && <TabsTrigger value="settings">Settings</TabsTrigger>}
          {isAdmin() && (
            <TabsTrigger value="maintenance">Maintenance</TabsTrigger>
          )}
        </TabsList>
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
