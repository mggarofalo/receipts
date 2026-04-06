# Route Matrix

All routes in the Receipts app with their expected headings, access levels, and layout contexts.

## Public Routes (PublicLayout)

| # | Path | Page | Expected H1 | Access |
|---|------|------|-------------|--------|
| 1 | `/login` | Login | Sign In | Public |
| 2 | `/change-password` | ChangePassword | Change Password | Authenticated (must-reset only) |

## Protected Routes (Layout, requires auth)

| # | Path | Page | Expected H1 | Access |
|---|------|------|-------------|--------|
| 3 | `/` | Dashboard | Dashboard | User |
| 4 | `/accounts` | Accounts | Accounts | User |
| 5 | `/categories` | Categories | Categories | User |
| 6 | `/subcategories` | Subcategories | Subcategories | User |
| 7 | `/receipts` | Receipts | Receipts | User |
| 8 | `/receipts/new` | NewReceipt | New Receipt | User |
| 9 | `/receipts/scan` | ScanReceipt | Scan Receipt | User |
| 10 | `/receipts/:id` | ReceiptDetail | Receipt Details | User |
| 11 | `/item-templates` | ItemTemplates | Item Templates | User |
| 12 | `/reports` | Reports | Reports | User |
| 13 | `/api-keys` | ApiKeys | API Keys | User |
| 14 | `/security` | SecurityLog | Security Log | User |

## Admin Routes (AdminRoute, requires admin role)

| # | Path | Page | Expected H1 | Access |
|---|------|------|-------------|--------|
| 15 | `/audit` | AuditLog | Audit Log | Admin |
| 16 | `/trash` | RecycleBin | Recycle Bin | Admin |
| 17 | `/admin/users` | AdminUsers | User Management | Admin |
| 18 | `/admin/backup` | BackupRestore | Backup & Restore | Admin |

## Catch-All

| Path | Page | Expected H1 |
|------|------|-------------|
| `/*` (any unmatched) | NotFound | Page Not Found |

## Route Notes

- **`/change-password`** only renders the form when `mustResetPassword` is true. If the user doesn't need a password change, it redirects to `/`. For smoke testing, just verify the URL doesn't error -- don't expect the form to be visible.
- **`/receipts/:id`** requires a valid receipt UUID in the path. Without a valid ID, it shows a loading state or error. Verify the page loads without errors using a known receipt ID.
- **`/receipts/new`** is the 4-step wizard. For smoke testing, just verify step 1 loads with the "Trip Details" card.
- **`/receipts/scan`** is the OCR receipt scanning page. Verify the page loads with the upload area.
- **Admin routes** (`/audit`, `/trash`, `/admin/users`, `/admin/backup`) require the logged-in user to have an admin role. The seeded `admin@receipts.local` user is an admin.
- **Auth guard behavior**: Unauthenticated access to protected routes redirects to `/login`. Non-admin access to admin routes redirects to `/` (dashboard).

## Removed Routes (historical)

The following routes were removed as part of RECEIPTS-504. They previously redirected to `/receipts`:

- `/receipt-items` -- consolidated into `/receipts`
- `/transactions` -- consolidated into `/receipts`
- `/trips` -- consolidated into `/receipts`
- `/receipt-detail` -- replaced by `/receipts/:id`
- `/transaction-detail` -- consolidated into `/receipts`
