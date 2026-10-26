import { Component, inject, OnInit, signal } from '@angular/core';
import { JsonPipe, KeyValuePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { BackofficeApiService, EconomyAsset, EconomyAuctionLot, EconomyAuditRow, EconomyBalance, EconomyItem, EconomyListing, EconomyRecipe, EconomyRecipePart } from '../data/backoffice-api.service';

@Component({
  selector: 'app-backoffice-economy',
  imports: [FormsModule, JsonPipe, KeyValuePipe],
  templateUrl: './economy.component.html'
})
export class EconomyComponent implements OnInit {
  private readonly api = inject(BackofficeApiService);
  readonly assets = signal<EconomyAsset[]>([]);
  readonly listings = signal<EconomyListing[]>([]);
  readonly items = signal<EconomyItem[]>([]);
  readonly recipes = signal<EconomyRecipe[]>([]);
  readonly ledger = signal<EconomyAuditRow[]>([]);
  readonly stockAudit = signal<EconomyAuditRow[]>([]);
  readonly busy = signal(false);
  readonly auctionLots = signal<EconomyAuctionLot[]>([]);
  readonly balances = signal<EconomyBalance[]>([]);
  readonly message = signal('');
  item: EconomyItem = { id: '', name: '', type: '', rarity: '', stats: '{}', asset_key: null, enabled: true };
  recipe: EconomyRecipe = { id: '', name: '', inputs: '[]', outputs: '[]', craft_seconds: 0, enabled: true };
  inputs: EconomyRecipePart[] = []; outputs: EconomyRecipePart[] = [];
  listing: EconomyListing = { id: '', itemId: '', name: '', type: '', rarity: '', prices: {}, stockAvailable: null,
    perAccountLimit: null, restockIntervalSeconds: null, restockAmount: null, enabled: true };
  priceAsset = 'credits'; priceAmount = 0;
  stockTarget: number | null = null;
  ledgerAssetId = ''; ledgerReason = ''; stockAuditReason = '';
  accountId = '';
  assetId = 'credits';
  balanceDelta = 0;
  balanceReason = '';
  listingId = '';
  stockDelta = 0;
  stockReason = '';
  newAssetId = '';
  newAssetName = '';
  newAssetKind: 'currency' | 'resource' = 'currency';
  newAssetEnabled = true;
  auctionId = '';
  auctionItemId = '';
  auctionOpeningBid = 100;
  auctionDurationSeconds = 300;
  auctionEnabled = true;

  async ngOnInit(): Promise<void> { await this.reload(); }

  async reload(): Promise<void> {
    this.busy.set(true);
    try {
      const [assets, listings, auctionLots, items, recipes] = await Promise.all([
        firstValueFrom(this.api.listEconomyAssets()),
        firstValueFrom(this.api.listEconomyListings()),
        firstValueFrom(this.api.listEconomyAuctionLots()),
        firstValueFrom(this.api.listEconomyItems()),
        firstValueFrom(this.api.listEconomyRecipes())
      ]);
      this.assets.set(assets);
      this.listings.set(listings);
      this.auctionLots.set(auctionLots);
      this.items.set(items); this.recipes.set(recipes);
      this.message.set('');
    } catch { this.message.set('Could not load economy data.'); }
    finally { this.busy.set(false); }
  }

  editItem(value: EconomyItem): void { this.item = { ...value }; }
  newItem(): void { this.item = { id: '', name: '', type: '', rarity: '', stats: '{}', asset_key: null, enabled: true }; }
  async saveItem(): Promise<void> {
    this.busy.set(true);
    try {
      const stats = JSON.parse(this.item.stats);
      if (!this.item.id.trim() || !this.item.name.trim() || !this.item.type.trim() || !this.item.rarity.trim()
        || !stats || typeof stats !== 'object' || Array.isArray(stats)) throw new Error();
      await firstValueFrom(this.api.putEconomyItem({ ...this.item, asset_key: this.item.asset_key || null }));
      await this.reload(); this.message.set('Item saved.');
    } catch { this.message.set('Item needs ID, name, type, rarity and JSON object stats; or the save failed.'); }
    finally { this.busy.set(false); }
  }

  editRecipe(value: EconomyRecipe): void {
    this.recipe = { ...value };
    try { this.inputs = JSON.parse(value.inputs); this.outputs = JSON.parse(value.outputs); }
    catch { this.inputs = []; this.outputs = []; this.message.set('Saved recipe parts could not be parsed.'); }
  }
  newRecipe(): void { this.recipe = { id: '', name: '', inputs: '[]', outputs: '[]', craft_seconds: 0, enabled: true }; this.inputs = []; this.outputs = []; }
  partChoices(kind: EconomyRecipePart['kind']): { id: string; name: string }[] {
    return kind === 'item' ? this.items() : this.assets().filter(asset => asset.kind === kind);
  }
  addPart(side: 'inputs' | 'outputs'): void { this[side].push({ kind: 'item', id: '', quantity: 1 }); }
  removePart(side: 'inputs' | 'outputs', index: number): void { this[side].splice(index, 1); }
  async saveRecipe(): Promise<void> {
    const valid = (parts: EconomyRecipePart[]) => parts.length > 0 && parts.every(part =>
      this.partChoices(part.kind).some(choice => choice.id === part.id) && Number.isSafeInteger(part.quantity) && part.quantity > 0);
    if (!this.recipe.id.trim() || !this.recipe.name.trim() || !Number.isSafeInteger(this.recipe.craft_seconds)
      || this.recipe.craft_seconds < 0 || !valid(this.inputs) || !valid(this.outputs)) {
      this.message.set('Recipe needs a name, nonnegative craft time, and valid input and output parts.'); return;
    }
    this.busy.set(true);
    try { await firstValueFrom(this.api.putEconomyRecipe({ ...this.recipe, inputs: JSON.stringify(this.inputs), outputs: JSON.stringify(this.outputs) }));
      await this.reload(); this.message.set('Recipe saved.'); }
    catch { this.message.set('Could not save recipe.'); }
    finally { this.busy.set(false); }
  }

  editListing(value: EconomyListing): void { this.listing = { ...value, prices: { ...value.prices } }; }
  newListing(): void { this.listing = { id: '', itemId: '', name: '', type: '', rarity: '', prices: {}, stockAvailable: null,
    perAccountLimit: null, restockIntervalSeconds: null, restockAmount: null, enabled: true }; }
  setPrice(): void {
    if (!this.priceAsset || !Number.isSafeInteger(this.priceAmount) || this.priceAmount < 0) { this.message.set('Price must be a nonnegative whole amount.'); return; }
    this.listing.prices = { ...this.listing.prices, [this.priceAsset]: this.priceAmount };
  }
  removePrice(asset: string): void { const prices = { ...this.listing.prices }; delete prices[asset]; this.listing.prices = prices; }
  async saveListing(): Promise<void> {
    const value = this.listing;
    if (!value.id.trim() || !this.items().some(item => item.id === value.itemId) || !Object.keys(value.prices).length
      || Object.keys(value.prices).some(id => !this.assets().some(asset => asset.id === id && asset.kind === 'currency' && asset.enabled)
        || !Number.isSafeInteger(value.prices[id]) || value.prices[id] < 0)
      || (value.stockAvailable !== null && (!Number.isSafeInteger(value.stockAvailable) || value.stockAvailable < 0))
      || (value.perAccountLimit !== null && (!Number.isSafeInteger(value.perAccountLimit) || value.perAccountLimit < 1))
      || (value.restockIntervalSeconds === null) !== (value.restockAmount === null)
      || (value.restockIntervalSeconds !== null && (!Number.isSafeInteger(value.restockIntervalSeconds) || value.restockIntervalSeconds < 1))
      || (value.restockAmount !== null && (!Number.isSafeInteger(value.restockAmount) || value.restockAmount < 1))) {
      this.message.set('Listing needs an item, price, valid stock/limit, and paired positive restock values.'); return;
    }
    this.busy.set(true);
    try { await firstValueFrom(this.api.putEconomyListing(value)); await this.reload();
      this.message.set('Listing saved. Existing stock is unchanged; use stock adjustment to change it.'); }
    catch { this.message.set('Could not save listing.'); }
    finally { this.busy.set(false); }
  }

  editAsset(asset: EconomyAsset): void { this.newAssetId = asset.id; this.newAssetName = asset.name; this.newAssetKind = asset.kind; this.newAssetEnabled = asset.enabled; }
  async loadLedger(beforeId?: number): Promise<void> {
    if (!this.accountId.trim()) { this.message.set('Enter an account UUID.'); return; }
    this.busy.set(true);
    try { this.ledger.set(await firstValueFrom(this.api.getEconomyLedger(this.accountId.trim(),
      { ...(this.ledgerAssetId ? { assetId: this.ledgerAssetId } : {}), ...(this.ledgerReason ? { reason: this.ledgerReason } : {}), ...(beforeId ? { beforeId } : {}) }))); }
    catch { this.message.set('Could not load ledger.'); }
    finally { this.busy.set(false); }
  }
  async loadStockAudit(beforeId?: number): Promise<void> {
    if (!this.listingId) { this.message.set('Choose a listing.'); return; }
    this.busy.set(true);
    try { this.stockAudit.set(await firstValueFrom(this.api.getEconomyStockAudit(this.listingId,
      { ...(this.stockAuditReason ? { reason: this.stockAuditReason } : {}), ...(beforeId ? { beforeId } : {}) }))); }
    catch { this.message.set('Could not load stock audit.'); }
    finally { this.busy.set(false); }
  }
  async setStock(): Promise<void> {
    const current = this.listings().find(listing => listing.id === this.listingId)?.stockAvailable;
    if (current == null || this.stockTarget == null || !Number.isSafeInteger(this.stockTarget) || this.stockTarget < 0 || current === this.stockTarget) {
      this.message.set('Choose finite stock and a different nonnegative target.'); return;
    }
    this.stockDelta = this.stockTarget - current; await this.adjustStock();
  }

  async loadBalances(): Promise<void> {
    if (!this.accountId.trim()) { this.message.set('Enter an account UUID.'); return; }
    this.busy.set(true);
    try { this.balances.set(await firstValueFrom(this.api.getEconomyBalances(this.accountId.trim()))); }
    catch { this.message.set('Could not load account balances.'); }
    finally { this.busy.set(false); }
  }

  async addAsset(): Promise<void> {
    if (!this.newAssetId.trim() || !this.newAssetName.trim()) { this.message.set('Asset ID and name are required.'); return; }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.putEconomyAsset({ id: this.newAssetId.trim(), name: this.newAssetName.trim(), kind: this.newAssetKind, enabled: this.newAssetEnabled }));
      this.newAssetId = ''; this.newAssetName = '';
      await this.reload();
    } catch { this.message.set('Could not save the asset.'); }
    finally { this.busy.set(false); }
  }

  async adjustStock(): Promise<void> {
    if (!this.listingId || !Number.isSafeInteger(this.stockDelta) || this.stockDelta === 0 || !this.stockReason.trim()) {
      this.message.set('Choose a finite listing, nonzero whole stock change, and reason.'); return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.adjustEconomyStock(this.listingId, this.stockDelta, this.stockReason.trim()));
      await this.reload();
    } catch { this.message.set('Stock change was rejected.'); }
    finally { this.busy.set(false); }
  }

  async adjustBalance(): Promise<void> {
    if (!this.accountId.trim() || !this.assetId || !Number.isSafeInteger(this.balanceDelta) || this.balanceDelta <= 0 || !this.balanceReason.trim()) {
      this.message.set('Grant needs an account, asset, positive whole amount, and reason.'); return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.adjustEconomyBalance(this.accountId.trim(), this.assetId,
        this.balanceDelta, this.balanceReason.trim(), crypto.randomUUID()));
      await this.loadBalances();
      this.message.set('Balance updated.');
    } catch { this.message.set('Balance change was rejected.'); }
    finally { this.busy.set(false); }
  }

  async saveAuctionLot(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.putEconomyAuctionLot({ id: this.auctionId.trim(),
        itemId: this.auctionItemId.trim(), openingBid: this.auctionOpeningBid,
        durationSeconds: this.auctionDurationSeconds, enabled: this.auctionEnabled }));
      await this.reload();
      this.message.set('Auction lot saved.');
    } catch { this.message.set('Could not save the auction lot.'); }
    finally { this.busy.set(false); }
  }
}
