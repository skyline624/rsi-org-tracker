/** Same nonce paths as upstream ImplicitRelationships; applying these twice is harmless. */
export const noncePatches = [
  { find: ".REQUEST_GUILD_MEMBERS,", replacement: {
    match: /\.REQUEST_GUILD_MEMBERS,\{(?!nonce:)/,
    replace: "$&nonce:arguments[1]?.nonce,", noWarn: true,
  } },
  { find: "GUILD_MEMBERS_REQUEST:", replacement: {
    match: /presences:!!(\i)\.presences(?!,nonce:)/,
    replace: "$&,nonce:$1.nonce", noWarn: true,
  } },
  { find: ".not_found", replacement: {
    match: /notFound:(\i)\.not_found(?!,nonce:)/,
    replace: "$&,nonce:$1.nonce", noWarn: true,
  } },
];
