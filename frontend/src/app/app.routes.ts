import { Routes } from '@angular/router';
import { anonymousGuard, permissionGuard, signedInGuard } from './core/access';

/**
 * Staff app routes (brief §64). Each feature is lazy-loaded. Guards only shape the UI —
 * the API enforces every permission again on every call.
 * Routes for later sprints are added by the sprint that builds them (see docs/sprints).
 */
export const routes: Routes = [
  { path: 'login', canActivate: [anonymousGuard], loadComponent: () => import('./features/auth/login.page').then(m => m.LoginPage) },
  {
    path: '',
    canActivate: [signedInGuard],
    loadComponent: () => import('./layout/shell').then(m => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.page').then(m => m.DashboardPage) },
      { path: 'account/password', loadComponent: () => import('./features/auth/password.page').then(m => m.PasswordPage) },

      // /settings
      { path: 'settings', pathMatch: 'full', redirectTo: 'settings/properties' },
      { path: 'settings/properties', canActivate: [permissionGuard('property.read')], loadComponent: () => import('./features/settings/properties/properties.page').then(m => m.PropertiesPage) },
      { path: 'settings/properties/new', canActivate: [permissionGuard('property.create')], loadComponent: () => import('./features/settings/properties/property-edit.page').then(m => m.PropertyEditPage) },
      { path: 'settings/properties/:id', canActivate: [permissionGuard('property.read')], loadComponent: () => import('./features/settings/properties/property-edit.page').then(m => m.PropertyEditPage) },

      // /users, /roles, /audit
      { path: 'users', canActivate: [permissionGuard('user.read')], loadComponent: () => import('./features/users/users.page').then(m => m.UsersPage) },
      { path: 'users/new', canActivate: [permissionGuard('user.create')], loadComponent: () => import('./features/users/user-edit.page').then(m => m.UserEditPage) },
      { path: 'users/:id', canActivate: [permissionGuard('user.read')], loadComponent: () => import('./features/users/user-edit.page').then(m => m.UserEditPage) },
      { path: 'roles', canActivate: [permissionGuard('role.read')], loadComponent: () => import('./features/users/roles.page').then(m => m.RolesPage) },
      { path: 'audit', canActivate: [permissionGuard('audit.read')], loadComponent: () => import('./features/audit/audit.page').then(m => m.AuditPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
