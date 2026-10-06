import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { AuthResponse } from './models';

const PROPERTY_A = 'aaaaaaaa-0000-0000-0000-000000000001';
const PROPERTY_B = 'bbbbbbbb-0000-0000-0000-000000000002';

function signIn(auth: AuthService, http: HttpTestingController, grants: AuthResponse['me']['grants']) {
  auth.login('x@test.local', 'secret12345').subscribe();
  http.expectOne('/api/v1/auth/login').flush({
    accessToken: 'token', accessTokenExpiresAt: new Date().toISOString(),
    me: { userId: 'u1', email: 'x@test.local', fullName: 'X Person', mustChangePassword: false,
          organization: { id: 'o1', code: 'ORG', name: 'Org' }, grants },
  } satisfies AuthResponse);
}

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('grants org-wide permissions for every property', () => {
    signIn(auth, http, [{ propertyId: null, roles: ['MANAGER'], permissions: ['reservation.read'] }]);
    expect(auth.can('reservation.read', PROPERTY_A)).toBeTrue();
    expect(auth.can('reservation.read', null)).toBeTrue();
  });

  it('limits property-scoped permissions to that property', () => {
    signIn(auth, http, [{ propertyId: PROPERTY_A, roles: ['RECEPTIONIST'], permissions: ['reservation.read'] }]);
    expect(auth.can('reservation.read', PROPERTY_A)).toBeTrue();
    expect(auth.can('reservation.read', PROPERTY_B)).toBeFalse();
    expect(auth.can('reservation.read', null)).toBeFalse();
    expect(auth.canAnywhere('reservation.read')).toBeTrue();
  });

  it('keeps the access token in memory only', () => {
    signIn(auth, http, []);
    expect(auth.accessToken).toBe('token');
    expect(JSON.stringify(localStorage)).not.toContain('token');
    expect(JSON.stringify(sessionStorage)).not.toContain('token');
  });

  it('shares one in-flight refresh between concurrent callers', () => {
    let a = '', b = '';
    auth.refresh().subscribe(t => (a = t));
    auth.refresh().subscribe(t => (b = t));
    const reqs = http.match('/api/v1/auth/refresh');
    expect(reqs.length).toBe(1);
    expect(reqs[0].request.headers.get('X-Resort-Client')).toBe('web');
    reqs[0].flush({ accessToken: 'new', accessTokenExpiresAt: '', me: { userId: 'u', email: '', fullName: '', mustChangePassword: false, organization: { id: '', code: '', name: '' }, grants: [] } });
    expect(a).toBe('new');
    expect(b).toBe('new');
  });
});
