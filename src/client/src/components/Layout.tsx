import { useCallback, useState, useMemo } from "react";
import {
  Link,
  NavLink,
  Outlet,
  useNavigate,
  useNavigation,
  useLocation,
} from "react-router";
import { Icon, Kbd, type IconComponent } from "@/components/primitives";
import { useAuth } from "@/hooks/useAuth";
import { usePermission } from "@/hooks/usePermission";
import { useSignalR } from "@/hooks/useSignalR";
import type { SignalRConnectionState } from "@/hooks/useSignalR";
import { useGlobalShortcuts } from "@/hooks/useGlobalShortcuts";
import { useKeyboardShortcut } from "@/hooks/useKeyboardShortcut";
import { ShortcutsHelp } from "@/components/ShortcutsHelp";
import { Breadcrumbs } from "@/components/Breadcrumbs";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { CommandPalette } from "@/components/CommandPalette";
import { ThemeToggle } from "@/components/ThemeToggle";
import { cn } from "@/lib/utils";

const appVersion = __APP_VERSION__;
const commitHash = __COMMIT_HASH__;
const showVersion = appVersion !== "dev" && commitHash !== "local";
const shortHash = commitHash.slice(0, 7);
const commitUrl = `https://github.com/mggarofalo/Receipts/commit/${commitHash}`;

interface NavItem {
  to: string;
  label: string;
  icon: IconComponent;
  kbd?: string;
  aliases?: readonly string[];
  admin?: boolean;
}

interface NavSection {
  title: string;
  items: readonly NavItem[];
}

const NAV: readonly NavSection[] = [
  {
    title: "Workspace",
    items: [
      { to: "/", label: "Dashboard", icon: Icon.Dashboard, kbd: "G D" },
      {
        to: "/receipts",
        label: "Receipts",
        icon: Icon.Receipt,
        kbd: "G R",
        aliases: ["/receipts/new", "/receipts/"],
      },
      { to: "/reports", label: "Reports", icon: Icon.Chart, kbd: "G P" },
    ],
  },
  {
    title: "Library",
    items: [
      { to: "/accounts", label: "Accounts", icon: Icon.Wallet },
      { to: "/cards", label: "Cards", icon: Icon.Card },
      { to: "/categories", label: "Categories", icon: Icon.Tag },
      { to: "/subcategories", label: "Subcategories", icon: Icon.Tag },
      { to: "/item-templates", label: "Templates", icon: Icon.Sparkle },
      { to: "/settings/ynab", label: "YNAB", icon: Icon.Link },
    ],
  },
  {
    title: "Account",
    items: [
      { to: "/security", label: "Security", icon: Icon.Settings },
      { to: "/api-keys", label: "API Keys", icon: Icon.Command },
    ],
  },
  {
    title: "Admin",
    items: [
      { to: "/admin/users", label: "Users", icon: Icon.Users, admin: true },
      { to: "/audit", label: "Audit", icon: Icon.Clock, admin: true },
      { to: "/trash", label: "Trash", icon: Icon.Trash, admin: true },
      { to: "/admin/backup", label: "Backup", icon: Icon.Upload, admin: true },
    ],
  },
];

const MOBILE_TABS = [
  { to: "/", label: "Home", icon: Icon.Dashboard },
  { to: "/receipts", label: "List", icon: Icon.Receipt },
  { to: "/reports", label: "Reports", icon: Icon.Chart },
] as const;

const CONNECTION: Record<
  SignalRConnectionState,
  { className: string; label: string }
> = {
  connected: { className: "conn", label: "Live" },
  reconnecting: { className: "conn warn", label: "Reconnecting" },
  disconnected: { className: "conn neg", label: "Offline" },
};

function isLinkActive(pathname: string, item: NavItem): boolean {
  if (item.to === "/") return pathname === "/";
  if (pathname === item.to) return true;
  if (pathname.startsWith(item.to + "/")) return true;
  return (item.aliases ?? []).some(
    (alias) => pathname === alias || pathname.startsWith(alias),
  );
}

