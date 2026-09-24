import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Release builds (deploy/deploy.sh, CI) set NEXT_OUTPUT=standalone to get a
  // self-contained server (.next/standalone/server.js) that runs from its release
  // directory without the source tree. Off by default: tracing uses symlinks,
  // which Windows refuses without Developer Mode.
  output: process.env.NEXT_OUTPUT === "standalone" ? "standalone" : undefined,
  reactStrictMode: true,
  poweredByHeader: false,
  // RSI CDN hosts avatars and org banners. Allow them through next/image optimisation.
  images: {
    remotePatterns: [
      { protocol: "https", hostname: "cdn.robertsspaceindustries.com" },
      { protocol: "https", hostname: "robertsspaceindustries.com" },
      { protocol: "https", hostname: "media.robertsspaceindustries.com" },
    ],
  },
  experimental: {
    // Typed routes would be nice but conflict with dynamic segments on v15.1.
  },
  // In dev we hit a self-signed cert; skip validation for the fetch inside RSC/BFF.
  // This is only effective for Node fetch inside server code — browser calls still
  // go through the browser's TLS stack.
};

export default nextConfig;
