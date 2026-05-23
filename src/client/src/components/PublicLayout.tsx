import { Link, Outlet } from "react-router";
import { ThemeToggle } from "@/components/ThemeToggle";

export function PublicLayout() {
  return (
    <div className="auth-shell">
      <header className="auth-topbar">
        <Link to="/" className="brand">
          <div className="mark">R</div>
          <div className="name">Receipts</div>
        </Link>
        <ThemeToggle />
      </header>
      <main className="auth-main">
        <Outlet />
      </main>
    </div>
  );
}
