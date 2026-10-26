import { Navigate, useLocation } from "react-router";

export function CategoryRedirect({
  preserveRevision = false,
}: {
  preserveRevision?: boolean;
}) {
  const { search, hash } = useLocation();
  return (
    <Navigate
      to={{
        pathname: "/categories",
        search: preserveRevision ? search : "",
        hash,
      }}
      replace
    />
  );
}
