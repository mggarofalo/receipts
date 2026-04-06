import { ProtectedRoute } from "@/components/ProtectedRoute";
import { AdminRoute } from "@/components/AdminRoute";
import { Layout } from "@/components/Layout";
import { PublicLayout } from "@/components/PublicLayout";
import { RootLayout } from "@/components/RootLayout";
import Dashboard from "@/pages/Dashboard";
import Login from "@/pages/Login";
import ChangePassword from "@/pages/ChangePassword";
import ApiKeys from "@/pages/ApiKeys";
import Accounts from "@/pages/Accounts";
import Categories from "@/pages/Categories";
import Subcategories from "@/pages/Subcategories";
import Receipts from "@/pages/Receipts";
import ItemTemplates from "@/pages/ItemTemplates";
import Reports from "@/pages/Reports";
import ReceiptDetail from "@/pages/ReceiptDetail";
import AdminUsers from "@/pages/AdminUsers";
import AuditLog from "@/pages/AuditLog";
import SecurityLog from "@/pages/SecurityLog";
import RecycleBin from "@/pages/RecycleBin";
import BackupRestore from "@/pages/BackupRestore";
import NewReceipt from "@/pages/new-receipt/NewReceiptPage";
import ScanReceipt from "@/pages/scan-receipt/ScanReceiptPage";
import NotFound from "@/pages/NotFound";

export const routeConfig = [
  {
    element: <RootLayout />,
    children: [
      {
        element: <PublicLayout />,
        children: [
          { path: "/login", element: <Login /> },
          { path: "/change-password", element: <ChangePassword /> },
        ],
      },
      {
        element: (
          <ProtectedRoute>
            <Layout />
          </ProtectedRoute>
        ),
        children: [
          { path: "/", element: <Dashboard /> },
          { path: "/accounts", element: <Accounts /> },
          { path: "/categories", element: <Categories /> },
          { path: "/subcategories", element: <Subcategories /> },
          { path: "/receipts", element: <Receipts /> },
          { path: "/receipts/new", element: <NewReceipt /> },
          { path: "/receipts/scan", element: <ScanReceipt /> },
          { path: "/item-templates", element: <ItemTemplates /> },
          { path: "/reports", element: <Reports /> },
          { path: "/receipts/:id", element: <ReceiptDetail /> },
          { path: "/api-keys", element: <ApiKeys /> },
          { path: "/security", element: <SecurityLog /> },
          {
            path: "/audit",
            element: (
              <AdminRoute>
                <AuditLog />
              </AdminRoute>
            ),
          },
          {
            path: "/trash",
            element: (
              <AdminRoute>
                <RecycleBin />
              </AdminRoute>
            ),
          },
          {
            path: "/admin/users",
            element: (
              <AdminRoute>
                <AdminUsers />
              </AdminRoute>
            ),
          },
          {
            path: "/admin/backup",
            element: (
              <AdminRoute>
                <BackupRestore />
              </AdminRoute>
            ),
          },
        ],
      },
      { path: "*", element: <NotFound /> },
    ],
  },
];

