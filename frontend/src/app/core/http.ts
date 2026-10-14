import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { FormGroup } from '@angular/forms';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { Problem } from './models';

const NO_RETRY = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

/** Adds the bearer token; on a 401 refreshes once and retries the original request. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const withToken = (t: string | null) => (t ? req.clone({ setHeaders: { Authorization: `Bearer ${t}` } }) : req);

  return next(withToken(auth.accessToken)).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse)) return throwError(() => err);

      if (err.status === 401 && !NO_RETRY.some(u => req.url.startsWith(u))) {
        return auth.refresh().pipe(
          switchMap(t => next(withToken(t))),
          catchError(e => {
            auth.clear();
            router.navigateByUrl('/login');
            return throwError(() => e);
          }),
        );
      }
      if (err.status === 403 && toProblem(err).code === 'PASSWORD_CHANGE_REQUIRED') {
        router.navigateByUrl('/account/password');
      }
      return throwError(() => err);
    }),
  );
};

/** Normalises any HTTP error into the API's problem shape. */
export function toProblem(err: unknown): Problem {
  if (err instanceof HttpErrorResponse) {
    const b = err.error ?? {};
    if (err.status === 0) return { status: 0, code: 'OFFLINE', detail: 'Can’t reach the server. Check the connection and try again.' };
    if (err.status === 429) return { status: 429, code: 'RATE_LIMITED', detail: b.message ?? 'Too many attempts. Wait a minute and try again.' };
    // API error shape (brief §66): { code, message, details, correlationId }
    return { status: err.status, code: b.code ?? 'ERROR', detail: b.message ?? 'Something went wrong.',
             errors: b.details ?? undefined, correlationId: b.correlationId };
  }
  return { status: 0, code: 'ERROR', detail: 'Something went wrong.' };
}

/** First validation message per field, keyed by lower-camel field name (matches form control names). */
export function fieldErrors(p: Problem): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(p.errors ?? {})) out[k.charAt(0).toLowerCase() + k.slice(1)] = v[0];
  return out;
}

/** The form path for an API field name: "Components[0].SharePercent" → "components.0.sharePercent". */
export function controlPath(apiField: string): string {
  return apiField.replace(/\[(\d+)\]/g, '.$1').split('.')
    .map(s => s.charAt(0).toLowerCase() + s.slice(1)).join('.');
}

/**
 * Puts the API's field messages on the matching form controls as `{ server: message }`, so each one shows in that
 * field's <mat-error> (Material only shows an error for a control that is in error). Nested names such as
 * "Components[0].Description" reach controls inside form arrays. Returns the messages by field.
 */
export function showFieldErrors(form: FormGroup, p: Problem): Record<string, string> {
  const errs = fieldErrors(p);
  for (const [k, msg] of Object.entries(errs)) {
    const c = form.get(k) ?? form.get(controlPath(k));
    if (c) { c.setErrors({ ...c.errors, server: msg }); c.markAsTouched(); }
  }
  return errs;
}
