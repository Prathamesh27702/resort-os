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

      { path: 'settings/room-types', canActivate: [permissionGuard('accommodation.read')], loadComponent: () => import('./features/settings/room-types/room-types.page').then(m => m.RoomTypesPage) },
      { path: 'settings/room-types/new', canActivate: [permissionGuard('accommodation.manage')], loadComponent: () => import('./features/settings/room-types/room-type-edit.page').then(m => m.RoomTypeEditPage) },
      { path: 'settings/room-types/:id', canActivate: [permissionGuard('accommodation.read')], loadComponent: () => import('./features/settings/room-types/room-type-edit.page').then(m => m.RoomTypeEditPage) },

      // /rooms (Sprint 2)
      { path: 'rooms', canActivate: [permissionGuard('accommodation.read')], loadComponent: () => import('./features/rooms/rooms.page').then(m => m.RoomsPage) },
      { path: 'rooms/new', canActivate: [permissionGuard('accommodation.manage')], loadComponent: () => import('./features/rooms/room-edit.page').then(m => m.RoomEditPage) },
      { path: 'rooms/status', canActivate: [permissionGuard('accommodation.read')], loadComponent: () => import('./features/rooms/room-status.page').then(m => m.RoomStatusPage) },
      { path: 'rooms/:id', canActivate: [permissionGuard('accommodation.read')], loadComponent: () => import('./features/rooms/room-edit.page').then(m => m.RoomEditPage) },

      // /guests (Sprint 3)
      { path: 'guests', canActivate: [permissionGuard('guest.read')], loadComponent: () => import('./features/guests/guests.page').then(m => m.GuestsPage) },
      { path: 'guests/new', canActivate: [permissionGuard('guest.create')], loadComponent: () => import('./features/guests/guest-edit.page').then(m => m.GuestEditPage) },
      { path: 'guests/:id/edit', canActivate: [permissionGuard('guest.update')], loadComponent: () => import('./features/guests/guest-edit.page').then(m => m.GuestEditPage) },
      { path: 'guests/:id', canActivate: [permissionGuard('guest.read')], loadComponent: () => import('./features/guests/guest.page').then(m => m.GuestPage) },

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
