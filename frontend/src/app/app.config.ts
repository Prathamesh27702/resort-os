import { ApplicationConfig, inject, isDevMode, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideServiceWorker } from '@angular/service-worker';
import { MatIconRegistry } from '@angular/material/icon';
import { routes } from './app.routes';
import { authInterceptor } from './core/http';
import { AuthService } from './core/auth.service';
import { PropertyContext } from './core/property-context';
import { provideIsoDates } from './core/dates';
import { InstallPrompt } from './core/install';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    // Date fields show "15 Oct 2026" with a calendar; their values stay ISO strings for the API.
    provideIsoDates(),
    // Installable app (Sprint 18): the service worker caches the app itself so it opens fast from the home screen. API
    // calls always go to the server (working offline comes in Sprint 28). Production builds only.
    provideServiceWorker('ngsw-worker.js', { enabled: !isDevMode(), registrationStrategy: 'registerWhenStable:30000' }),
    provideAppInitializer(() => { inject(InstallPrompt); }),
    provideAppInitializer(() => { inject(MatIconRegistry).setDefaultFontSetClass('material-symbols-outlined'); }),
    // Restore the session from the refresh cookie before the first route renders.
    provideAppInitializer(async () => {
      // inject() only works before the first await.
      const auth = inject(AuthService);
      const properties = inject(PropertyContext);
      await auth.restore();
      if (auth.me() && !auth.me()!.mustChangePassword) await properties.load();
    }),
  ],
};
