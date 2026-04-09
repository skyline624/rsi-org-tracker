import type { Metadata } from "next";
import { Orbitron, Rajdhani, JetBrains_Mono } from "next/font/google";
import "./globals.css";
import { Providers } from "./providers";
import { TopNav } from "@/components/layout/TopNav";
import { Footer } from "@/components/layout/Footer";
import { ScanLineOverlay } from "@/components/hud/ScanLineOverlay";
import { getSession } from "@/lib/auth/session";

const orbitron = Orbitron({
  subsets: ["latin"],
  variable: "--font-display",
  display: "swap",
  weight: ["500", "700", "900"],
});

const rajdhani = Rajdhani({
  subsets: ["latin"],
  variable: "--font-ui",
  display: "swap",
  weight: ["400", "500", "600", "700"],
});

const jetbrains = JetBrains_Mono({
  subsets: ["latin"],
  variable: "--font-mono",
  display: "swap",
  weight: ["400", "500", "600"],
});

export const metadata: Metadata = {
  title: "Citizen Intel — Star Citizen Organization Tracker",
  description:
    "Real-time intelligence on Star Citizen organizations: 100k+ orgs, 400k+ citizens, growth trends, join/leave history.",
  robots: { index: true, follow: true },
};

export default async function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const session = await getSession();

  return (
    <html lang="en" className="dark">
      <body
        className={`${orbitron.variable} ${rajdhani.variable} ${jetbrains.variable} min-h-screen antialiased`}
      >
        <Providers>
          <TopNav authenticated={!!session} />
          <main className="mx-auto max-w-[1440px] px-6 py-8">{children}</main>
          <Footer />
          <ScanLineOverlay />
        </Providers>
      </body>
    </html>
  );
}
