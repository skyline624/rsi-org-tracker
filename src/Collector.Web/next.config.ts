import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // A browser kept open across releases must fetch assets from the current build.
  deploymentId: process.env.NEXT_DEPLOYMENT_ID,
  // Release builds (deploy/deploy.sh, CI) set NEXT_OUTPUT=standalone to get a
  // self-contained server (.next/standalone/server.js) that runs from its release
  // directory without the source tree. Off by default: tracing uses symlinks,
  // which Windows refuses without Developer Mode.
  output: process.env.NEXT_OUTPUT === "standalone" ? "standalone" : undefined,
  reactStrictMode: true,
  poweredByHeader: false,
  // Avatars and banners are loaded straight from the RSI CDN: no /_next/image
  // optimizer (an unauthenticated proxy that fetches remote images for anyone).
  images: {
    unoptimized: true,
  },
  experimental: {
    serverActions: {
      // Voice recordings go through a Server Action; the API caps them at 25 MB.
      bodySizeLimit: "26mb",
    },
  },
};

export default nextConfig;
