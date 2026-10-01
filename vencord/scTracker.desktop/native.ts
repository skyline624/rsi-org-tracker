import type { IpcMainInvokeEvent } from "electron";

import { pinnedPost, type PostResult } from "./lib/pinnedPost";
import { validatePostArgs } from "./lib/postArgs";

/** The only native entry point: fixes the ingest path and validates before opening a socket. */
export async function postSync(_event: IpcMainInvokeEvent, args: unknown): Promise<PostResult> {
  const result = validatePostArgs(args);
  if (!result.ok) return {
    status: 400, retryAfter: null,
    body: JSON.stringify({ status: 400, title: "Arguments d'envoi invalides", code: "invalid_arguments", detail: result.error }),
  };
  const { url, fingerprint, apiKey, body } = result.value;
  return pinnedPost({ url, fingerprint, body, path: result.path, headers: { "x-api-key": apiKey, "content-type": "application/json" } });
}
