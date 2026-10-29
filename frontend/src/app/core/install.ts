import { Injectable, signal } from '@angular/core';

/** Chrome's install prompt event (not in the DOM typings yet). */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

const DISMISSED = 'resort.install.dismissed';

/**
 * Installing the staff app on a phone's home screen (Sprint 18). Android and desktop Chrome raise `beforeinstallprompt`
 * once the app qualifies (manifest, icons, service worker); we keep it and show our own "Install" button instead of the
 * browser's mini-bar. iPhones have no such event: we show how to do it from Safari's Share menu. Created at start-up so
 * the event is never missed.
 */
@Injectable({ providedIn: 'root' })
export class InstallPrompt {
  private deferred: BeforeInstallPromptEvent | null = null;
  /** The browser offered to install and we can show the button. */
  readonly canInstall = signal(false);
  /** Running as the installed app (from the home screen). */
  readonly installed = signal(standalone());
  /** An iPhone or iPad in Safari, not installed: show the Share → Add to Home Screen hint. */
  readonly iosHint = signal(!standalone() && /iphone|ipad|ipod/i.test(navigator.userAgent));
  /** The person closed the hint on this device. */
  readonly dismissed = signal(read());

  constructor() {
    window.addEventListener('beforeinstallprompt', e => {
      e.preventDefault();
      this.deferred = e as BeforeInstallPromptEvent;
      this.canInstall.set(true);
    });
    window.addEventListener('appinstalled', () => {
      this.deferred = null;
      this.canInstall.set(false);
      this.installed.set(true);
    });
  }

  /** Shows the browser's install dialog. Resolves true when the person installed. */
  async install(): Promise<boolean> {
    const e = this.deferred;
    if (!e) return false;
    await e.prompt();
    const { outcome } = await e.userChoice;
    this.deferred = null;
    this.canInstall.set(false);
    return outcome === 'accepted';
  }

  dismiss() {
    this.dismissed.set(true);
    try { localStorage.setItem(DISMISSED, '1'); } catch { /* storage unavailable */ }
  }
}

function standalone(): boolean {
  return window.matchMedia?.('(display-mode: standalone)').matches || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

function read(): boolean {
  try { return localStorage.getItem(DISMISSED) === '1'; } catch { return false; }
}
