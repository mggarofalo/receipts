import { useState, useCallback } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import {
  useUsers,
  useCreateUser,
  useUpdateUser,
  useDeleteUser,
  useResetUserPassword,
} from "@/hooks/useUsers";
import { useAuth } from "@/hooks/useAuth";
import { usePageTitle } from "@/hooks/usePageTitle";
import { useOpenNewItem } from "@/hooks/useOpenNewItem";
import { useServerPagination } from "@/hooks/useServerPagination";
import { useServerSort } from "@/hooks/useServerSort";
import { useListKeyboardNav } from "@/hooks/useListKeyboardNav";
import { Button } from "@/components/ui/button";
import { Icon, PageHead } from "@/components/primitives";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/ui/password-input";
import { Badge } from "@/components/ui/badge";
import { Spinner } from "@/components/ui/spinner";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { TableSkeleton } from "@/components/ui/table-skeleton";
import { SortableTableHead } from "@/components/SortableTableHead";
import { Pagination } from "@/components/Pagination";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Combobox } from "@/components/ui/combobox";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { Pencil } from "lucide-react";

const ROLE_OPTIONS = [
  { value: "Admin", label: "Admin" },
  { value: "User", label: "User" },
];

const createUserSchema = z.object({
  email: z.string().email("Please enter a valid email address"),
  password: z.string().min(8, "Password must be at least 8 characters"),
  role: z.string().min(1, "Role is required"),
  firstName: z.string().optional(),
  lastName: z.string().optional(),
});

type CreateUserFormValues = z.infer<typeof createUserSchema>;

const editUserSchema = z.object({
  email: z.string().email("Please enter a valid email address"),
  role: z.string().min(1, "Role is required"),
  isDisabled: z.boolean(),
  firstName: z.string().optional(),
  lastName: z.string().optional(),
});

type EditUserFormValues = z.infer<typeof editUserSchema>;

const resetPasswordSchema = z.object({
  newPassword: z.string().min(8, "Password must be at least 8 characters"),
});

type ResetPasswordFormValues = z.infer<typeof resetPasswordSchema>;

