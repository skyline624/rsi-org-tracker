import { X509Certificate } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer as createHttpsServer, type Server as HttpsServer } from "node:https";
import { createServer as createNetServer, type AddressInfo, type Server as NetServer } from "node:net";
import { createServer as createTlsServer, type Server as TlsServer, type TLSSocket } from "node:tls";
import { afterEach, describe, expect, it, vi } from "vitest";

import { parseRetryAfter, pinnedPost } from "../scTracker.desktop/lib/pinnedPost";

const cert = readFileSync(new URL("./fixtures/tracker-test.crt", import.meta.url));
const key = readFileSync(new URL("./fixtures/tracker-test.key", import.meta.url));
/** "AB:CD:…", the form the site shows and users paste. */
const RIGHT = new X509Certificate(cert).fingerprint256;
/** The right fingerprint with its first hex digit changed. */
const WRONG = (RIGHT.startsWith("0") ? "1" : "0") + RIGHT.slice(1);

const API_KEY = "sct_test_key_0123456789";
const BODY = JSON.stringify({ pluginVersion: "1.0.0", members: [{ userId: "123456789012345678", nick: "Pilote éé" }] });
const PATH = "/ingest/discord/guilds/123456789012345678/syncs";

type Received = {
  method?: string; url?: string; apiKey?: string; contentType?: string; contentLength?: string; body: string;
};

const servers: (HttpsServer | TlsServer | NetServer)[] = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    if ("closeAllConnections" in server) server.closeAllConnections();
    await new Promise<void>(resolve => server.close(() => resolve()));
  }
});

function listen(server: HttpsServer | TlsServer | NetServer): Promise<string> {
  servers.push(server);
  return new Promise(resolve => {
    server.listen(0, "127.0.0.1", () => resolve(`https://127.0.0.1:${(server.address() as AddressInfo).port}`));
  });
}

/** A tracker that records each request, then answers with the given status, headers and body. */
async function tracker(status: number, answer: string, headers: Record<string, string> = {}) {
  const received: Received[] = [];
  const server = createHttpsServer({ cert, key }, (req, res) => {
    const chunks: Buffer[] = [];
    req.on("data", (chunk: Buffer) => chunks.push(chunk));
    req.on("end", () => {
      received.push({
        method: req.method,
        url: req.url,
        apiKey: req.headers["x-api-key"] as string | undefined,
        contentType: req.headers["content-type"],
        contentLength: req.headers["content-length"],
        body: Buffer.concat(chunks).toString("utf8"),
      });
      res.writeHead(status, { "content-type": "application/json", ...headers });
      res.end(answer);
    });
  });
  return { url: await listen(server), received };
}

function post(url: string, fingerprint: string, idleTimeoutMs?: number) {
  return pinnedPost({
    url,
    path: PATH,
    fingerprint,
    body: BODY,
    idleTimeoutMs,
    headers: { "x-api-key": API_KEY, "content-type": "application/json" },
  });
}

