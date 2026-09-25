import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/headers", () => ({ headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(async () => ({ userId: 1, username: "pilot", isAdmin: false, accessToken: "jwt" })),
}));
vi.mock("@/lib/api/client", () => ({ apiPost: vi.fn(), apiPut: vi.fn(), apiDelete: vi.fn() }));

const { apiDelete, apiPost } = await import("@/lib/api/client");
const { createNoteAction, deleteNoteAction } = await import("./actions");

describe("note actions validate their arguments before calling the API", () => {
  beforeEach(() => {
    vi.mocked(apiDelete).mockReset();
    vi.mocked(apiPost).mockReset();
  });

  it("rejects a path-like id instead of forwarding it", async () => {
    const result = await deleteNoteAction("../admin/users/3" as unknown as number);

    expect(result.ok).toBe(false);
    expect(apiDelete).not.toHaveBeenCalled();
  });

  it("rejects a handle that is not an RSI handle", async () => {
    const result = await createNoteAction("../../admin", "hello");

    expect(result.ok).toBe(false);
    expect(apiPost).not.toHaveBeenCalled();
  });

  it("still deletes a real note", async () => {
    vi.mocked(apiDelete).mockResolvedValue(undefined);

    expect((await deleteNoteAction(12)).ok).toBe(true);
    expect(apiDelete).toHaveBeenCalledWith("/api/notes/12", expect.anything());
  });
});
