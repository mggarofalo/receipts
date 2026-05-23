import { useState } from "react";
import { Navigate } from "react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useAuth } from "@/hooks/useAuth";
import { usePageTitle } from "@/hooks/usePageTitle";
import { isTimeoutError } from "@/lib/api-client";
import { SubmitButton } from "@/components/ui/submit-button";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import { PasswordInput } from "@/components/ui/password-input";
import { Alert, AlertDescription } from "@/components/ui/alert";

const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, "Current password is required"),
    newPassword: z.string().min(8, "Password must be at least 8 characters"),
    confirmPassword: z.string().min(1, "Please confirm your new password"),
  })
  .refine((data) => data.newPassword !== data.currentPassword, {
    message: "New password must be different from current password",
    path: ["newPassword"],
  })
  .refine((data) => data.newPassword === data.confirmPassword, {
    message: "Passwords do not match",
    path: ["confirmPassword"],
  });

type ChangePasswordFormValues = z.infer<typeof changePasswordSchema>;

function ChangePassword() {
  usePageTitle("Change Password");
  const { user, mustResetPassword, changePassword } = useAuth();
  const [error, setError] = useState<string | null>(null);

  const form = useForm<ChangePasswordFormValues>({
    resolver: zodResolver(changePasswordSchema),
    defaultValues: { currentPassword: "", newPassword: "", confirmPassword: "" },
  });

  if (!user) {
    return <Navigate to="/login" replace />;
  }

  if (!mustResetPassword) {
    return <Navigate to="/" replace />;
  }

  async function onSubmit(values: ChangePasswordFormValues) {
    setError(null);
    try {
      await changePassword(values.currentPassword, values.newPassword);
    } catch (err: unknown) {
      if (isTimeoutError(err)) {
        setError("Request timed out. Please try again.");
      } else {
        const description =
          err != null &&
          typeof err === "object" &&
          "error_description" in err &&
          typeof (err as Record<string, unknown>).error_description === "string"
            ? (err as Record<string, string>).error_description
            : null;
        setError(
          description ??
            "Failed to change password. Please check your current password and try again.",
        );
      }
    }
  }

  return (
    <div className="auth-card">
      <h1 className="auth-title">Change password</h1>
      <p className="auth-sub">Required before continuing</p>
      {error && (
        <Alert variant="destructive" className="mb-4">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}
      <Form {...form}>
        <form onSubmit={form.handleSubmit(onSubmit)} className="auth-form">
          <FormField
            control={form.control}
            name="currentPassword"
            render={({ field }) => (
              <FormItem>
                <FormLabel required>Current password</FormLabel>
                <FormControl>
                  <PasswordInput
                    placeholder="Enter your current password"
                    autoComplete="current-password"
                    {...field}
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="newPassword"
            render={({ field }) => (
              <FormItem>
                <FormLabel required>New password</FormLabel>
                <FormControl>
                  <PasswordInput
                    placeholder="At least 8 characters"
                    autoComplete="new-password"
                    {...field}
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="confirmPassword"
            render={({ field }) => (
              <FormItem>
                <FormLabel required>Confirm new password</FormLabel>
                <FormControl>
                  <PasswordInput
                    placeholder="Re-enter your new password"
                    autoComplete="new-password"
                    {...field}
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <SubmitButton
            isSubmitting={form.formState.isSubmitting}
            label="Change password"
            loadingLabel="Changing password…"
            className="w-full"
          />
        </form>
      </Form>
    </div>
  );
}

export default ChangePassword;
