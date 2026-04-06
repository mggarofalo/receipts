import { useState } from "react";
import {
  Link,
  Outlet,
  useNavigate,
  useNavigation,
  useLocation,
} from "react-router";
import { Menu, Search } from "lucide-react";
import { useAuth } from "@/hooks/useAuth";
import { usePermission } from "@/hooks/usePermission";
import { useSignalR } from "@/hooks/useSignalR";
import type { SignalRConnectionState } from "@/hooks/useSignalR";
import { useGlobalShortcuts } from "@/hooks/useGlobalShortcuts";
import { ShortcutsHelp } from "@/components/ShortcutsHelp";
import { Breadcrumbs } from "@/components/Breadcrumbs";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { GlobalSearchDialog } from "@/components/GlobalSearchDialog";
import { ThemeToggle } from "@/components/ThemeToggle";
import {
  NavigationMenu,
  NavigationMenuContent,
  NavigationMenuItem,
  NavigationMenuLink,
  NavigationMenuList,
  NavigationMenuTrigger,
  navigationMenuTriggerStyle,
} from "@/components/ui/navigation-menu";
import { cn } from "@/lib/utils";

const appVersion = __APP_VERSION__;
const commitHash = __COMMIT_HASH__;
const showVersion = appVersion !== "dev" && commitHash !== "local";
const shortHash = commitHash.slice(0, 7);
const commitUrl = `https://github.com/mggarofalo/Receipts/commit/${commitHash}`;

const connectionStateColors: Record<SignalRConnectionState, string> = {
  connected: "bg-green-500",
  reconnecting: "bg-yellow-500 animate-pulse",
  disconnected: "bg-red-500",
};

const connectionStateLabels: Record<SignalRConnectionState, string> = {
  connected: "Live",
  reconnecting: "Reconnecting",
  disconnected: "Offline",
};

interface NavGroupItem {
  to: string;
  label: string;
  description: string;
}

interface NavGroup {
  label: string;
  items: NavGroupItem[];
}

const navGroups: NavGroup[] = [
  {
    label: "Data",
    items: [
      {
        to: "/receipts",
        label: "Receipts",
        description: "View and manage receipts",
      },
      {
        to: "/reports",
        label: "Reports",
        description: "View analytical reports",
      },
    ],
  },
  {
    label: "Manage",
    items: [
      {
        to: "/accounts",
        label: "Accounts",
        description: "Manage financial accounts",
      },
      {
        to: "/categories",
        label: "Categories",
        description: "Manage item categories",
      },
      {
        to: "/subcategories",
        label: "Subcategories",
        description: "Manage item subcategories",
      },
      {
        to: "/item-templates",
        label: "Item Templates",
        description: "Manage item templates for autocomplete",
      },
      {
        to: "/security",
        label: "Security",
        description: "Security settings and sessions",
      },
      {
        to: "/settings/ynab",
        label: "YNAB",
        description: "YNAB sync settings",
      },
    ],
  },
];

