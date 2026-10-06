import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { MatIconRegistry } from '@angular/material/icon';
import { routes } from './app.routes';
import { authInterceptor } from './core/http';
import { AuthService } from './core/auth.service';
import { PropertyContext } from './core/property-context';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
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
