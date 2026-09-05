import { useCallback, useMemo, useState } from "react";
import {
  useMutation,
  useQueryClient,
  type DefaultError,
  type MutateOptions,
  type QueryClient,
  type UseMutateAsyncFunction,
  type UseMutateFunction,
  type UseMutationOptions,
  type UseMutationResult,
} from "@tanstack/react-query";
import {
  assertSessionCurrent,
  getSessionSignal,
  getSessionVersion,
} from "@/lib/auth";

async function inSession<T>(
  version: number,
  action: () => T | Promise<T>,
): Promise<T> {
  const signal = getSessionSignal();
  assertSessionCurrent(version);
  let onAbort: (() => void) | undefined;
  try {
    return await new Promise<T>((resolve, reject) => {
      onAbort = () => reject(signal.reason);
      signal.addEventListener("abort", onAbort, { once: true });
      // Attach both handlers even if the caller aborts first: late failures remain handled.
      Promise.resolve(action()).then(resolve, reject);
    });
  } finally {
    if (onAbort) signal.removeEventListener("abort", onAbort);
    // An obsolete failure must reject as cancellation too, rather than reaching an old caller's UI.
    assertSessionCurrent(version);
  }
}

function sessionCallbacks<TData, TError, TVariables, TContext>(
  version: number,
  callbacks?: MutateOptions<TData, TError, TVariables, TContext>,
): MutateOptions<TData, TError, TVariables, TContext> | undefined {
  if (!callbacks) return undefined;
  return {
    onSuccess: (...args) => {
      if (getSessionVersion() === version) callbacks.onSuccess?.(...args);
    },
    onError: (...args) => {
      if (getSessionVersion() === version) callbacks.onError?.(...args);
    },
    onSettled: (...args) => {
      if (getSessionVersion() === version) callbacks.onSettled?.(...args);
    },
  };
}

/**
 * A mutation belongs to the session in which its hook mounted. AuthProvider remounts its
 * QueryClient subtree at a session boundary; pending optimistic work stays in the old cache.
 * Guard asynchronous stages as well as completion callbacks so that work cannot continue
 * with replacement credentials or deliver private results to the next session.
 */
export function useSessionMutation<
  TData = unknown,
  TError = DefaultError,
  TVariables = void,
  TContext = unknown,
>(
  options: UseMutationOptions<TData, TError, TVariables, TContext>,
  queryClient?: QueryClient,
): UseMutationResult<TData, TError, TVariables, TContext> {
  const client = useQueryClient(queryClient);
  const [version] = useState(getSessionVersion);
  // Resolve global/key defaults first, so an inherited mutationFn or callback gets the same guards.
  const defaults = client.defaultMutationOptions(options);
  const mutation = useMutation<TData, TError, TVariables, TContext>(
    {
      ...defaults,
      mutationFn: defaults.mutationFn
        ? (...args) => inSession(version, () => defaults.mutationFn!(...args))
        : undefined,
      onMutate: defaults.onMutate
        ? (...args) => inSession(version, () => defaults.onMutate!(...args))
        : undefined,
      onSuccess: (...args) =>
        inSession(version, () => defaults.onSuccess?.(...args)),
      onError: (...args) => {
        if (getSessionVersion() === version) return defaults.onError?.(...args);
      },
      onSettled: (...args) => {
        if (getSessionVersion() === version)
          return defaults.onSettled?.(...args);
      },
      retry: (failureCount, error) => {
        if (getSessionVersion() !== version) return false;
        return typeof defaults.retry === "function"
          ? defaults.retry(failureCount, error)
          : defaults.retry === true || failureCount < (defaults.retry || 0);
      },
      throwOnError: (error) =>
        getSessionVersion() === version &&
        (typeof defaults.throwOnError === "function"
          ? defaults.throwOnError(error)
          : !!defaults.throwOnError),
    },
    client,
  );

  const execute = mutation.mutateAsync;
  const mutateAsync = useCallback<
    UseMutateAsyncFunction<TData, TError, TVariables, TContext>
  >(
    (variables, callbacks) =>
      inSession(version, () =>
        execute(variables, sessionCallbacks(version, callbacks)),
      ),
    [execute, version],
  );
  const mutate = useCallback<
    UseMutateFunction<TData, TError, TVariables, TContext>
  >(
    (variables, callbacks) => {
      void mutateAsync(variables, callbacks).catch(() => {});
    },
    [mutateAsync],
  );

  // TanStack's top-level result is fresh each render. Preserve its complete result API while
  // memoizing individual fields, just as useStableQuery does for query consumers.
  const {
    context,
    data,
    error,
    failureCount,
    failureReason,
    isError,
    isIdle,
    isPaused,
    isPending,
    isSuccess,
    reset,
    status,
    submittedAt,
    variables,
  } = mutation;
  return useMemo(
    () =>
      ({
        context,
        data,
        error,
        failureCount,
        failureReason,
        isError,
        isIdle,
        isPaused,
        isPending,
        isSuccess,
        mutate,
        mutateAsync,
        reset,
        status,
        submittedAt,
        variables,
      }) as UseMutationResult<TData, TError, TVariables, TContext>,
    [
      context,
      data,
      error,
      failureCount,
      failureReason,
      isError,
      isIdle,
      isPaused,
      isPending,
      isSuccess,
      mutate,
      mutateAsync,
      reset,
      status,
      submittedAt,
      variables,
    ],
  );
}
