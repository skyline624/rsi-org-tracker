import type { ClientRequest } from "node:http";
import { request } from "node:https";
import type { TLSSocket } from "node:tls";

import { normalizeFingerprint } from "./fingerprint";

/**
 * What native.ts hands back to the renderer. `status` is 0 when no HTTP answer arrived, and
 * `error` then says why:
 * - `pin_mismatch`: the tracker presented another certificate (or the pin is not a fingerprint),
 *   and nothing was sent;
 * - `network`: no connection, or it failed before the body was fully written;
 * - `no_response_after_upload`: the body was fully written, then the connection failed or stayed
 *   idle, so the tracker may have recorded the sync.
 */
export type PostResult = {
  status: number;
  body: string;
  retryAfter: number | null;
  error?: "pin_mismatch" | "network" | "no_response_after_upload";
};

/** Idle (not total) socket timeout: a 25 MiB upload on a slow line may take longer than this. */
export const DEFAULT_IDLE_TIMEOUT_MS = 30_000;
/** Covers DNS, TCP and TLS, before any request data can be sent. */
export const DEFAULT_CONNECT_TIMEOUT_MS = 15_000;
/** Allows a slow 25 MiB upload while still bounding an endless trickle response. */
export const DEFAULT_TOTAL_TIMEOUT_MS = 10 * 60 * 1000;
/** The ingest reply is a small summary or a ProblemDetails object. */
export const DEFAULT_MAX_RESPONSE_BYTES = 256 * 1024;

const PIN_MISMATCH = "PIN_MISMATCH";

/** An IMF-fixdate, the only HTTP-date form servers send today. */
const HTTP_DATE = /^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$/;

/**
 * Reads a Retry-After header as whole seconds: the delay form ("120"), or an HTTP-date turned
 * into a delay from `now` (0 when already past). Anything else gives null.
 */
export function parseRetryAfter(value: string | string[] | undefined, now: number = Date.now()): number | null {
  const raw = (Array.isArray(value) ? value[0] : value)?.trim();
  if (!raw) return null;
  if (/^\d+$/.test(raw)) {
    const seconds = Number(raw);
    return Number.isSafeInteger(seconds) ? seconds : null;
  }
  if (HTTP_DATE.test(raw)) {
    const at = Date.parse(raw);
    if (!Number.isNaN(at)) return Math.max(0, Math.ceil((at - now) / 1000));
  }
  return null;
}

/**
 * POSTs `body` to `url` + `path` over HTTPS, trusting the server only if its certificate's
 * SHA-256 fingerprint equals `fingerprint`. The tracker uses a self-signed certificate, so the
 * usual chain check is disabled for this request only (never through a global TLS setting in
 * Discord's main process) and replaced by the pin, checked at `secureConnect`: at that point
 * nothing has been written on the socket, so on a mismatch neither the x-api-key header nor the
 * body leaves the machine. Never rejects.
 */
export function pinnedPost(opts: {
  url: string;
  path: string;
  fingerprint: string;
  headers: Record<string, string>;
  body: string;
  idleTimeoutMs?: number;
  connectTimeoutMs?: number;
  totalTimeoutMs?: number;
  maxResponseBytes?: number;
}): Promise<PostResult> {
  return new Promise<PostResult>(resolve => {
    let settled = false;
    let uploaded = false;
    let responseStarted = false;
    let connectTimer: ReturnType<typeof setTimeout> | undefined;
    let totalTimer: ReturnType<typeof setTimeout> | undefined;
    let req: ClientRequest | undefined;
    const settle = (result: PostResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(connectTimer);
      clearTimeout(totalTimer);
      resolve(result);
      // An early 413/429 can arrive before a slow upload ends. Do not keep sending after settlement.
      req?.destroy();
    };
    const fail = (error: NonNullable<PostResult["error"]>) => settle({ status: 0, body: "", retryAfter: null, error });
    const failAfterConnect = () => fail(uploaded ? "no_response_after_upload" : "network");

    const expected = normalizeFingerprint(opts.fingerprint);
    if (expected === null) {
      fail("pin_mismatch");
      return;
    }

    const idleTimeout = opts.idleTimeoutMs ?? DEFAULT_IDLE_TIMEOUT_MS;
    const connectTimeout = opts.connectTimeoutMs ?? DEFAULT_CONNECT_TIMEOUT_MS;
    const totalTimeout = opts.totalTimeoutMs ?? DEFAULT_TOTAL_TIMEOUT_MS;
    const maxResponseBytes = opts.maxResponseBytes ?? DEFAULT_MAX_RESPONSE_BYTES;
    if ([idleTimeout, connectTimeout, totalTimeout, maxResponseBytes]
      .some(value => !Number.isSafeInteger(value) || value <= 0 || value > 2_147_483_647)) {
      fail("network");
      return;
    }

    let target: URL;
    try {
      target = new URL(opts.url);
      if (target.protocol !== "https:") {
        fail("network");
        return;
      }
      req = request({
        method: "POST",
        hostname: target.hostname.replace(/^\[(.*)\]$/, "$1"),
        port: target.port === "" ? 443 : Number(target.port),
        path: opts.path,
        headers: { ...opts.headers, "content-length": String(Buffer.byteLength(opts.body)) },
        // A fresh socket that no other request shares, and no chain check for this request
        // only: the pin below replaces it.
        agent: false,
        rejectUnauthorized: false,
      }, res => {
        responseStarted = true;
        const chunks: Buffer[] = [];
        let responseBytes = 0;
        res.on("data", (chunk: Buffer) => {
          responseBytes += chunk.length;
          if (responseBytes > maxResponseBytes) {
            failAfterConnect();
            res.destroy();
            req?.destroy();
            return;
          }
          chunks.push(chunk);
        });
        res.on("end", () => settle({
          status: res.statusCode ?? 0,
          body: Buffer.concat(chunks).toString("utf8"),
          retryAfter: parseRetryAfter(res.headers["retry-after"]),
        }));
        res.on("error", failAfterConnect);
        res.on("close", () => {
          if (!res.complete) failAfterConnect();
        });
      });
    } catch {
      fail("network");
      return;
    }

    req.on("socket", socket => {
      socket.once("secureConnect", () => {
        if (settled) return;
        clearTimeout(connectTimer);
        const presented = normalizeFingerprint((socket as TLSSocket).getPeerCertificate().fingerprint256);
        if (presented !== expected) {
          req.destroy(Object.assign(new Error("Certificate fingerprint mismatch"), { code: PIN_MISMATCH }));
          return;
        }
        req.end(opts.body);
      });
    });
    // "finish": the whole body has been handed to the socket.
    req.on("finish", () => {
      uploaded = true;
    });
    req.on("error", (err: NodeJS.ErrnoException) => {
      if (err.code === PIN_MISMATCH) fail("pin_mismatch");
      else failAfterConnect();
    });
    req.on("close", () => {
      if (!responseStarted) failAfterConnect();
    });
    connectTimer = setTimeout(() => {
      fail("network");
      req.destroy();
    }, connectTimeout);
    totalTimer = setTimeout(() => {
      failAfterConnect();
      req.destroy();
    }, totalTimeout);
    req.setTimeout(idleTimeout, () => {
      req.destroy(Object.assign(new Error("Idle timeout"), { code: "IDLE_TIMEOUT" }));
    });
  });
}
