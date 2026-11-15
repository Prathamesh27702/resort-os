import { Injectable, signal } from '@angular/core';

/** The person's theme choice on this device: Light, Dark or Match my phone (docs/design.md 3). */
export type ThemeChoice = 'light' | 'dark' | 'system';

const THEME_KEY = 'ro-theme';
const SOLID_KEY = 'ro-solid';

export const THEME_LABELS: Record<ThemeChoice, string> = { light: 'Light', dark: 'Dark', system: 'Match my phone' };
export const THEME_ICONS: Record<ThemeChoice, string> = { light: 'light_mode', dark: 'dark_mode', system: 'contrast' };

function read(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}
function write(key: string, value: string) {
  try { localStorage.setItem(key, value); } catch { /* storage blocked: the choice lasts until the page reloads */ }
}

/**
 * Light and dark themes and the "Solid panels" setting. The choice is a convenience kept per device in localStorage;
 * index.html applies it before the app starts so nothing flashes, and this service changes it afterwards.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly choice = signal<ThemeChoice>(this.initialChoice());
  /** True when panels are solid instead of frosted glass (cheap phones, "reduce transparency", or the person's choice). */
  readonly solid = signal(document.documentElement.classList.contains('ro-solid'));
  readonly dark = signal(this.isDark(this.choice()));

  private media = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  constructor() {
    this.media?.addEventListener?.('change', () => this.apply(this.choice()));
  }

  /** Light → Dark → Match my phone → Light (the top-bar button). */
  cycle(from?: Element | null) {
    const order: ThemeChoice[] = ['light', 'dark', 'system'];
    this.set(order[(order.indexOf(this.choice()) + 1) % order.length], from);
  }

  set(choice: ThemeChoice, from?: Element | null) {
    write(THEME_KEY, choice);
    const go = () => { this.choice.set(choice); this.apply(choice); };
    const doc = document as Document & { startViewTransition?: (cb: () => void) => unknown };
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    if (doc.startViewTransition && !reduce && from) {
      const r = from.getBoundingClientRect();
      document.documentElement.style.setProperty('--ro-vt-x', `${r.left + r.width / 2}px`);
      document.documentElement.style.setProperty('--ro-vt-y', `${r.top + r.height / 2}px`);
      doc.startViewTransition(go);
    } else {
      go();
    }
  }

  setSolid(on: boolean) {
    write(SOLID_KEY, on ? 'on' : 'off');
    document.documentElement.classList.toggle('ro-solid', on);
    this.solid.set(on);
  }

  private initialChoice(): ThemeChoice {
    const t = read(THEME_KEY);
    return t === 'light' || t === 'dark' ? t : 'system';
  }

  private isDark(choice: ThemeChoice) {
    return choice === 'dark' || (choice === 'system' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches);
  }

  private apply(choice: ThemeChoice) {
    const html = document.documentElement;
    if (choice === 'system') html.removeAttribute('data-theme'); else html.setAttribute('data-theme', choice);
    const dark = this.isDark(choice);
    this.dark.set(dark);
    document.querySelector('meta[name=theme-color]')?.setAttribute('content', dark ? '#071a1f' : '#eaf4f2');
  }
}
