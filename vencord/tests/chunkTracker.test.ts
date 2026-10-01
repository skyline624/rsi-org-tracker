import { describe, expect, it } from "vitest";

import { type ChunkLike, createChunkTracker } from "../scTracker.desktop/lib/chunkTracker";

const GUILD = "900000000000000001";
const OTHER_GUILD = "900000000000000002";
const [U1, U2, U3, U4] = ["100000000000000001", "100000000000000002", "100000000000000003", "100000000000000004"];

const chunk = (guildId: string, present: string[], notFound?: string[]): ChunkLike =>
  ({ guildId, members: present.map(id => ({ user: { id } })), notFound });

describe("createChunkTracker", () => {
  it("does not resolve overlapping ids from an older request nonce", () => {
    const tracker = createChunkTracker(GUILD, [U1, U2], "this-batch");
    tracker.accept({ ...chunk(GUILD, [U1], [U2]), nonce: "earlier-batch" });
    tracker.accept(chunk(GUILD, [U1], [U2]));
    expect(tracker.unresolved()).toEqual([U1, U2]);

    tracker.accept({ ...chunk(GUILD, [U1], [U2]), nonce: "this-batch" });
    expect(tracker.done).toBe(true);
    expect([...tracker.present]).toEqual([U1]);
    expect([...tracker.notFound]).toEqual([U2]);
  });

  it("ignores malformed notFound values instead of coercing them to requested ids", () => {
    const tracker = createChunkTracker(GUILD, [U1]);
    const object = { toString: () => U1 };
    tracker.accept({ ...chunk(GUILD, []), notFound: [object] } as unknown as ChunkLike);
    expect(tracker.done).toBe(false);
  });

  it("resolves requested ids as present or not found, and is done when all are resolved", () => {
    const tracker = createChunkTracker(GUILD, [U1, U2, U3]);

    tracker.accept(chunk(GUILD, [U1], [U3]));
    expect(tracker.done).toBe(false);
    expect(tracker.unresolved()).toEqual([U2]);

    tracker.accept(chunk(GUILD, [U2]));
    expect(tracker.done).toBe(true);
    expect([...tracker.present]).toEqual([U1, U2]);
    expect([...tracker.notFound]).toEqual([U3]);
    expect(tracker.unresolved()).toEqual([]);
  });

  it("ignores chunks of another guild", () => {
    const tracker = createChunkTracker(GUILD, [U1, U2]);

    tracker.accept(chunk(OTHER_GUILD, [U1], [U2]));

    expect(tracker.present.size).toBe(0);
    expect(tracker.notFound.size).toBe(0);
    expect(tracker.done).toBe(false);
    expect(tracker.unresolved()).toEqual([U1, U2]);
  });

  it("ignores ids that were not requested, such as the answer to Discord's own request", () => {
    const tracker = createChunkTracker(GUILD, [U1]);

    tracker.accept(chunk(GUILD, [U4], [U3]));

    expect(tracker.present.has(U4)).toBe(false);
    expect(tracker.notFound.has(U3)).toBe(false);
    expect(tracker.unresolved()).toEqual([U1]);
  });

  it("keeps the first resolution of an id", () => {
    const tracker = createChunkTracker(GUILD, [U1, U2]);

    tracker.accept(chunk(GUILD, [U1], [U2]));
    tracker.accept(chunk(GUILD, [U2], [U1]));

    expect([...tracker.present]).toEqual([U1]);
    expect([...tracker.notFound]).toEqual([U2]);
  });

  it("lists unresolved ids once each, in request order, for a batch that timed out", () => {
    const tracker = createChunkTracker(GUILD, [U3, U1, U3, U2]);

    tracker.accept(chunk(GUILD, [U1]));

    expect(tracker.unresolved()).toEqual([U3, U2]);
  });

  it("is done at once for an empty batch", () => {
    expect(createChunkTracker(GUILD, []).done).toBe(true);
  });

  it("survives malformed chunks", () => {
    const tracker = createChunkTracker(GUILD, [U1]);

    expect(() => {
      tracker.accept(undefined as unknown as ChunkLike);
      tracker.accept({ guildId: GUILD } as unknown as ChunkLike);
      tracker.accept({ guildId: GUILD, members: [null, { user: null }, {}] } as unknown as ChunkLike);
    }).not.toThrow();
    expect(tracker.unresolved()).toEqual([U1]);
  });
});
