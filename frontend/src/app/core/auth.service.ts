import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, finalize, firstValueFrom, map, shareReplay, tap } from 'rxjs';
import { AuthResponse, Me } from './models';

export const CLIENT_HEADER = new HttpHeaders({ 'X-Resort-Client': 'web' });

/**
 * Session handling.
 *  - The access token lives only in memory (never localStorage), so XSS can't lift a long-lived credential.
 *  - The refresh token is an HttpOnly cookie the script can't read; /api/auth/refresh swaps it for a new pair.
 *  - Concurrent 401s share one in-flight refresh, so a token is never presented twice (that would trip
 *    the server's reuse detection and sign the user out).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private router = inject(Router);

  private token = signal<string | null>(null);
  readonly me = signal<Me | null>(null);
  readonly signedIn = computed(() => this.me() !== null);

  private refreshing$: Observable<string> | null = null;

  get accessToken() { return this.token(); }

  login(email: string, password: string) {
    return this.http.post<AuthResponse>('/api/v1/auth/login', { email, password }).pipe(tap(r => this.accept(r)));
  }

  /** Called once at startup: restores the session from the refresh cookie if there is one. */
  async restore(): Promise<void> {
    try { await firstValueFrom(this.refresh()); } catch { /* not signed in */ }
  }

  refresh(): Observable<string> {
    this.refreshing$ ??= this.http.post<AuthResponse>('/api/v1/auth/refresh', null, { headers: CLIENT_HEADER }).pipe(
      tap(r => this.accept(r)),
      map(r => r.accessToken),
      finalize(() => (this.refreshing$ = null)),
      shareReplay(1),
    );
    return this.refreshing$;
  }

  async logout(): Promise<void> {
    try { await firstValueFrom(this.http.post('/api/v1/auth/logout', null, { headers: CLIENT_HEADER })); } catch { /* ignore */ }
    this.clear();
    await this.router.navigateByUrl('/login');
  }

  changePassword(currentPassword: string, newPassword: string) {
    return this.http.post('/api/v1/auth/change-password', { currentPassword, newPassword });
  }

  clear() { this.token.set(null); this.me.set(null); }

  /** True if the user holds the permission org-wide, or for the given property. */
  can(permission: string, propertyId?: string | null): boolean {
    const me = this.me();
    if (!me) return false;
    return me.grants.some(g =>
      (g.propertyId === null || (propertyId != null && g.propertyId === propertyId)) && g.permissions.includes(permission));
  }

  /** True if the user holds the permission for at least one scope. */
  canAnywhere(permission: string): boolean {
    return !!this.me()?.grants.some(g => g.permissions.includes(permission));
  }

  roleLabel(): string {
    const roles = [...new Set(this.me()?.grants.flatMap(g => g.roles) ?? [])];
    return roles.map(r => r.charAt(0) + r.slice(1).toLowerCase().replace(/_/g, ' ')).join(', ');
  }

  private accept(r: AuthResponse) { this.token.set(r.accessToken); this.me.set(r.me); }
}
