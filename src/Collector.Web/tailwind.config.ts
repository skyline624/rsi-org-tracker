import type { Config } from "tailwindcss";

const config: Config = {
  content: ["./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        hud: {
          bg: "var(--hud-bg)",
          elevated: "var(--hud-bg-elevated)",
          cyan: "var(--hud-cyan)",
          "cyan-dim": "var(--hud-cyan-dim)",
          orange: "var(--hud-orange)",
          red: "var(--hud-red)",
          green: "var(--hud-green)",
          text: "var(--hud-text)",
          "text-dim": "var(--hud-text-dim)",
          grid: "var(--hud-grid)",
        },
      },
      fontFamily: {
        display: ["var(--font-display)", "sans-serif"],
        ui: ["var(--font-ui)", "sans-serif"],
        mono: ["var(--font-mono)", "monospace"],
      },
      boxShadow: {
        "hud-glow": "0 0 12px var(--hud-cyan), inset 0 0 1px var(--hud-cyan)",
        "hud-glow-orange": "0 0 12px var(--hud-orange)",
        "hud-glow-red": "0 0 12px var(--hud-red)",
      },
      keyframes: {
        "boot-in": {
          "0%": { opacity: "0", transform: "translateY(4px)" },
          "100%": { opacity: "1", transform: "translateY(0)" },
        },
        "glow-pulse": {
          "0%,100%": { boxShadow: "0 0 8px var(--hud-cyan)" },
          "50%": { boxShadow: "0 0 20px var(--hud-cyan)" },
        },
      },
      animation: {
        "boot-in": "boot-in 300ms cubic-bezier(0.22, 1, 0.36, 1)",
        "glow-pulse": "glow-pulse 2.4s ease-in-out infinite",
      },
    },
  },
  plugins: [],
};

export default config;
