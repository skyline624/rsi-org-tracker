import { describe, expect, it, vi } from "vitest";

import { bindCredentials, credentialsForSettings } from "../scTracker.desktop/lib/credentials";
import { createSyncRunner } from "../scTracker.desktop/lib/syncRunner";

const saved = { url: "https://tracker.example", fingerprint: "AB".repeat(32), apiKey: "local-key" };
describe("locally bound tracker credentials", () => {
  it("normalizes origin and certificate without trusting a settings copy of the secret", () => {
    const bound = bindCredentials({ ...saved, url: "https://TRACKER.example/", fingerprint: "ab:".repeat(31) + "ab" });
    expect(bound).toEqual(saved);
    expect(credentialsForSettings(bound, saved.url, saved.fingerprint)).toEqual(saved);
  });
  it.each(["legacy-key", null, { ...saved, fingerprint: "bad" }])("requires an explicit save for legacy or invalid local data", value => {
    expect(() => credentialsForSettings(value, saved.url, saved.fingerprint)).toThrow("Enregistre");
  });
  it.each([
    { url: "https://another.example", fingerprint: saved.fingerprint },
    { url: saved.url, fingerprint: "CD".repeat(32) },
  ])("never collects or posts after Cloud Sync redirects the URL or certificate", async settings => {
    const collect = vi.fn();
    const post = vi.fn();
    const runner = createSyncRunner({
      config: async () => credentialsForSettings(saved, settings.url, settings.fingerprint),
      isTracked: () => true, collect, post, status: vi.fn(), state: vi.fn(), notify: vi.fn(),
    });
    await runner.run(["100000000000000001"]);
    expect(collect).not.toHaveBeenCalled();
    expect(post).not.toHaveBeenCalled();
  });
});
