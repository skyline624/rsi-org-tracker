import { describe, expect, it } from "vitest";

import { noncePatches } from "../scTracker.desktop/lib/noncePatches";

describe("nonce propagation patches", () => {
  it.each([
    [0, "send(Op.REQUEST_GUILD_MEMBERS,{guildIds:a})", "nonce:arguments[1]?.nonce,"],
    [1, "GUILD_MEMBERS_REQUEST:function(r){return {presences:!!r.presences}}", "nonce:r.nonce"],
    [2, "dispatch({notFound:r.not_found})", "nonce:r.nonce"],
  ] as const)("keeps nonce on path %s and does not duplicate an upstream patch", (index, source, nonce) => {
    const patch = noncePatches[index]!;
    const regex = new RegExp(patch.replacement.match.source.replaceAll("\\i", "[A-Za-z_$][\\w$]*"));
    const once = source.replace(regex, patch.replacement.replace);
    expect(once).toContain(nonce);
    expect(once.replace(regex, patch.replacement.replace)).toBe(once);
  });
});
