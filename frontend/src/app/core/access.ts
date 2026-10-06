import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Route guard: signed in, and past the forced password change. */
export const signedInGuard: CanActivateFn = (_r, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const me = auth.me();
  if (!me) return router.createUrlTree(['/login'], { queryParams: { next: state.url } });
  if (me.mustChangePassword && !state.url.startsWith('/account/password')) return router.createUrlTree(['/account/password']);
  return true;
};

/** Route guard factory: needs a permission in at least one scope. The API still enforces every call. */
export const permissionGuard = (permission: string): CanActivateFn => () => {
  const auth = inject(AuthService);
  return auth.canAnywhere(permission) ? true : inject(Router).createUrlTree(['/']);
};

export const anonymousGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.me() ? inject(Router).createUrlTree(['/']) : true;
};

