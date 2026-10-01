import { EventEmitter } from "node:events";
import { describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock("node:https", () => ({ request: mocks.request }));
import { pinnedPost } from "../scTracker.desktop/lib/pinnedPost";

describe("early native HTTP response", () => {
  it.each([false, true])("destroys the request even if HTTP buffers report writableFinished=%s", async writableFinished => {
    const request = Object.assign(new EventEmitter(), { writableFinished, end: vi.fn(), destroy: vi.fn(), setTimeout: vi.fn() });
    const response = Object.assign(new EventEmitter(), { statusCode: 429, headers: { "retry-after": "30" }, complete: true });
    let respond!: (reply: typeof response) => void;
    mocks.request.mockImplementation((_opts, callback) => { respond = callback; return request; });
    const pending = pinnedPost({ url: "https://tracker.example", path: "/ingest/discord/guilds/100000000000000001/syncs",
      fingerprint: "A".repeat(64), headers: { "x-api-key": "local-key" }, body: "pending body" });
    const socket = Object.assign(new EventEmitter(), { getPeerCertificate: () => ({ fingerprint256: "A".repeat(64) }) });
    request.emit("socket", socket);
    socket.emit("secureConnect");
    expect(request.end).toHaveBeenCalledWith("pending body");
    respond(response);
    response.emit("data", Buffer.from("{}"));
    response.emit("end");
    expect(await pending).toEqual({ status: 429, body: "{}", retryAfter: 30 });
    expect(request.destroy).toHaveBeenCalledOnce();
  });
});
