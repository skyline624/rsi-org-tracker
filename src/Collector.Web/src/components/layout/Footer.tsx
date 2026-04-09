export function Footer() {
  return (
    <footer className="mt-16 border-t border-hud-cyan/20 py-6">
      <div className="mx-auto flex max-w-[1440px] items-center justify-between px-6 font-mono text-[10px] uppercase tracking-[0.2em] text-hud-text-dim">
        <span>
          CITIZEN_INTEL · UNOFFICIAL TRACKER · NOT AFFILIATED WITH RSI
        </span>
        <span>
          DATA FROM{" "}
          <a
            href="https://robertsspaceindustries.com"
            rel="noreferrer"
            target="_blank"
            className="text-hud-cyan hover:text-hud-orange"
          >
            robertsspaceindustries.com
          </a>
        </span>
      </div>
    </footer>
  );
}
