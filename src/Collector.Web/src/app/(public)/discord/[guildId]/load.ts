import { notFound } from "next/navigation";
import { ApiError } from "@/lib/api/errors";
import { withAuthRedirect } from "@/lib/auth/server-api";

/**
 * An API read for the server page: a 401 sends to /login, a 404 (the server was
 * deleted meanwhile) to the not-found page; any other error reaches error.tsx.
 */
export async function loadOrNotFound<T>(promise: Promise<T>): Promise<T> {
  try {
    return await withAuthRedirect(promise);
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) notFound();
    throw err;
  }
}
