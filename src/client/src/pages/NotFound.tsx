import { Link, useNavigate } from "react-router";
import { usePageTitle } from "@/hooks/usePageTitle";

function NotFound() {
  usePageTitle("Page Not Found");
  const navigate = useNavigate();

  return (
    <div className="page">
      <div className="err-shell">
        <div>
          <div className="err-code" aria-hidden="true">
            404
          </div>
          <div className="err-ti">This page left the counter</div>
          <div className="err-sub">
            That route doesn’t exist — or it got renamed in a past migration.
            Head back to the dashboard and let’s pretend this never happened.
          </div>
          <div
            style={{ display: "flex", gap: 8, justifyContent: "center" }}
          >
            <button
              type="button"
              className="btn"
              onClick={() => navigate(-1)}
            >
              ← Back
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

export default NotFound;
