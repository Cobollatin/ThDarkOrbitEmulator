import { Routes } from '@angular/router';
import { backofficeGuard, economyAdminGuard, permissionGuard } from './auth/permission.guard';

export const backofficeRoutes: Routes = [
  {
    path: 'backoffice',
    canActivate: [backofficeGuard],
    loadComponent: () =>
      import('./shell/backoffice-shell.component').then((m) => m.BackofficeShellComponent),
    children: [
      { path: '', redirectTo: 'overview', pathMatch: 'full' },
      {
        path: 'overview',
        canActivate: [permissionGuard('backoffice.ops.read')],
        loadComponent: () =>
          import('./overview/overview.component').then((m) => m.OverviewComponent)
      },
      {
        path: 'identity',
        canActivate: [permissionGuard('backoffice.identity.read')],
        loadComponent: () =>
          import('./identity/identity-users.component').then((m) => m.IdentityUsersComponent)
      },
      {
        path: 'keycloak',
        canActivate: [permissionGuard('backoffice.keycloak.read')],
        loadComponent: () =>
          import('./keycloak/keycloak-users.component').then((m) => m.KeycloakUsersComponent)
      },
      {
        path: 'operations',
        canActivate: [permissionGuard('backoffice.cache.write')],
        loadComponent: () =>
          import('./operations/operations.component').then((m) => m.OperationsComponent)
      },
      {
        path: 'mesh',
        canActivate: [permissionGuard('backoffice.ops.read')],
        loadComponent: () => import('./mesh/mesh.component').then((m) => m.MeshComponent)
      },
      {
        path: 'runtime-vars',
        canActivate: [permissionGuard('backoffice.ops.read')],
        loadComponent: () => import('./runtime-vars/runtime-vars.component').then((m) => m.RuntimeVarsComponent)
      },
      {
        path: 'economy',
        canActivate: [economyAdminGuard],
        loadComponent: () =>
          import('./economy/economy.component').then((m) => m.EconomyComponent)
      },
      {
        path: 'cockpit',
        canActivate: [economyAdminGuard],
        loadComponent: () => import('./cockpit/cockpit.component').then((m) => m.CockpitComponent)
      },
      {
        path: 'progression',
        canActivate: [economyAdminGuard],
        loadComponent: () => import('./progression/progression.component').then((m) => m.ProgressionComponent)
      }
    ]
  }
];
