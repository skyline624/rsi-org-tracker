import { X509Certificate } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer } from "node:https";
import type { AddressInfo } from "node:net";
import { describe, expect, it } from "vitest";

import type { PostResult } from "../scTracker.desktop/lib/pinnedPost";

// Electron is a type-only import in native.ts. Full bridge types are also checked
// inside the pinned Vencord checkout, independently of this pure test package.
const nativePath = "../scTracker.desktop/native.ts";
const { postSync } = await import(nativePath) as { postSync(event: unknown, args: unknown): Promise<PostResult> };
const cert = readFileSync(new URL("./fixtures/tracker-test.crt", import.meta.url));
const key = readFileSync(new URL("./fixtures/tracker-test.key", import.meta.url));
const guildId = "100000000000000001";

describe("desktop IPC boundary", () => {
  it.each([null, [], {}, { url: "http://127.0.0.1", fingerprint: "A".repeat(64), apiKey: "test", guildId, body: "{}" }])("refuses invalid arguments locally with HTTP 400: %s", async args => {
    const reply = await postSync({}, args);
    expect(reply.status).toBe(400);
    expect(JSON.parse(reply.body).code).toBe("invalid_arguments");
    expect(reply.error).toBeUndefined();
  });
  it("constructs the sole ingestion path and pinned HTTPS request", async () => {
    const received: { path?: string; key?: string; body: string }[] = [];
    const server = createServer({ cert, key }, (req, res) => {
      const chunks: Buffer[] = [];
      req.on("data", chunk => chunks.push(chunk));
      req.on("end", () => {
        received.push({ path: req.url, key: req.headers["x-api-key"] as string, body: Buffer.concat(chunks).toString("utf8") });
        res.end('{"isComplete":false}');
      });
    });
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    try {
      const args = { url: `https://127.0.0.1:${(server.address() as AddressInfo).port}`, fingerprint: new X509Certificate(cert).fingerprint256, apiKey: "native-test-key", guildId, body: '{"members":[]}' };
      const good = await postSync({}, args);
      expect(good.status).toBe(200);
      expect(received).toEqual([{ path: `/ingest/discord/guilds/${guildId}/syncs`, key: "native-test-key", body: args.body }]);
      const bad = await postSync({}, { ...args, fingerprint: "0".repeat(64) });
      expect(bad.error).toBe("pin_mismatch");
      expect(received).toHaveLength(1);
    } finally {
      server.closeAllConnections();
      await new Promise<void>(resolve => server.close(() => resolve()));
    }
  });
});