function AdminUsers() {
  usePageTitle("User Management");
  const { user: currentUser } = useAuth();

  const { sortBy, sortDirection, toggleSort } = useServerSort({ defaultSortBy: "email", defaultSortDirection: "asc" });
  const { offset, limit, currentPage, pageSize, totalPages, setPage, setPageSize, resetPage } = useServerPagination({ defaultPageSize: 20, sortBy, sortDirection });
  const { data: usersData, total: serverTotal, isLoading } = useUsers(offset, limit, sortBy, sortDirection);

  const handleSort = useCallback((column: string) => {
    toggleSort(column);
    resetPage();
  }, [toggleSort, resetPage]);

  const createUser = useCreateUser();
  const updateUser = useUpdateUser(currentUser?.userId);
  const deleteUser = useDeleteUser();
  const resetPassword = useResetUserPassword();

  const [createOpen, setCreateOpen] = useState(false);
  const [editUser, setEditUser] = useState<{
    id: string;
    email: string;
    firstName?: string | null;
    lastName?: string | null;
    role: string;
    isDisabled: boolean;
  } | null>(null);
  const [resetUserId, setResetUserId] = useState<string | null>(null);
  const [deactivateUser, setDeactivateUser] = useState<{
    id: string;
    email: string;
  } | null>(null);

  const openCreate = useCallback(() => setCreateOpen(true), []);
  useOpenNewItem(openCreate);

  const items = usersData ?? [];

  const listItems = items.map((u) => ({ id: u.id }));
  const { focusedId, setFocusedIndex, tableRef } = useListKeyboardNav({
    items: listItems,
    getId: (u) => u.id,
    enabled: listItems.length > 0,
  });

  const createForm = useForm<CreateUserFormValues>({
    resolver: zodResolver(createUserSchema),
    defaultValues: {
      email: "",
      password: "",
      role: "User",
      firstName: "",
      lastName: "",
    },
  });

  const editForm = useForm<EditUserFormValues>({
    resolver: zodResolver(editUserSchema),
  });

  const resetForm = useForm<ResetPasswordFormValues>({
    resolver: zodResolver(resetPasswordSchema),
    defaultValues: { newPassword: "" },
  });

  async function onCreateSubmit(values: CreateUserFormValues) {
    await createUser.mutateAsync({
      email: values.email,
      password: values.password,
      role: values.role,
      firstName: values.firstName || null,
      lastName: values.lastName || null,
    });
    setCreateOpen(false);
    createForm.reset();
  }

  async function onEditSubmit(values: EditUserFormValues) {
    if (!editUser) return;
    await updateUser.mutateAsync({
      userId: editUser.id,
      body: {
        email: values.email,
        role: values.role,
        isDisabled: values.isDisabled,
        firstName: values.firstName || null,
        lastName: values.lastName || null,
      },
    });
    setEditUser(null);
  }

  async function onResetSubmit(values: ResetPasswordFormValues) {
    if (!resetUserId) return;
    await resetPassword.mutateAsync({
      userId: resetUserId,
      newPassword: values.newPassword,
    });
    setResetUserId(null);
    resetForm.reset();
  }

  function openEdit(user: (typeof items)[0]) {
    const primaryRole = user.roles[0] ?? "User";
    setEditUser({
      id: user.id,
      email: user.email,
      firstName: user.firstName,
      lastName: user.lastName,
      role: primaryRole,
      isDisabled: user.isDisabled ?? false,
    });
    editForm.reset({
      email: user.email,
      role: primaryRole,
      isDisabled: user.isDisabled,
      firstName: user.firstName ?? "",
      lastName: user.lastName ?? "",
    });
  }

  function formatDate(dateStr: string | null | undefined) {
    if (!dateStr) return "-";
    return new Date(dateStr).toLocaleDateString();
  }

  return (
    <>
      <PageHead
        title="Users"
        sub={`${serverTotal} total`}
        actions={
          <button
            type="button"
            className="btn primary"
            onClick={() => setCreateOpen(true)}
          >
            <Icon.Plus /> New user
          </button>
        }
      />
      <div className="space-y-6">

      {isLoading && <TableSkeleton rows={5} columns={7} />}

      {!isLoading && items.length === 0 && (
        <div className="py-12 text-center text-muted-foreground">
          No users found.
        </div>
      )}

      {!isLoading && items.length > 0 && (
        <>
          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
          <div className="rounded-md border" ref={tableRef}>
            <Table>
              <TableHeader>
                <TableRow>
                  <SortableTableHead column="firstName" label="Name" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <SortableTableHead column="email" label="Email" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Role</TableHead>
                  <TableHead>Status</TableHead>
                  <SortableTableHead column="createdAt" label="Created" currentSortBy={sortBy} currentSortDirection={sortDirection} onToggleSort={handleSort} />
                  <TableHead>Last Login</TableHead>
                  <TableHead className="w-[200px]">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {items.map((user, index) => {
                  const isSelf = currentUser?.email === user.email;
                  const name =
                    [user.firstName, user.lastName]
                      .filter(Boolean)
                      .join(" ") || "-";
                  return (
                    <TableRow
                      key={user.id}
                      className={`cursor-pointer ${focusedId === user.id ? "bg-accent" : ""}`}
                      onClick={(e) => {
                        if (
                          (e.target as HTMLElement).closest(
                            "button, input, a, [role='button']",
                          )
                        )
                          return;
                        setFocusedIndex(index);
                      }}
                    >
                      <TableCell className="font-medium">{name}</TableCell>
                      <TableCell>{user.email}</TableCell>
                      <TableCell>
                        {user.roles.map((role) => (
                          <Badge
                            key={role}
                            variant={
                              role === "Admin" ? "default" : "secondary"
                            }
                            className="mr-1"
                          >
                            {role}
                          </Badge>
                        ))}
                      </TableCell>
                      <TableCell>
                        <Badge
                          variant={user.isDisabled ? "destructive" : "outline"}
                        >
                          {user.isDisabled ? "Disabled" : "Active"}
                        </Badge>
                      </TableCell>
                      <TableCell>{formatDate(user.createdAt)}</TableCell>
                      <TableCell>{formatDate(user.lastLoginAt)}</TableCell>
                      <TableCell>
                        <div className="flex items-center gap-1">
                          <Button
                            variant="outline"
                            size="icon-sm"
                            aria-label="Edit"
                            onClick={() => openEdit(user)}
                          >
                            <Pencil className="h-4 w-4" />
                          </Button>
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => setResetUserId(user.id)}
                          >
                            Reset PW
                          </Button>
                          <Button
                            variant="destructive"
                            size="sm"
                            disabled={isSelf || user.isDisabled}
                            onClick={() =>
                              setDeactivateUser({
                                id: user.id,
                                email: user.email,
                              })
                            }
                          >
                            Disable
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>

          <Pagination
            currentPage={currentPage}
            totalItems={serverTotal}
            pageSize={pageSize}
            totalPages={totalPages(serverTotal)}
            onPageChange={(page) => setPage(page, serverTotal)}
            onPageSizeChange={setPageSize}
          />
        </>
      )}

      {/* Create User Dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New user</DialogTitle>
            <DialogDescription>
              Create a new user account. They will be required to change their
              password on first login.
            </DialogDescription>
          </DialogHeader>
          <Form {...createForm}>
            <form
              onSubmit={createForm.handleSubmit(onCreateSubmit)}
              className="space-y-4"
            >
              <div className="grid grid-cols-2 gap-4">
                <FormField
                  control={createForm.control}
                  name="firstName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>First Name</FormLabel>
                      <FormControl>
                        <Input placeholder="John" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={createForm.control}
                  name="lastName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>Last Name</FormLabel>
                      <FormControl>
                        <Input placeholder="Doe" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
              <FormField
                control={createForm.control}
                name="email"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Email</FormLabel>
                    <FormControl>
                      <Input
                        type="email"
                        placeholder="name@example.com"
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={createForm.control}
                name="password"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Password</FormLabel>
                    <FormControl>
                      <PasswordInput
                        placeholder="At least 8 characters"
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={createForm.control}
                name="role"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Role</FormLabel>
                    <FormControl>
                      <Combobox
                        options={ROLE_OPTIONS}
                        value={field.value}
                        onValueChange={field.onChange}
                        placeholder="Select a role..."
                        searchPlaceholder="Search roles..."
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <DialogFooter>
                <Button
                  type="submit"
                  disabled={createForm.formState.isSubmitting}
                >
                  {createForm.formState.isSubmitting && <Spinner size="sm" />}
                  {createForm.formState.isSubmitting
                    ? "Creating…"
                    : "New user"}
                </Button>
              </DialogFooter>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      {/* Edit User Dialog */}
      <Dialog open={!!editUser} onOpenChange={() => setEditUser(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Edit User</DialogTitle>
            <DialogDescription>
              Update user details, role, and account status.
            </DialogDescription>
          </DialogHeader>
          <Form {...editForm}>
            <form
              onSubmit={editForm.handleSubmit(onEditSubmit)}
              className="space-y-4"
            >
              <div className="grid grid-cols-2 gap-4">
                <FormField
                  control={editForm.control}
                  name="firstName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>First Name</FormLabel>
                      <FormControl>
                        <Input placeholder="John" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={editForm.control}
                  name="lastName"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>Last Name</FormLabel>
                      <FormControl>
                        <Input placeholder="Doe" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
              <FormField
                control={editForm.control}
                name="email"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Email</FormLabel>
                    <FormControl>
                      <Input type="email" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={editForm.control}
                name="role"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>Role</FormLabel>
                    <FormControl>
                      <Combobox
                        options={ROLE_OPTIONS}
                        value={field.value}
                        onValueChange={field.onChange}
                        placeholder="Select a role..."
                        searchPlaceholder="Search roles..."
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={editForm.control}
                name="isDisabled"
                render={({ field }) => (
                  <FormItem>
                    <div className="flex items-center gap-3">
                      <FormControl>
                        <Checkbox
                          checked={field.value}
                          onCheckedChange={field.onChange}
                        />
                      </FormControl>
                      <FormLabel>Account Disabled</FormLabel>
                    </div>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <DialogFooter>
                <Button
                  type="submit"
                  disabled={editForm.formState.isSubmitting}
                >
                  {editForm.formState.isSubmitting && <Spinner size="sm" />}
                  {editForm.formState.isSubmitting
                    ? "Saving..."
                    : "Save Changes"}
                </Button>
              </DialogFooter>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      {/* Reset Password Dialog */}
      <Dialog
        open={!!resetUserId}
        onOpenChange={() => {
          setResetUserId(null);
          resetForm.reset();
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Reset Password</DialogTitle>
            <DialogDescription>
              Set a new password for this user. They will be required to change
              it on their next login.
            </DialogDescription>
          </DialogHeader>
          <Form {...resetForm}>
            <form
              onSubmit={resetForm.handleSubmit(onResetSubmit)}
              className="space-y-4"
            >
              <FormField
                control={resetForm.control}
                name="newPassword"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel required>New Password</FormLabel>
                    <FormControl>
                      <PasswordInput
                        placeholder="At least 8 characters"
                        {...field}
                      />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <DialogFooter>
                <Button
                  type="submit"
                  disabled={resetForm.formState.isSubmitting}
                >
                  {resetForm.formState.isSubmitting && <Spinner size="sm" />}
                  {resetForm.formState.isSubmitting
                    ? "Resetting..."
                    : "Reset Password"}
                </Button>
              </DialogFooter>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      {/* Deactivate Confirmation */}
      <AlertDialog
        open={!!deactivateUser}
        onOpenChange={() => setDeactivateUser(null)}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Deactivate User</AlertDialogTitle>
            <AlertDialogDescription>
              Are you sure you want to deactivate{" "}
              <span className="font-semibold">{deactivateUser?.email}</span>?
              They will no longer be able to log in.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              onClick={async () => {
                if (deactivateUser) {
                  await deleteUser.mutateAsync(deactivateUser.id);
                  setDeactivateUser(null);
                }
              }}
            >
              {deleteUser.isPending && <Spinner size="sm" />}
              Deactivate
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
    </>
  );
}

export default AdminUsers;
