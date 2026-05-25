import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../../identity/auth/auth.service';
import { RbacService } from '../../../core/auth/rbac.service';

export const permissionGuard =
  (permission: string): CanActivateFn =>
  () => {
    const auth = inject(AuthService);
    const rbac = inject(RbacService);
    const router = inject(Router);

    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login']);
    }

    if (!rbac.hasPermission(permission)) {
      return router.createUrlTree(['/']);
    }

    return true;
  };

export const backofficeGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const rbac = inject(RbacService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login']);
  }

  if (!rbac.canAccessBackoffice()) {
    return router.createUrlTree(['/']);
  }

  return true;
};
