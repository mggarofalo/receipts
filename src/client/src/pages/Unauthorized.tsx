import { Link } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";

/**
 * Editorial 401 page (RECEIPTS-740). Note that most 401 cases route through
 * `/login` directly (via `setLoginFlash` in the api-client). This route is
 * reachable for the explicit case where a user navigates to a protected URL
 * without an authenticated session and the app chooses to land them here
 * instead of `/login` — e.g. a deep-link they followed from email.
 */
function Unauthorized() {
  usePageTitle("Sign in required");

  return (
    <div className="page">
      <div className="err-shell">
        <div>
          <div className="err-code" aria-hidden="true">
            401
          </div>
          <div className="err-ti">You need to sign in</div>
          <div className="err-sub">
            This page is only available to signed-in members of the
            household. Sign in to keep going — we’ll send you back to where
            you were trying to go.
          </div>
          <div
            style={{ display: "flex", gap: 8, justifyContent: "center" }}
          >
            <Link to="/login" className="btn primary">
              Sign in
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}

export default Unauthorized;
