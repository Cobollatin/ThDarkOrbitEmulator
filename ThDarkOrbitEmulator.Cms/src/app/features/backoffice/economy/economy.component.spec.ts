import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { BackofficeApiService } from '../data/backoffice-api.service';
import { EconomyComponent } from './economy.component';

describe('EconomyComponent', () => {
  const api = {
    listEconomyAssets: () => of([{ id: 'credits', kind: 'currency', name: 'Credits', enabled: true }]),
    listEconomyItems: () => of([{ id: 'ship', name: 'Ship', type: 'ship', rarity: 'common', stats: '{}', asset_key: null, enabled: true }]),
    listEconomyRecipes: () => of([]), listEconomyListings: () => of([]), listEconomyAuctionLots: () => of([]),
    putEconomyRecipe: vi.fn(() => of(void 0)), putEconomyItem: vi.fn(() => of(void 0))
  };
  beforeEach(async () => {
    api.putEconomyRecipe.mockClear(); api.putEconomyItem.mockClear();
    await TestBed.configureTestingModule({ imports: [EconomyComponent],
      providers: [{ provide: BackofficeApiService, useValue: api }] }).compileComponents();
  });

  it('shows loaded catalog entries and rejects invalid item stats', async () => {
    const fixture = TestBed.createComponent(EconomyComponent);
    await fixture.componentInstance.reload(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Ship');
    fixture.componentInstance.editItem({
      id: 'ship', name: 'Ship', type: 'ship', rarity: 'common', stats: '[]', asset_key: null, enabled: true
    });
    await fixture.componentInstance.saveItem();
    expect(api.putEconomyItem).not.toHaveBeenCalled();
    expect(fixture.componentInstance.message()).toContain('JSON object');
  });

  it('builds a recipe with referenced input and output items', async () => {
    const fixture = TestBed.createComponent(EconomyComponent);
    await fixture.componentInstance.reload(); fixture.detectChanges();
    const component = fixture.componentInstance;
    component.recipe = { id: 'r1', name: 'Craft', inputs: '[]', outputs: '[]', craft_seconds: 30, enabled: true };
    component.inputs = [{ kind: 'item', id: 'ship', quantity: 1 }];
    component.outputs = [{ kind: 'item', id: 'ship', quantity: 1 }];
    await component.saveRecipe();
    expect(api.putEconomyRecipe).toHaveBeenCalledWith(expect.objectContaining({ craft_seconds: 30,
      inputs: '[{"kind":"item","id":"ship","quantity":1}]' }));
  });
});
