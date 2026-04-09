import { v4 as uuidv4 } from "uuid";
import { ApiError } from "./errors";

/**
 * Fetch wrapper universel.
 *
 * Deux contextes :
 *   - **Serveur (RSC / route handlers)** : utilise l'API key interne
 *     (`process.env.API_INTERNAL_KEY`) côté `x-api-key`.
 *   - **Client navigateur** : passe le JWT via les cookies httpOnly qui sont
 *     posés par le BFF `/api/auth/*`. On `credentials: "include"` pour que les
 *     cookies soient envoyés cross-origin.
 *
 * Erreurs :
 *   - 401 → throw ApiError (le client décide de rediriger vers /login).
 *   - 429 → throw ApiError (le composant affiche un toast "Rate limited").
 *   - 4xx/5xx → ApiError avec ProblemDetails parsé.
 */

const API_BASE = process.env.API_BASE_URL ?? "https://localhost:5001";

export interface FetchOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE" | "PATCH";
  query?: Record<string, string | number | boolean | undefined | null>;
  body?: unknown;
  headers?: Record<string, string>;
  /** Signal d'annulation externe (ex: debounce search). */
  signal?: AbortSignal;
  /** Durée max, défaut 15 s. */
  timeoutMs?: number;
  /**
   * Si true, ajoute `x-api-key: API_INTERNAL_KEY` (contexte serveur).
   * Si false, le navigateur enverra les cookies httpOnly seul.
   */
  serverSide?: boolean;
  /** JWT explicite à passer (contexte Bearer). */
  bearerToken?: string;
}

function buildUrl(path: string, query?: FetchOptions["query"]): string {
  const url = new URL(
    path.startsWith("http") ? path : `${API_BASE}${path}`,
  );
  if (query) {
    for (const [k, v] of Object.entries(query)) {
      if (v === undefined || v === null || v === "") continue;
      url.searchParams.set(k, String(v));
    }
  }
  return url.toString();
}

export async function apiFetch<T>(
  path: string,
  opts: FetchOptions = {},
): Promise<T> {
  const {
    method = "GET",
    query,
    body,
    headers = {},
    signal,
    timeoutMs = 15_000,
    serverSide = false,
    bearerToken,
  } = opts;

  const url = buildUrl(path, query);
  const correlationId = uuidv4();

  const finalHeaders: Record<string, string> = {
    Accept: "application/json",
    "X-Correlation-Id": correlationId,
    ...headers,
  };

  if (body !== undefined) {
    finalHeaders["Content-Type"] = "application/json";
  }

  if (serverSide) {
    const key = process.env.API_INTERNAL_KEY;
    if (key) finalHeaders["x-api-key"] = key;
  }

  if (bearerToken) {
    finalHeaders["Authorization"] = `Bearer ${bearerToken}`;
  }

  // Timeout via AbortController. Si un signal externe est fourni, on les
  // compose manuellement.
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  if (signal) {
    signal.addEventListener("abort", () => controller.abort(), { once: true });
  }

  let res: Response;
  try {
    res = await fetch(url, {
      method,
      headers: finalHeaders,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal: controller.signal,
      credentials: serverSide ? "omit" : "include",
      cache: "no-store",
    });
  } finally {
    clearTimeout(timeout);
  }

  if (!res.ok) {
    throw await ApiError.fromResponse(res);
  }

  // 204 No Content
  if (res.status === 204) return undefined as T;

  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

// Convenience wrappers
export const apiGet = <T>(
  path: string,
  query?: FetchOptions["query"],
  opts?: Omit<FetchOptions, "query" | "method">,
) => apiFetch<T>(path, { ...opts, method: "GET", query });

export const apiPost = <T>(
  path: string,
  body?: unknown,
  opts?: Omit<FetchOptions, "body" | "method">,
) => apiFetch<T>(path, { ...opts, method: "POST", body });

export const apiPut = <T>(
  path: string,
  body?: unknown,
  opts?: Omit<FetchOptions, "body" | "method">,
) => apiFetch<T>(path, { ...opts, method: "PUT", body });

export const apiDelete = <T>(
  path: string,
  opts?: Omit<FetchOptions, "method">,
) => apiFetch<T>(path, { ...opts, method: "DELETE" });
