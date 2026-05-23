import { Link, useNavigate } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";

/**
 * Editorial 500 page (RECEIPTS-740). Reachable on the first 5xx response in
 * a tab session — subsequent 5xx surface as toasts only. Reuses the
 * `.err-shell` block from the design system that NotFound already uses.
 */
function ServerError() {
  usePageTitle("Something broke");
  const navigate = useNavigate();

  return (
    <div className="page">
      <div className="err-shell">
        <div>
          <div className="err-code" aria-hidden="true">
            500
          </div>
          <div className="err-ti">The kitchen is closed</div>
          <div className="err-sub">
            Something on our side caught fire. We logged it; if you saw
            unsaved work disappear, that’s the explanation. Try the page
            again, or head back to the dashboard.
          </div>
          <div
            style={{ display: "flex", gap: 8, justifyContent: "center" }}
          >
            <button
              type="button"
              className="btn"
              onClick={() => navigate(-1)}
            >
              ← Try again
            </button>
            <Link to="/" className="btn primary">
              Dashboard
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}

export default ServerError;