export function Layout() {
  const { user, logout } = useAuth();
  const { isAdmin } = usePermission();
  const navigate = useNavigate();
  const navigation = useNavigation();
  const location = useLocation();
  const { connectionState } = useSignalR(!!user);
  const [searchOpen, setSearchOpen] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  useGlobalShortcuts();
  const openSearch = useCallback(() => setSearchOpen(true), []);
  useKeyboardShortcut({ key: "k", handler: openSearch });

  const admin = isAdmin();
  const sections = useMemo(
    () =>
      NAV.map((section) => ({
        ...section,
        items: section.items.filter((item) => !item.admin || admin),
      })).filter((section) => section.items.length > 0),
    [admin],
  );

  const handleLogout = useCallback(async () => {
    await logout();
    navigate("/login");
  }, [logout, navigate]);

  const conn = CONNECTION[connectionState];

  return (
    <div className="app">
      <a href="#main-content" className="skip-link">
        Skip to main content
      </a>

      <aside className="sidebar" aria-label="Primary navigation">
        <Link to="/" className="brand">
          <div className="mark">R</div>
          <div className="name">Receipts</div>
          {showVersion && <div className="ver">{appVersion}</div>}
        </Link>

        {sections.map((section) => (
          <div key={section.title}>
            <div className="nav-section">{section.title}</div>
            {section.items.map((item) => {
              const IconComp = item.icon;
              const active = isLinkActive(location.pathname, item);
              return (
                <NavLink
                  key={item.to}
                  to={item.to}
                  className={cn("nav-item", active && "active")}
                  aria-current={active ? "page" : undefined}
                  end={item.to === "/"}
                >
                  <IconComp />
                  {item.label}
                  {item.kbd && <span className="kbd">{item.kbd}</span>}
                </NavLink>
              );
            })}
          </div>
        ))}

        <div
          className="sidebar-foot"
          role="status"
          aria-live="polite"
          title={conn.label}
        >
          <span className={conn.className}>
            <span className="dot" />
            {conn.label}
          </span>
          {showVersion && (
            <a
              href={commitUrl}
              target="_blank"
              rel="noopener noreferrer"
              style={{ marginLeft: "auto", color: "var(--mute-2)" }}
            >
              {shortHash}
            </a>
          )}
        </div>
      </aside>

      <div className="main">
        <div className="topbar">
          <button
            type="button"
            className="icon-btn"
            aria-label="Open navigation menu"
            onClick={() => setMobileOpen(true)}
            style={{ display: "none" }}
            data-mobile-only
          >
            <Icon.Sliders />
          </button>
          <div className="crumbs">
            <Breadcrumbs />
          </div>
          <button
            type="button"
            className="search-btn"
            onClick={openSearch}
            aria-label="Search or jump to…"
          >
            <Icon.Search />
            <span>Search or jump to…</span>
            <span
              style={{ marginLeft: "auto", display: "flex", gap: 3 }}
              aria-hidden
            >
              <Kbd>⌘</Kbd>
              <Kbd>K</Kbd>
            </span>
          </button>
          <ThemeToggle />
          {user && (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <button
                  type="button"
                  className="icon-btn"
                  aria-label={`User menu for ${user.email ?? "current user"}`}
                  style={{ width: "auto", padding: "0 10px", gap: 6 }}
                >
                  <Icon.Users />
                  <span
                    style={{
                      fontSize: 12,
                      color: "var(--ink-2)",
                      maxWidth: 140,
                      overflow: "hidden",
                      textOverflow: "ellipsis",
                      whiteSpace: "nowrap",
                    }}
                  >
                    {user.email}
                  </span>
                </button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">
                  {user.email}
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={() => navigate("/api-keys")}>
                  API Keys
                </DropdownMenuItem>
                <DropdownMenuItem onClick={() => navigate("/change-password")}>
                  Change password
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={handleLogout}>Logout</DropdownMenuItem>
                {showVersion && (
                  <>
                    <DropdownMenuSeparator />
                    <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">
                      {appVersion} ·{" "}
                      <a
                        href={commitUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="hover:underline"
                        onClick={(e) => e.stopPropagation()}
                      >
                        {shortHash}
                      </a>
                    </DropdownMenuLabel>
                  </>
                )}
              </DropdownMenuContent>
            </DropdownMenu>
          )}
        </div>

        {navigation.state === "loading" && (
          <div role="status" aria-live="polite" aria-busy="true">
            <span className="sr-only">Loading page…</span>
            <div
              aria-hidden="true"
              style={{
                height: 2,
                background:
                  "linear-gradient(90deg, transparent, var(--accent), transparent)",
                animation: "skel 1.4s ease-in-out infinite",
              }}
            />
          </div>
        )}

        <main
          id="main-content"
          tabIndex={-1}
          className="page"
          style={{ outline: "none" }}
        >
          <div
            key={location.pathname}
            className="animate-in fade-in duration-200"
          >
            <Outlet />
          </div>
        </main>

        <nav className="mobile-tabbar" aria-label="Mobile navigation">
          {MOBILE_TABS.map((tab) => {
            const TabIcon = tab.icon;
            const active =
              tab.to === "/"
                ? location.pathname === "/"
                : location.pathname.startsWith(tab.to);
            return (
              <NavLink
                key={tab.to}
                to={tab.to}
                className={cn(active && "on")}
                aria-current={active ? "page" : undefined}
                end={tab.to === "/"}
              >
                <TabIcon />
                {tab.label}
              </NavLink>
            );
          })}
          <button
            type="button"
            onClick={() => setMobileOpen(true)}
            aria-label="More navigation"
          >
            <Icon.Sliders />
            More
          </button>
        </nav>
      </div>

      <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
        <SheetContent side="left" className="w-72">
          <SheetHeader>
            <SheetTitle>
              <span className="brand" style={{ border: 0, margin: 0, padding: 0 }}>
                <span className="mark">R</span>
                <span className="name">Receipts</span>
              </span>
            </SheetTitle>
          </SheetHeader>
          <div
            style={{
              padding: "8px 12px",
              display: "flex",
              flexDirection: "column",
              gap: 4,
              overflowY: "auto",
              minHeight: 0,
              flex: 1,
            }}
          >
            {sections.map((section) => (
              <div key={section.title}>
                <div className="nav-section">{section.title}</div>
                {section.items.map((item) => {
                  const IconComp = item.icon;
                  const active = isLinkActive(location.pathname, item);
                  return (
                    <NavLink
                      key={item.to}
                      to={item.to}
                      onClick={() => setMobileOpen(false)}
                      className={cn("nav-item", active && "active")}
                      aria-current={active ? "page" : undefined}
                      end={item.to === "/"}
                    >
                      <IconComp />
                      {item.label}
                    </NavLink>
                  );
                })}
              </div>
            ))}
          </div>
          <div className="sidebar-foot" style={{ borderTop: "1px dashed var(--line)", padding: "12px 16px" }}>
            <span className={conn.className}>
              <span className="dot" />
              {conn.label}
            </span>
            {user && (
              <button
                type="button"
                onClick={() => {
                  setMobileOpen(false);
                  void handleLogout();
                }}
                className="btn xs ghost"
                style={{ marginLeft: "auto" }}
              >
                Logout
              </button>
            )}
          </div>
        </SheetContent>
      </Sheet>

      {searchOpen && (
        <CommandPalette open={searchOpen} onOpenChange={setSearchOpen} />
      )}
      <ShortcutsHelp />
    </div>
  );
}
