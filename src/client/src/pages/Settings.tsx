import { useCallback, useMemo } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useAppearance } from "@/hooks/useAppearance";
import { usePermission } from "@/hooks/usePermission";
import { usePreferences } from "@/hooks/usePreferences";
import { Icon, PageHead } from "@/components/primitives";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";

type SettingsTab = "appearance" | "preferences" | "export" | "data" | "ynab";

const TABS: { value: SettingsTab; label: string; adminOnly?: boolean }[] = [
  { value: "appearance", label: "Appearance" },
  { value: "preferences", label: "Preferences" },
  { value: "export", label: "Export" },
  { value: "data", label: "Data & backup", adminOnly: true },
  { value: "ynab", label: "YNAB" },
];

const DEFAULT_TAB: SettingsTab = "appearance";

function isSettingsTab(v: string): v is SettingsTab {
  return TABS.some((t) => t.value === v);
}

function Settings() {
  usePageTitle("Settings");
  const location = useLocation();
  const navigate = useNavigate();
  const { isAdmin } = usePermission();
  const { palette, density, setPalette, setDensity } = useAppearance();
  const { preferences, setWeekStart, setShowKeyboardHints } = usePreferences();

  const visibleTabs = useMemo(
    () => TABS.filter((t) => !t.adminOnly || isAdmin()),
    [isAdmin],
  );

  // Hash drives the active tab so URLs like /settings#preferences land
  // directly on the right surface. Strip the leading '#'.
  const hash = location.hash.replace(/^#/, "");
  const activeTab: SettingsTab = isSettingsTab(hash) ? hash : DEFAULT_TAB;

  const handleTabChange = useCallback(
    (value: string) => {
      navigate(`/settings#${value}`, { replace: true });
    },
    [navigate],
  );

  return (
    <>
      <PageHead title="Settings" sub="Configure your workspace" />

      <Tabs
        value={activeTab}
        onValueChange={handleTabChange}
        className="w-full"
      >
        {/* Five triggers measure ~453px, so the strip overflowed a 375px
            viewport and dragged the whole page into horizontal scroll.
            Wrapping keeps every tab reachable without a scroll affordance. */}
        <TabsList className="h-auto flex-wrap">
          {visibleTabs.map((tab) => (
            <TabsTrigger key={tab.value} value={tab.value}>
              {tab.label}
            </TabsTrigger>
          ))}
        </TabsList>

        <TabsContent value="appearance" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle>Appearance</CardTitle>
              <CardDescription>
                Palette and density preferences sync across reloads in this
                browser. Paper intensity and motion are no longer
                user-configurable.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <fieldset className="space-y-2">
                <legend className="text-sm font-medium">Palette</legend>
                <div className="flex flex-wrap gap-2">
                  {(["graphite", "paper"] as const).map((opt) => (
                    <button
                      key={opt}
                      type="button"
                      className={`btn xs ${palette === opt ? "primary" : ""}`}
                      aria-pressed={palette === opt}
                      onClick={() => setPalette(opt)}
                    >
                      {opt[0].toUpperCase() + opt.slice(1)}
                    </button>
                  ))}
                </div>
              </fieldset>

              <fieldset className="space-y-2">
                <legend className="text-sm font-medium">Density</legend>
                <div className="flex flex-wrap gap-2">
                  {(["compact", "comfortable", "spacious"] as const).map(
                    (opt) => (
                      <button
                        key={opt}
                        type="button"
                        className={`btn xs ${density === opt ? "primary" : ""}`}
                        aria-pressed={density === opt}
                        onClick={() => setDensity(opt)}
                      >
                        {opt[0].toUpperCase() + opt.slice(1)}
                      </button>
                    ),
                  )}
                </div>
              </fieldset>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="preferences" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle>Preferences</CardTitle>
              <CardDescription>
                Per-browser preferences that don't sync across devices yet.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <fieldset className="space-y-2">
                <legend className="text-sm font-medium">Week starts on</legend>
                <div className="flex flex-wrap gap-2">
                  {(["sunday", "monday"] as const).map((opt) => (
                    <button
                      key={opt}
                      type="button"
                      className={`btn xs ${preferences.weekStart === opt ? "primary" : ""}`}
                      aria-pressed={preferences.weekStart === opt}
                      onClick={() => setWeekStart(opt)}
                    >
                      {opt[0].toUpperCase() + opt.slice(1)}
                    </button>
                  ))}
                </div>
              </fieldset>

              <div className="flex items-start justify-between gap-4">
                <div>
                  <Label
                    htmlFor="show-keyboard-hints"
                    className="text-sm font-medium"
                  >
                    Keyboard hints
                  </Label>
                  <p className="text-sm text-muted-foreground">
                    Show ⌘K and shortcut chips in the topbar and nav.
                  </p>
                </div>
                <Switch
                  id="show-keyboard-hints"
                  checked={preferences.showKeyboardHints}
                  onCheckedChange={setShowKeyboardHints}
                />
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="export" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle>Export</CardTitle>
              <CardDescription>
                Download report CSVs or use the existing backup tools.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-6">
              <section aria-labelledby="report-exports-title">
                <h3 id="report-exports-title" className="font-medium">
                  Report CSVs
                </h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Open a report, choose its filters, then select Export CSV.
                </p>
                <ul className="mt-2 space-y-1 text-sm">
                  {[
                    ["spending-by-location", "Spending by Location"],
                    [
                      "spending-by-normalized-description",
                      "Spending by Normalized Description",
                    ],
                    ["out-of-balance", "Out of Balance"],
                    ["duplicate-detection", "Duplicate Detection"],
                    ["uncategorized-items", "Uncategorized Items"],
                  ].map(([slug, label]) => (
                    <li key={slug}>
                      <Link
                        to={`/reports?report=${slug}`}
                        className="underline"
                      >
                        {label}
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
              {isAdmin() && (
                <section aria-labelledby="database-export-title">
                  <h3 id="database-export-title" className="font-medium">
                    Portable backup
                  </h3>
                  <p className="mt-1 text-sm text-muted-foreground">
                    Download a SQLite backup of application data for restoring
                    or moving your data.
                  </p>
                  <Link
                    to="/admin/backup"
                    className="mt-2 inline-block text-sm underline"
                  >
                    Backup &amp; Restore
                  </Link>
                </section>
              )}
              <section aria-labelledby="programmatic-export-title">
                <h3 id="programmatic-export-title" className="font-medium">
                  Programmatic access
                </h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Create API credentials for custom data exports at{" "}
                  <Link to="/api-keys" className="underline">
                    API Keys
                  </Link>
                  .
                </p>
              </section>
            </CardContent>
          </Card>
        </TabsContent>

        {isAdmin() && (
          <TabsContent value="data" className="mt-4">
            <Card>
              <CardHeader>
                <CardTitle>Data &amp; backup</CardTitle>
                <CardDescription>
                  Backup, restore, and bulk data operations.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <p className="text-sm text-muted-foreground">
                  Backup &amp; restore lives at{" "}
                  <Link to="/admin/backup" className="underline">
                    Backup &amp; restore
                  </Link>
                  .
                </p>
              </CardContent>
            </Card>
          </TabsContent>
        )}

        <TabsContent value="ynab" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle>YNAB</CardTitle>
              <CardDescription>
                Personal-access-token + budget selection.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <p className="text-sm text-muted-foreground">
                Manage YNAB settings at{" "}
                <Link to="/settings/ynab" className="underline">
                  YNAB settings
                </Link>
                .
              </p>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>

      <div style={{ marginTop: 20 }} className="text-xs text-muted-foreground">
        <Icon.Settings /> Settings live here. Tabs sync with the URL hash so you
        can deep-link to a specific section.
      </div>
    </>
  );
}

export default Settings;
