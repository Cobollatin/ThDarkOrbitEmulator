import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { RbacService } from '../../../core/auth/rbac.service';
import { BackofficeApiService, RuntimeVariable, RuntimeVariableChange } from '../data/backoffice-api.service';
import { RuntimeVarsComponent } from './runtime-vars.component';

describe('RuntimeVarsComponent', () => {
  const variable: RuntimeVariable = { key: 'world.movement.speed_multiplier', category: 'Movement',
    valueType: 'double', defaultValue: '1', currentValue: '2' };
  const change: RuntimeVariableChange = { changeId: 7, variableKey: variable.key, category: 'Movement',
    previousValue: '1', newValue: '2', defaultValue: '1', valueType: 'double',
    surface: 'InGameAdmin', changedBy: 'pilot', nodeId: 'node-a', reason: 'test',
    rawCommand: null, changedAtUtc: '2026-10-24T00:00:00Z' };
  const api = {
    listVariables: vi.fn(() => of([variable])), getVariableHistory: vi.fn(() => of([change])),
    setVariable: vi.fn(() => of(change)), resetVariable: vi.fn(() => of(change)),
    rollbackVariable: vi.fn(() => of(change)),
    executeAdminCommand: vi.fn(() => of({ command: 'var list', data: [variable], auditId: 1 }))
  };
  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({ imports: [RuntimeVarsComponent], providers: [
      { provide: BackofficeApiService, useValue: api },
      { provide: RbacService, useValue: { hasPermission: () => true } }
    ] }).compileComponents();
  });

  it('shows live values and all audit attribution, then applies and rolls back', async () => {
    const fixture = TestBed.createComponent(RuntimeVarsComponent);
    const component = fixture.componentInstance;
    await component.refresh(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('InGameAdmin');
    expect(fixture.nativeElement.textContent).toContain('node-a');
    component.reason = 'CMS change';
    component.values[variable.key] = '3';
    await component.save(variable);
    expect(api.setVariable).toHaveBeenCalledWith(variable.key, '3', 'CMS change');
    await component.rollback(change);
    expect(api.rollbackVariable).toHaveBeenCalledWith(7, 'CMS change');
    component.useCommand('mesh status'); await component.execute();
    expect(api.executeAdminCommand).toHaveBeenCalledWith('mesh status');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('var list');
    fixture.destroy();
  });

  it('filters variables and requires a reason for mutations', async () => {
    const fixture = TestBed.createComponent(RuntimeVarsComponent);
    const component = fixture.componentInstance;
    await component.refresh();
    component.category = 'Combat';
    expect(component.filteredVariables()).toEqual([]);
    component.category = 'Movement'; component.search = 'SPEED';
    expect(component.filteredVariables()).toEqual([variable]);
    await component.reset(variable);
    expect(api.resetVariable).not.toHaveBeenCalled();
    expect(component.error()).toContain('reason');
    fixture.destroy();
  });
});