const adminNavGroup: NavGroup = {
  label: "Admin",
  items: [
    { to: "/admin/users", label: "Users", description: "Manage user accounts" },
    { to: "/audit", label: "Audit", description: "View audit logs" },
    { to: "/trash", label: "Trash", description: "Recover deleted items" },
    { to: "/admin/backup", label: "Backup", description: "Export and import data backups" },
  ],
};

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

  function isLinkActive(to: string) {
    return to === "/"
      ? location.pathname === "/"
      : location.pathname.startsWith(to);
  }

  function mobileNavLink(to: string, label: string) {
    return (
      <Link
        key={to}
        to={to}
        onClick={() => setMobileOpen(false)}
        className={`block rounded-md px-3 py-2 text-sm transition-colors ${
          isLinkActive(to)
            ? "bg-accent text-accent-foreground font-medium"
            : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
        }`}
      >
        {label}
      </Link>
    );
  }

  function renderNavGroup(group: NavGroup) {
    const groupActive = group.items.some(({ to }) => isLinkActive(to));
    return (
      <NavigationMenuItem key={group.label}>
        <NavigationMenuTrigger
          className={cn("px-2 md:px-4", groupActive && "text-accent-foreground")}
        >
          {group.label}
        </NavigationMenuTrigger>
        <NavigationMenuContent>
          <ul className="grid w-[250px] gap-1 p-2">
            {group.items.map(({ to, label, description }) => (
              <li key={to}>
                <NavigationMenuLink asChild active={isLinkActive(to)}>
                  <Link to={to}>
                    <span className="font-medium leading-none">{label}</span>
                    <span className="text-muted-foreground text-xs leading-snug">
                      {description}
                    </span>
                  </Link>
                </NavigationMenuLink>
              </li>
            ))}
          </ul>
        </NavigationMenuContent>
      </NavigationMenuItem>
    );
  }

  async function handleLogout() {
    await logout();
    navigate("/login");
  }

  const navLinks = [
    { to: "/", label: "Dashboard" },
    { to: "/accounts", label: "Accounts" },
    { to: "/categories", label: "Categories" },
    { to: "/subcategories", label: "Subcategories" },
    { to: "/receipts", label: "Receipts" },
    { to: "/reports", label: "Reports" },
    { to: "/item-templates", label: "Item Templates" },
    { to: "/security", label: "Security" },
    { to: "/settings/ynab", label: "YNAB" },
  ];

  const adminLinks = [
    { to: "/admin/users", label: "Users" },
    { to: "/audit", label: "Audit" },
    { to: "/trash", label: "Trash" },
    { to: "/admin/backup", label: "Backup" },
  ];

  return (
    <div className="min-h-screen flex flex-col">
      <a href="#main-content" className="skip-link">
        Skip to main content
      </a>
      <header className="border-b">
        <div className="container mx-auto flex h-14 items-center justify-between px-4">
          {/* Phone: hamburger + brand (below sm only) */}
          <div className="flex items-center gap-2 sm:hidden">
            <Button
              variant="ghost"
              size="icon"
              className="h-8 w-8"
              onClick={() => setMobileOpen(true)}
              aria-label="Open navigation menu"
            >
              <Menu className="h-5 w-5" />
            </Button>
            <Link to="/" className="font-semibold text-lg">
              Receipts
            </Link>
          </div>

          {/* Horizontal nav (visible sm+) */}
          <nav
            className="hidden sm:flex items-center gap-1 md:gap-2"
            aria-label="Main navigation"
          >
            <Link to="/" className="font-semibold text-lg mr-1 md:mr-2">
              Receipts
            </Link>
            <Separator orientation="vertical" className="h-6" />
            <NavigationMenu viewport={false}>
              <NavigationMenuList>
                <NavigationMenuItem>
                  <NavigationMenuLink asChild>
                    <Link
                      to="/"
                      className={cn(
                        navigationMenuTriggerStyle(),
                        "px-2 md:px-4",
                        isLinkActive("/") &&
                          "bg-accent/50 text-accent-foreground",
                      )}
                    >
                      Dashboard
                    </Link>
                  </NavigationMenuLink>
                </NavigationMenuItem>
                {navGroups.map(renderNavGroup)}
                {isAdmin() && renderNavGroup(adminNavGroup)}
              </NavigationMenuList>
            </NavigationMenu>
          </nav>

          {/* Right-side actions (sm+: responsive layout) */}
          <div className="hidden sm:flex items-center gap-1 md:gap-3">
            <Button
              variant="ghost"
              size="icon"
              className="h-8 w-8 md:hidden"
              onClick={() => setSearchOpen(true)}
              aria-label="Search"
            >
              <Search className="h-4 w-4" />
            </Button>
            <Button
              variant="outline"
              size="sm"
              className="hidden md:inline-flex gap-1.5 text-muted-foreground"
              onClick={() => setSearchOpen(true)}
            >
              <Search className="h-3.5 w-3.5" />
              Search
              <kbd className="pointer-events-none select-none rounded border bg-muted px-1.5 py-0.5 text-[10px] font-medium">
                Ctrl+K
              </kbd>
            </Button>
            <div
              className="flex items-center gap-1.5"
              role="status"
              aria-live="polite"
              title={connectionStateLabels[connectionState]}
            >
              <span
                className={`h-2 w-2 rounded-full ${connectionStateColors[connectionState]}`}
                aria-hidden="true"
              />
              <span className="hidden md:inline text-xs text-muted-foreground">
                {connectionStateLabels[connectionState]}
              </span>
              <span className="sr-only md:hidden">
                {connectionStateLabels[connectionState]}
              </span>
            </div>

            <ThemeToggle />

            {user && (
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`User menu for ${user.email || "current user"}`}
                  >
                    <span className="hidden md:inline">{user.email}</span>
                    <span className="md:hidden text-xs font-semibold">
                      {user.email ? user.email[0].toUpperCase() : "?"}
                    </span>
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem onClick={() => navigate("/api-keys")}>
                    API Keys
                  </DropdownMenuItem>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onClick={handleLogout}>
                    Logout
                  </DropdownMenuItem>
                  {showVersion && (
                    <>
                      <DropdownMenuSeparator />
                      <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">
                        {appVersion} &middot;{" "}
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

          {/* Phone-only: compact actions */}
          <div className="flex items-center gap-1 sm:hidden">
            <Button
              variant="ghost"
              size="icon"
              className="h-8 w-8"
              onClick={() => setSearchOpen(true)}
              aria-label="Search"
            >
              <Search className="h-4 w-4" />
            </Button>
            <ThemeToggle />
          </div>
        </div>
      </header>

      {navigation.state === "loading" && (
        <div className="h-0.5 w-full overflow-hidden bg-muted">
          <div className="h-full w-1/3 animate-pulse bg-primary" />
        </div>
      )}

      <main
        id="main-content"
        tabIndex={-1}
        className="flex-1 container mx-auto px-4 pt-6 pb-16 focus:outline-none"
      >
        <Breadcrumbs />
        <div
          key={location.pathname}
          className="animate-in fade-in duration-200"
        >
          <Outlet />
        </div>
      </main>

      {/* Phone navigation drawer (below sm only) */}
      <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
        <SheetContent side="left" className="w-72">
          <SheetHeader>
            <SheetTitle>Receipts</SheetTitle>
          </SheetHeader>
          <nav
            className="min-h-0 flex-1 overflow-y-auto flex flex-col gap-1 px-4"
            aria-label="Mobile navigation"
          >
            {navLinks.map(({ to, label }) => mobileNavLink(to, label))}
            {isAdmin() && (
              <>
                <Separator className="my-2" />
                <span className="px-3 py-1 text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  Admin
                </span>
                {adminLinks.map(({ to, label }) => mobileNavLink(to, label))}
              </>
            )}
          </nav>
          <Separator className="mx-4" />
          <div className="flex flex-col gap-2 px-4">
            <div
              className="flex items-center gap-1.5 px-3 py-1"
              role="status"
              aria-live="polite"
            >
              <span
                className={`h-2 w-2 rounded-full ${connectionStateColors[connectionState]}`}
                aria-hidden="true"
              />
              <span className="text-xs text-muted-foreground">
                {connectionStateLabels[connectionState]}
              </span>
            </div>
            {user && (
              <>
                <Link
                  to="/api-keys"
                  onClick={() => setMobileOpen(false)}
                  className="block rounded-md px-3 py-2 text-sm text-muted-foreground hover:bg-accent hover:text-accent-foreground transition-colors"
                >
                  API Keys
                </Link>
                <button
                  onClick={() => {
                    setMobileOpen(false);
                    handleLogout();
                  }}
                  className="rounded-md px-3 py-2 text-left text-sm text-muted-foreground hover:bg-accent hover:text-accent-foreground transition-colors"
                >
                  Logout
                </button>
              </>
            )}
            {showVersion && (
              <p className="px-3 py-2 text-xs text-muted-foreground">
                {appVersion} &middot;{" "}
                <a
                  href={commitUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="hover:underline"
                >
                  {shortHash}
                </a>
              </p>
            )}
          </div>
        </SheetContent>
      </Sheet>

      <GlobalSearchDialog open={searchOpen} onOpenChange={setSearchOpen} />
      <ShortcutsHelp />
    </div>
  );
}