describe("pinnedPost", () => {
  it("bounds a TCP connection that never finishes its TLS handshake", async () => {
    let bytes = 0;
    const server = createNetServer(socket => {
      socket.on("data", (chunk: Buffer) => { bytes += chunk.length; });
    });
    const url = await listen(server);
    const result = await pinnedPost({
      url, path: PATH, fingerprint: RIGHT, body: BODY, headers: { "x-api-key": API_KEY },
      connectTimeoutMs: 100, totalTimeoutMs: 2000,
    });
    expect(result.error).toBe("network");
    // The bytes received are TLS handshake data, not an HTTP header or upload.
    expect(bytes).toBeGreaterThan(0);
  });

  it("bounds a response that keeps the socket active without ending", async () => {
    const server = createHttpsServer({ cert, key }, (req, res) => {
      req.resume();
      req.on("end", () => {
        res.writeHead(200);
        const timer = setInterval(() => res.write("x"), 15);
        res.on("close", () => clearInterval(timer));
      });
    });
    const url = await listen(server);
    const result = await pinnedPost({
      url, path: PATH, fingerprint: RIGHT, body: BODY, headers: {}, idleTimeoutMs: 300,
      totalTimeoutMs: 150,
    });
    expect(result.error).toBe("no_response_after_upload");
  });

  it("stops buffering an oversized response, even with a declared success status", async () => {
    const t = await tracker(200, "x".repeat(1025));
    const result = await pinnedPost({
      url: t.url, path: PATH, fingerprint: RIGHT, body: BODY, headers: {}, maxResponseBytes: 1024,
    });
    expect(result).toEqual({ status: 0, body: "", retryAfter: null, error: "no_response_after_upload" });
  });

  it("accepts a response exactly at its byte limit", async () => {
    const t = await tracker(200, "é".repeat(512));
    const result = await pinnedPost({
      url: t.url, path: PATH, fingerprint: RIGHT, body: BODY, headers: {}, maxResponseBytes: 1024,
    });
    expect(result.status).toBe(200);
    expect(result.body).toBe("é".repeat(512));
  });

  it("sends the key and the body once the certificate matches, and returns the answer", async () => {
    const t = await tracker(200, '{"syncId":42}');

    const result = await post(t.url, RIGHT);

    expect(result).toEqual({ status: 200, body: '{"syncId":42}', retryAfter: null });
    expect(t.received).toEqual([{
      method: "POST",
      url: PATH,
      apiKey: API_KEY,
      contentType: "application/json",
      contentLength: String(Buffer.byteLength(BODY)),
      body: BODY,
    }]);
  });

  it("accepts the fingerprint as openssl prints it", async () => {
    const t = await tracker(200, "{}");

    const result = await post(t.url, `sha256 Fingerprint=${RIGHT.toLowerCase()}`);

    expect(result.status).toBe(200);
    expect(result.error).toBeUndefined();
  });

  it("returns an error status with its body and Retry-After", async () => {
    const t = await tracker(429, '{"title":"Too Many Requests"}', { "retry-after": "120" });

    const result = await post(t.url, RIGHT);

    expect(result).toEqual({ status: 429, body: '{"title":"Too Many Requests"}', retryAfter: 120 });
  });

  it("never reaches the request handler when the certificate differs", async () => {
    const t = await tracker(200, "{}");

    const result = await post(t.url, WRONG);

    expect(result).toEqual({ status: 0, body: "", retryAfter: null, error: "pin_mismatch" });
    expect(t.received).toHaveLength(0);
  });
  it("closes the connection after an early 429 response is complete", async () => {
    let closed = false;
    let connection: TLSSocket | undefined;
    const body = "x".repeat(16 * 1024 * 1024);
    const server = createTlsServer({ cert, key }, socket => {
      connection = socket;
      socket.on("error", () => {});
      socket.once("close", () => { closed = true; });
      socket.once("data", () => {
        socket.pause();
        socket.write("HTTP/1.1 429 Too Many Requests\r\nContent-Length: 2\r\nRetry-After: 30\r\nConnection: keep-alive\r\n\r\n{}");
        const timer = setTimeout(() => socket.resume(), 50);
        socket.once("close", () => clearTimeout(timer));
      });
    });
    const url = await listen(server);
    try {
      const result = await pinnedPost({ url, path: PATH, fingerprint: RIGHT, headers: { "x-api-key": API_KEY }, body,
        idleTimeoutMs: 5000, totalTimeoutMs: 5000 });
      expect(result).toEqual({ status: 429, body: "{}", retryAfter: 30 });
      await vi.waitFor(() => expect(closed).toBe(true), { timeout: 1000 });
    } finally { connection?.destroy(); }
  });

  it("writes no byte after the handshake when the certificate differs", async () => {
    let applicationBytes = 0;
    const server = createTlsServer({ cert, key }, socket => {
      socket.on("data", (chunk: Buffer) => { applicationBytes += chunk.length; });
      socket.on("error", () => { /* the client drops the connection */ });
    });
    const url = await listen(server);

    const result = await post(url, WRONG);
    // close() calls back once the server has seen every connection end.
    servers.splice(servers.indexOf(server), 1);
    await new Promise<void>(resolve => server.close(() => resolve()));

    expect(result.error).toBe("pin_mismatch");
    expect(applicationBytes).toBe(0);
  });

  it("does write the key and the body to the same raw TLS server when the certificate matches", async () => {
    let text = "";
    const server = createTlsServer({ cert, key }, socket => {
      socket.on("data", (chunk: Buffer) => {
        text += chunk.toString("utf8");
        if (text.includes(BODY)) socket.end("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
      });
    });
    const url = await listen(server);

    const result = await post(url, RIGHT);

    expect(result.status).toBe(204);
    expect(text.toLowerCase()).toContain(`x-api-key: ${API_KEY}`.toLowerCase());
    expect(text).toContain(BODY);
  });

  it("reports no_response_after_upload when the tracker closes the connection after reading the body", async () => {
    let bodies = 0;
    const server = createHttpsServer({ cert, key }, req => {
      req.resume();
      req.on("end", () => {
        bodies++;
        req.socket.destroy();
      });
    });
    const url = await listen(server);

    const result = await post(url, RIGHT);

    expect(bodies).toBe(1);
    expect(result).toEqual({ status: 0, body: "", retryAfter: null, error: "no_response_after_upload" });
  });

  it("reports no_response_after_upload when the tracker stays silent after the body", async () => {
    const server = createHttpsServer({ cert, key }, req => {
      req.resume();
    });
    const url = await listen(server);

    const result = await post(url, RIGHT, 300);

    expect(result.error).toBe("no_response_after_upload");
  });

  it("reports network when nothing listens", async () => {
    const probe = createNetServer();
    await new Promise<void>(resolve => probe.listen(0, "127.0.0.1", () => resolve()));
    const port = (probe.address() as AddressInfo).port;
    await new Promise<void>(resolve => probe.close(() => resolve()));

    const result = await post(`https://127.0.0.1:${port}`, RIGHT);

    expect(result).toEqual({ status: 0, body: "", retryAfter: null, error: "network" });
  });

  it("does not connect with a configured fingerprint that is not one", async () => {
    const t = await tracker(200, "{}");

    const result = await post(t.url, "not a fingerprint");

    expect(result.error).toBe("pin_mismatch");
    expect(t.received).toHaveLength(0);
  });
});

describe("parseRetryAfter", () => {
  const now = Date.parse("2026-09-30T12:00:00Z");

  it.each([
    ["30", 30],
    [" 5 ", 5],
    ["0", 0],
    [["45", "60"], 45],
    ["Wed, 30 Sep 2026 12:01:30 GMT", 90],
    ["Wed, 30 Sep 2026 11:00:00 GMT", 0],
  ])("reads %j as %i seconds", (value, seconds) => {
    expect(parseRetryAfter(value, now)).toBe(seconds);
  });

  it.each([[undefined], [""], ["soon"], ["-3"], ["1.5"]])("returns null for %j", value => {
    expect(parseRetryAfter(value, now)).toBeNull();
  });
});
