#!/usr/bin/env node
/**
 * ThDarkOrbitEmulator Interactive Server Admin CLI
 * - Live Tab Autocompletion & Ghost Suggestions (slash commands, live runtime variables, aliases, currencies, NPCs, maps)
 * - Local AI Natural-Language Assistant (Ollama / LM Studio auto-detection + instant built-in semantic intent & fuzzy resolver)
 */
import readline from 'node:readline';
import process from 'node:process';

process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

// â”€â”€â”€ ANSI Styling â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
const C = {
  reset: '\x1b[0m',
  bold: '\x1b[1m',
  dim: '\x1b[2m',
  cyan: '\x1b[36m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  magenta: '\x1b[35m',
  red: '\x1b[31m',
  gray: '\x1b[90m',
  brightCyan: '\x1b[96m',
  brightGreen: '\x1b[92m',
};

// â”€â”€â”€ Built-in Catalog & Aliases â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
const BUILTIN_VARIABLES = [
  { key: 'world.ship.pusat_plus_base_speed', category: 'Movement', valueType: 'int', defaultValue: '370', currentValue: '370', range: '0..1000000', desc: 'Pusat Plus (slot 1) player base ship speed (alias: world.player.base_speed)' },
  { key: 'world.ship.phoenix_base_speed', category: 'Movement', valueType: 'int', defaultValue: '320', currentValue: '320', range: '0..1000000', desc: 'Phoenix (slot 2) starter ship base speed' },
  { key: 'world.movement.speed_multiplier', category: 'Movement', valueType: 'double', defaultValue: '1', currentValue: '1', range: '>0..100', desc: 'Global ship movement speed multiplier' },
  { key: 'world.tick_rate_ms', category: 'World', valueType: 'int', defaultValue: '1000', currentValue: '1000', range: '20..60000', desc: 'World simulation tick interval in milliseconds' },
  { key: 'world.radiation.damage_per_attempt', category: 'World', valueType: 'int', defaultValue: '0', currentValue: '0', range: '0..1000000', desc: 'Hull damage dealt inside the radiation zone per tick' },
  { key: 'world.config.switch_cooldown_ms', category: 'World', valueType: 'int', defaultValue: '5000', currentValue: '5000', range: '0..1000000', desc: 'Cooldown between ship configuration 1/2 switches (ms)' },
  { key: 'world.portal.jump_radius', category: 'World', valueType: 'int', defaultValue: '500', currentValue: '500', range: '0..1000000', desc: 'Maximum distance from a jumpgate portal to jump' },
  { key: 'world.station.peace_radius', category: 'World', valueType: 'int', defaultValue: '1000', currentValue: '1000', range: '0..1000000', desc: 'Safe-zone & repair radius around home space station' },
  { key: 'world.station.repair_hp_per_tick', category: 'World', valueType: 'int', defaultValue: '0', currentValue: '0', range: '0..1000000', desc: 'Hull HP repaired per tick when parked at home station' },
  { key: 'world.spawn.streuner_count', category: 'Spawns', valueType: 'int', defaultValue: '3', currentValue: '3', range: '0..30', desc: 'Streuner NPC spawn count per map' },
  { key: 'world.spawn.lordakia_count', category: 'Spawns', valueType: 'int', defaultValue: '2', currentValue: '2', range: '0..30', desc: 'Lordakia NPC spawn count per map' },
  { key: 'world.spawn.saimon_count', category: 'Spawns', valueType: 'int', defaultValue: '1', currentValue: '1', range: '0..30', desc: 'Saimon NPC spawn count per map' },
  { key: 'world.spawn.bonus_box_count', category: 'Spawns', valueType: 'int', defaultValue: '6', currentValue: '6', range: '0..1000', desc: 'Bonus box & ore collectible count per map' },
  { key: 'world.collectibles.respawn_seconds', category: 'Collectibles', valueType: 'int', defaultValue: '300', currentValue: '300', range: '1..86400', desc: 'Collectible spawn cycle duration in seconds' },
  { key: 'world.collectibles.pickup_radius', category: 'Collectibles', valueType: 'int', defaultValue: '180', currentValue: '180', range: '0..1000000', desc: 'Maximum distance to collect a box or ore' },
  { key: 'world.combat.laser_damage_multiplier', category: 'Combat', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1000000', desc: 'Global laser combat damage multiplier' },
  { key: 'world.combat.fire_range', category: 'Combat', valueType: 'int', defaultValue: '1000', currentValue: '1000', range: '0..1000000', desc: 'Maximum laser attack range' },
  { key: 'world.combat.shield_absorption_ratio', category: 'Combat', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1', desc: 'Fraction of incoming damage absorbed by shield' },
  { key: 'economy.credits_reward_multiplier', category: 'Economy', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1000000', desc: 'Multiplier for NPC & box credit rewards' },
  { key: 'economy.uridium_reward_multiplier', category: 'Economy', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1000000', desc: 'Multiplier for NPC & box uridium rewards' },
  { key: 'economy.xp_reward_multiplier', category: 'Economy', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1000000', desc: 'Multiplier for NPC experience rewards' },
  { key: 'economy.honor_reward_multiplier', category: 'Economy', valueType: 'double', defaultValue: '1', currentValue: '1', range: '0..1000000', desc: 'Multiplier for NPC honor rewards' },
  { key: 'economy.shop_discount_percent', category: 'Economy', valueType: 'double', defaultValue: '0', currentValue: '0', range: '0..100', desc: 'Percentage discount across all shop listings' },
  { key: 'mesh.desired_replicas', category: 'ServerMesh', valueType: 'int', defaultValue: '1', currentValue: '1', range: '1..16', desc: 'Desired number of active world server nodes' },
  { key: 'mesh.max_maps_per_node', category: 'ServerMesh', valueType: 'int', defaultValue: '4', currentValue: '4', range: '1..256', desc: 'Maximum maps leased per world server node' },
  { key: 'mesh.heartbeat_ttl_seconds', category: 'ServerMesh', valueType: 'int', defaultValue: '30', currentValue: '30', range: '21..3600', desc: 'Node lease TTL in seconds' },
];

const VARIABLE_ALIASES = {
  'world.player.base_speed': 'world.ship.pusat_plus_base_speed',
  'world.player.speed': 'world.ship.pusat_plus_base_speed',
  'player.base_speed': 'world.ship.pusat_plus_base_speed',
  'player.speed': 'world.ship.pusat_plus_base_speed',
  'base_speed': 'world.ship.pusat_plus_base_speed',
  'pusat_base_speed': 'world.ship.pusat_plus_base_speed',
  'phoenix_base_speed': 'world.ship.phoenix_base_speed',
  'world.player.phoenix_base_speed': 'world.ship.phoenix_base_speed',
  'speed_multiplier': 'world.movement.speed_multiplier',
  'world.speed_multiplier': 'world.movement.speed_multiplier',
  'laser_damage': 'world.combat.laser_damage_multiplier',
  'combat.laser_damage': 'world.combat.laser_damage_multiplier',
  'world.combat.laser_damage': 'world.combat.laser_damage_multiplier',
  'fire_range': 'world.combat.fire_range',
  'combat.fire_range': 'world.combat.fire_range',
  'shield_absorption': 'world.combat.shield_absorption_ratio',
  'tick_rate': 'world.tick_rate_ms',
  'world.tick_rate': 'world.tick_rate_ms',
  'streuner_count': 'world.spawn.streuner_count',
  'world.npc.streuner_count': 'world.spawn.streuner_count',
  'lordakia_count': 'world.spawn.lordakia_count',
  'world.npc.lordakia_count': 'world.spawn.lordakia_count',
  'saimon_count': 'world.spawn.saimon_count',
  'world.npc.saimon_count': 'world.spawn.saimon_count',
  'bonus_box_count': 'world.spawn.bonus_box_count',
  'world.box.bonus_box_count': 'world.spawn.bonus_box_count',
  'pickup_radius': 'world.collectibles.pickup_radius',
  'respawn_seconds': 'world.collectibles.respawn_seconds',
  'jump_radius': 'world.portal.jump_radius',
  'peace_radius': 'world.station.peace_radius',
  'repair_hp': 'world.station.repair_hp_per_tick',
  'radiation_damage': 'world.radiation.damage_per_attempt',
  'shop_discount': 'economy.shop_discount_percent',
  'credits_multiplier': 'economy.credits_reward_multiplier',
  'uridium_multiplier': 'economy.uridium_reward_multiplier',
  'xp_multiplier': 'economy.xp_reward_multiplier',
  'honor_multiplier': 'economy.honor_reward_multiplier',
};

const MAP_ALIASES = {
  '1-1': '1', '1-2': '2', '1-3': '3', '1-4': '4',
  '2-1': '5', '2-2': '6', '2-3': '7', '2-4': '8',
  '3-1': '9', '3-2': '10', '3-3': '11', '3-4': '12',
  '4-1': '13', '4-2': '14', '4-3': '15', '4-4': '16',
};

const SLASH_COMMANDS = [
  { cmd: '/getvar', usage: '/getvar <key>', desc: 'Inspect a runtime variable (e.g. /getvar world.player.base_speed)' },
  { cmd: '/setvar', usage: '/setvar <key> <value> [reason]', desc: 'Update a runtime variable live across the cluster' },
  { cmd: '/resetvar', usage: '/resetvar <key> [reason]', desc: 'Reset a runtime variable to its catalog default' },
  { cmd: '/vars', usage: '/vars [category]', desc: 'List all runtime variables and their live values' },
  { cmd: '/varhistory', usage: '/varhistory [key] [limit]', desc: 'Show audit history of runtime variable changes' },
  { cmd: '/rollbackvar', usage: '/rollbackvar <changeId> [reason]', desc: 'Roll back a specific runtime variable change' },
  { cmd: '/grant', usage: '/grant <currency> <amount> [pilot]', desc: 'Grant credits, uridium, xp, or honor to a pilot' },
  { cmd: '/spawn', usage: '/spawn <kind> [map] [x] [y]', desc: 'Spawn NPC (streuner, lordakia, saimon) or box/ore on a map' },
  { cmd: '/tp', usage: '/tp [pilot] <map> <x> <y>', desc: 'Teleport a pilot to map (1-1..4-4 or 1..16) and coordinates' },
  { cmd: '/heal', usage: '/heal [pilot]', desc: 'Restore a pilot ship to full hull HP and shield' },
  { cmd: '/announce', usage: '/announce <message>', desc: 'Broadcast a server announcement to all pilots' },
  { cmd: '/status', usage: '/status [map]', desc: 'Show active pilots, maps, and world state' },
  { cmd: '/mesh', usage: '/mesh [status|scale|rebalance|drain|activate]', desc: 'Inspect or control the server mesh' },
  { cmd: '/ai', usage: '/ai <natural language request>', desc: 'Translate natural English into server admin commands' },
  { cmd: '/help', usage: '/help', desc: 'Show available slash commands, aliases, and AI examples' },
  { cmd: '/exit', usage: '/exit', desc: 'Exit the interactive admin CLI' },
];

const CURRENCIES = ['uridium', 'credits', 'xp', 'honor', 'experience', 'galaxy_energy', 'prometium', 'endurium', 'terbium'];
const SPAWN_KINDS = ['streuner', 'lordakia', 'saimon', 'box', 'ore', 'mordon', 'devolarium', 'sibelon', 'kristallin', 'kristallon', 'cubikon'];
const MAP_TOKENS = ['1-1', '1-2', '1-3', '1-4', '2-1', '2-2', '2-3', '2-4', '3-1', '3-2', '3-3', '3-4', '4-1', '4-2', '4-3', '4-4'];

// â”€â”€â”€ CLI Argument Parsing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function parseArgs(argv) {
  const opts = {
    baseUri: process.env.THDARKORBIT_GATEWAY_URL || 'https://localhost:60952',
    username: 'owner',
    password: 'owner',
    aiQuery: '',
    commandText: '',
    autoExecute: false,
  };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if ((a === '--url' || a === '--gateway' || a === '-u') && argv[i + 1]) opts.baseUri = argv[++i];
    else if (a === '--user' && argv[i + 1]) opts.username = argv[++i];
    else if (a === '--pass' && argv[i + 1]) opts.password = argv[++i];
    else if (a === '--ai' && argv[i + 1]) opts.aiQuery = argv[++i];
    else if ((a === '--command' || a === '-c') && argv[i + 1]) opts.commandText = argv[++i];
    else if (a === '--auto-execute' || a === '-y') opts.autoExecute = true;
  }
  opts.baseUri = opts.baseUri.replace(/\/+$/, '');
  return opts;
}

// â”€â”€â”€ Normalization & Alias Resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
export function resolveVariableAlias(key) {
  if (!key) return key;
  const lower = key.trim().toLowerCase();
  return VARIABLE_ALIASES[lower] || lower;
}

export function resolveMapAlias(mapToken) {
  if (!mapToken) return mapToken;
  const trimmed = String(mapToken).trim();
  return MAP_ALIASES[trimmed] || trimmed;
}

function normalizeCurrency(asset) {
  const lower = String(asset || '').toLowerCase();
  if (lower === 'experience' || lower === 'exp') return 'xp';
  if (lower === 'uri') return 'uridium';
  if (lower === 'cred' || lower === 'creds') return 'credits';
  return lower;
}

function normalizeSpawnKind(kind) {
  const lower = String(kind || '').toLowerCase().replace(/s$/, '');
  if (['streuner', 'lordakia', 'saimon', 'box', 'ore'].includes(lower)) return lower;
  // Map higher-tier NPC names to closest supported live spawn kind if needed
  if (['mordon', 'sibelon', 'devolarium'].includes(lower)) return 'lordakia';
  if (['kristallin', 'kristallon', 'cubikon'].includes(lower)) return 'saimon';
  return lower;
}

export function normalizeAdminCommand(rawText) {
  if (!rawText || !rawText.trim()) return '';
  const trimmed = rawText.trim();

  if (!trimmed.startsWith('/')) {
    const parts = trimmed.split(/\s+/);
    if (parts.length >= 3 && parts[0].toLowerCase() === 'var' && /^(get|set|reset|history)$/i.test(parts[1])) {
      parts[2] = resolveVariableAlias(parts[2]);
      return parts.join(' ');
    }
    return trimmed;
  }

  const withoutSlash = trimmed.slice(1).trim();
  if (!withoutSlash) return 'help';
  const tokens = withoutSlash.split(/\s+/);
  const verb = tokens[0].toLowerCase();
  const rest = tokens.slice(1);

  switch (verb) {
    case 'help':
      return 'help';
    case 'getvar':
      return rest.length >= 1 ? `var get ${resolveVariableAlias(rest[0])}` : 'var list';
    case 'setvar':
      if (rest.length >= 2) {
        const key = resolveVariableAlias(rest[0]);
        return `var set ${key} ${rest.slice(1).join(' ')}`;
      }
      return `var set ${rest.join(' ')}`.trim();
    case 'resetvar':
      if (rest.length >= 1) {
        const key = resolveVariableAlias(rest[0]);
        return `var reset ${key}${rest.length > 1 ? ' ' + rest.slice(1).join(' ') : ''}`;
      }
      return 'var reset';
    case 'vars':
    case 'listvars':
    case 'varlist':
      return rest.length >= 1 ? `var list ${rest.join(' ')}` : 'var list';
    case 'varhistory':
      if (rest.length >= 1) {
        const first = /^\d+$/.test(rest[0]) ? rest[0] : resolveVariableAlias(rest[0]);
        return `var history ${first}${rest.length > 1 ? ' ' + rest.slice(1).join(' ') : ''}`;
      }
      return 'var history';
    case 'rollbackvar':
      return `var rollback ${rest.join(' ')}`.trim();
    case 'var':
      if (rest.length >= 2 && /^(get|set|reset|history)$/i.test(rest[0])) {
        rest[1] = resolveVariableAlias(rest[1]);
      }
      return `var ${rest.join(' ')}`.trim();
    case 'tp':
      if (rest.length === 1) return `world tp admin ${resolveMapAlias(rest[0])} 2000 1300`;
      if (rest.length === 3) return `world tp admin ${resolveMapAlias(rest[0])} ${rest[1]} ${rest[2]}`;
      if (rest.length >= 4) return `world tp ${rest[0]} ${resolveMapAlias(rest[1])} ${rest[2]} ${rest[3]}`;
      return `world tp ${rest.join(' ')}`.trim();
    case 'heal':
      return rest.length === 0 ? 'world heal admin' : `world heal ${rest[0]}`;
    case 'spawn': {
      if (rest.length === 0) return 'world spawn streuner 1 2000 1300';
      const kind = normalizeSpawnKind(rest[0]);
      if (rest.length === 1) return `world spawn ${kind} 1 2000 1300`;
      if (rest.length === 2) return `world spawn ${kind} ${resolveMapAlias(rest[1])} 2000 1300`;
      if (rest.length >= 4) return `world spawn ${kind} ${resolveMapAlias(rest[1])} ${rest[2]} ${rest[3]}`;
      return `world spawn ${kind} ${rest.slice(1).join(' ')}`.trim();
    }
    case 'grant': {
      const assets = ['credits', 'uridium', 'xp', 'honor', 'experience', 'uri', 'creds'];
      if (rest.length === 2 && assets.includes(rest[0].toLowerCase())) {
        return `world grant admin ${normalizeCurrency(rest[0])} ${ parseHumanNumber(rest[1]) }`;
      }
      if (rest.length === 3 && assets.includes(rest[0].toLowerCase())) {
        return `world grant ${rest[2]} ${normalizeCurrency(rest[0])} ${ parseHumanNumber(rest[1]) }`;
      }
      if (rest.length === 3 && assets.includes(rest[1].toLowerCase())) {
        return `world grant ${rest[0]} ${normalizeCurrency(rest[1])} ${ parseHumanNumber(rest[2]) }`;
      }
      return `world grant ${rest.join(' ')}`.trim();
    }
    case 'announce':
      return `world announce ${rest.join(' ')}`.trim();
    case 'status':
    case 'players':
      return rest.length >= 1 ? `world status ${resolveMapAlias(rest[0])}` : 'world status';
    case 'speed':
      return rest.length >= 1
        ? `var set world.ship.pusat_plus_base_speed ${rest[0]}`
        : 'var get world.ship.pusat_plus_base_speed';
    case 'mesh':
      return rest.length === 0 ? 'mesh status' : `mesh ${rest.join(' ')}`;
    default:
      return withoutSlash;
  }
}

function parseHumanNumber(raw) {
  if (raw === undefined || raw === null) return raw;
  const s = String(raw).trim().replace(/,/g, '');
  const m = /^(-?\d+(?:\.\d+)?)\s*([kmb])?$/i.exec(s);
  if (!m) return s;
  let num = parseFloat(m[1]);
  const suffix = (m[2] || '').toLowerCase();
  if (suffix === 'k') num *= 1_000;
  else if (suffix === 'm') num *= 1_000_000;
  else if (suffix === 'b') num *= 1_000_000_000;
  return Number.isInteger(num) ? String(Math.round(num)) : String(num);
}

// â”€â”€â”€ Local AI Assistant (External Local LLM + Instant Semantic Resolver) â”€â”€â”€â”€
async function detectLocalAiProvider() {
  // 1. Check Ollama (http://127.0.0.1:11434)
  try {
    const res = await fetch('http://127.0.0.1:11434/api/tags', { signal: AbortSignal.timeout(600) });
    if (res.ok) {
      const data = await res.json();
      const model = data?.models?.[0]?.name;
      if (model) return { type: 'ollama', url: 'http://127.0.0.1:11434', model };
    }
  } catch {}

  // 2. Check LM Studio / local OpenAI-compatible server (http://127.0.0.1:1234)
  try {
    const res = await fetch('http://127.0.0.1:1234/v1/models', { signal: AbortSignal.timeout(600) });
    if (res.ok) {
      const data = await res.json();
      const model = data?.data?.[0]?.id;
      if (model) return { type: 'openai', url: 'http://127.0.0.1:1234', model };
    }
  } catch {}

  return { type: 'builtin', name: 'Built-in Semantic Admin AI (Instant Offline/Online)' };
}

function matchBestVariable(text, catalog) {
  const q = text.toLowerCase();

  // Direct key or alias mention
  for (const [alias, target] of Object.entries(VARIABLE_ALIASES)) {
    if (q.includes(alias)) return target;
  }
  for (const v of catalog) {
    if (q.includes(v.key.toLowerCase())) return v.key;
  }

  // Semantic keyword scoring
  const rules = [
    { key: 'world.ship.pusat_plus_base_speed', words: ['player', 'base', 'speed', 'pusat', 'ship speed', 'fly speed'] },
    { key: 'world.ship.phoenix_base_speed', words: ['phoenix', 'speed', 'starter ship'] },
    { key: 'world.movement.speed_multiplier', words: ['speed', 'multiplier', 'movement multiplier', 'boost speed'] },
    { key: 'world.combat.laser_damage_multiplier', words: ['laser', 'damage', 'multiplier', 'attack damage', 'dps'] },
    { key: 'world.combat.fire_range', words: ['fire', 'range', 'laser range', 'attack range', 'shoot range'] },
    { key: 'world.combat.shield_absorption_ratio', words: ['shield', 'absorption', 'ratio', 'absorb'] },
    { key: 'world.tick_rate_ms', words: ['tick', 'rate', 'tickrate', 'ms', 'simulation'] },
    { key: 'world.spawn.streuner_count', words: ['streuner', 'count', 'spawn count', 'streuners'] },
    { key: 'world.spawn.lordakia_count', words: ['lordakia', 'count', 'lordakias'] },
    { key: 'world.spawn.saimon_count', words: ['saimon', 'count', 'saimons'] },
    { key: 'world.spawn.bonus_box_count', words: ['bonus', 'box', 'count', 'boxes'] },
    { key: 'world.collectibles.pickup_radius', words: ['pickup', 'radius', 'collect radius', 'box radius'] },
    { key: 'world.collectibles.respawn_seconds', words: ['respawn', 'seconds', 'collectible respawn'] },
    { key: 'world.portal.jump_radius', words: ['portal', 'jump', 'radius', 'gate radius'] },
    { key: 'world.station.peace_radius', words: ['station', 'peace', 'safe', 'radius'] },
    { key: 'world.station.repair_hp_per_tick', words: ['station', 'repair', 'hp', 'regen'] },
    { key: 'world.radiation.damage_per_attempt', words: ['radiation', 'damage', 'border'] },
    { key: 'economy.shop_discount_percent', words: ['shop', 'discount', 'percent', 'sale'] },
    { key: 'economy.credits_reward_multiplier', words: ['credits', 'reward', 'multiplier'] },
    { key: 'economy.uridium_reward_multiplier', words: ['uridium', 'reward', 'multiplier'] },
    { key: 'economy.xp_reward_multiplier', words: ['xp', 'experience', 'reward', 'multiplier'] },
    { key: 'economy.honor_reward_multiplier', words: ['honor', 'reward', 'multiplier'] },
    { key: 'mesh.desired_replicas', words: ['mesh', 'replica', 'replicas', 'workers', 'nodes'] },
    { key: 'mesh.max_maps_per_node', words: ['max', 'maps', 'per', 'node'] },
    { key: 'mesh.heartbeat_ttl_seconds', words: ['heartbeat', 'ttl', 'lease'] },
  ];

  // Special common case: "player speed" or "base speed" or "speed"
  if (/\b(phoenix)\b/.test(q) && /\bspeed\b/.test(q)) return 'world.ship.phoenix_base_speed';
  if (/\b(multiplier)\b/.test(q) && /\bspeed\b/.test(q)) return 'world.movement.speed_multiplier';
  if (/\b(player|base|ship|pusat)\b/.test(q) && /\bspeed\b/.test(q)) return 'world.ship.pusat_plus_base_speed';

  let bestKey = null;
  let bestScore = 0;
  for (const r of rules) {
    let score = 0;
    for (const w of r.words) {
      if (q.includes(w)) score += w.includes(' ') ? 3 : 2;
    }
    if (score > bestScore) {
      bestScore = score;
      bestKey = r.key;
    }
  }
  if (bestScore >= 2) return bestKey;
  if (/\bspeed\b/.test(q)) return 'world.ship.pusat_plus_base_speed';
  return null;
}

export async function resolveNaturalLanguageCommand(query, catalog, historyFetcher) {
  const q = query.trim().replace(/^[?]\s*/, '').replace(/^\/ai\s+/i, '').trim();
  const lower = q.toLowerCase();

  // 1. Undo / Rollback intent ("undo the last speed change", "rollback change 12")
  if (/\b(undo|revert|rollback|roll back)\b/.test(lower)) {
    const idMatch = /\b(?:change|#)?\s*(\d+)\b/.exec(lower);
    if (idMatch) {
      const id = idMatch[1];
      return {
        slashCommand: `/rollbackvar ${id}`,
        wireCommand: `var rollback ${id}`,
        readOnly: false,
        explanation: `Roll back runtime variable change #${id}`,
      };
    }
    const varKey = matchBestVariable(lower, catalog);
    if (historyFetcher) {
      const entries = await historyFetcher(varKey);
      if (entries && entries.length > 0) {
        const latest = entries[0];
        return {
          slashCommand: `/rollbackvar ${latest.changeId}`,
          wireCommand: `var rollback ${latest.changeId}`,
          readOnly: false,
          explanation: `Undo latest change #${latest.changeId} on ${latest.variableKey} (${latest.newValue} -> ${latest.previousValue})`,
        };
      }
    }
    return {
      slashCommand: varKey ? `/varhistory ${varKey}` : '/varhistory',
      wireCommand: varKey ? `var history ${varKey}` : 'var history',
      readOnly: true,
      explanation: varKey
        ? `No active rollback entry cached; inspecting change history for ${varKey}`
        : 'Inspecting recent runtime variable change history',
    };
  }

  // 2. Grant / Give currency intent ("give admin 500k uridium", "grant 100000 credits to owner")
  if (/\b(give|grant|add|credit|reward)\b/.test(lower) && /\b(uridium|uri|credits|creds|xp|experience|honor)\b/.test(lower)) {
    const currencyMatch = /\b(uridium|uri|credits|creds|xp|experience|honor)\b/.exec(lower);
    const currency = normalizeCurrency(currencyMatch[1]);
    const amountMatch = /\b(\d+(?:,\d{3})*(?:\.\d+)?\s*[kmb]?)\b/i.exec(lower);
    const amount = amountMatch ? parseHumanNumber(amountMatch[1]) : '10000';
    let target = 'admin';
    const words = q.split(/\s+/);
    for (let i = 0; i < words.length; i++) {
      const w = words[i].replace(/[^a-zA-Z0-9_-]/g, '');
      const wl = w.toLowerCase();
      if (['give', 'grant', 'add', 'to', 'for', 'pilot', 'user', 'player', 'uridium', 'uri', 'credits', 'creds', 'xp', 'experience', 'honor'].includes(wl)) continue;
      if (/^\d+(\.\d+)?[kmb]?$/i.test(w.replace(/,/g, ''))) continue;
      if (w.length >= 2) {
        target = w;
        break;
      }
    }
    return {
      slashCommand: `/grant ${currency} ${amount} ${target}`,
      wireCommand: `world grant ${target} ${currency} ${amount}`,
      readOnly: false,
      explanation: `Grant ${Number(amount).toLocaleString('en-US')} ${currency} to pilot '${target}'`,
    };
  }

  // 3. Spawn intent ("spawn 5 kristallons", "spawn streuner on 1-2", "spawn a bonus box")
  if (/\b(spawn|summon|create)\b/.test(lower)) {
    let kind = 'streuner';
    for (const k of SPAWN_KINDS) {
      if (lower.includes(k)) {
        kind = k;
        break;
      }
    }
    const normalizedKind = normalizeSpawnKind(kind);
    const mapMatch = /\b([1-4]-[1-4]|map\s*\d+)\b/i.exec(lower);
    const mapToken = mapMatch ? mapMatch[1].replace(/^map\s*/i, '') : '1-1';
    const mapId = resolveMapAlias(mapToken);
    const coordMatch = /\b(\d{3,5})\s*[,\s]\s*(\d{3,5})\b/.exec(lower);
    const x = coordMatch ? coordMatch[1] : '2000';
    const y = coordMatch ? coordMatch[2] : '1300';
    return {
      slashCommand: `/spawn ${normalizedKind} ${mapToken} ${x} ${y}`,
      wireCommand: `world spawn ${normalizedKind} ${mapId} ${x} ${y}`,
      readOnly: false,
      explanation: `Spawn ${normalizedKind}${normalizedKind !== kind ? ` (mapped from ${kind})` : ''} on map ${mapToken} (id ${mapId}) at (${x}, ${y})`,
    };
  }

  // 4. Teleport intent ("teleport admin to 2-1 3000 1500", "tp to 1-3")
  if (/\b(teleport|tp|warp|move pilot)\b/.test(lower)) {
    const mapMatch = /\b([1-4]-[1-4])\b/.exec(lower);
    const mapToken = mapMatch ? mapMatch[1] : '1-1';
    const mapId = resolveMapAlias(mapToken);
    const coords = [...lower.matchAll(/\b(\d{3,5})\b/g)].map(m => m[1]);
    const x = coords[0] || '2000';
    const y = coords[1] || '1300';
    return {
      slashCommand: `/tp admin ${mapToken} ${x} ${y}`,
      wireCommand: `world tp admin ${mapId} ${x} ${y}`,
      readOnly: false,
      explanation: `Teleport pilot 'admin' to map ${mapToken} (id ${mapId}) at (${x}, ${y})`,
    };
  }

  // 5. Heal / Repair intent ("heal admin", "repair my ship")
  if (/\b(heal|repair|restore hp|full hp)\b/.test(lower) && !/\bradius|tick\b/.test(lower)) {
    return {
      slashCommand: '/heal admin',
      wireCommand: 'world heal admin',
      readOnly: false,
      explanation: 'Heal pilot ship to full HP and shield',
    };
  }

  // 6. Announce / Broadcast intent ("announce Server restart in 5m")
  const announceMatch = /^(?:announce|broadcast|say)\s+(.+)$/i.exec(q);
  if (announceMatch) {
    const msg = announceMatch[1].replace(/^['"]|['"]$/g, '');
    return {
      slashCommand: `/announce "${msg}"`,
      wireCommand: `world announce "${msg}"`,
      readOnly: false,
      explanation: `Broadcast server announcement: "${msg}"`,
    };
  }

  // 7. Mesh / World status intent ("show mesh status", "who is online", "world status")
  if (/\b(mesh|cluster|nodes|replicas)\b/.test(lower) && !/\b(set|change|to\s+\d)\b/.test(lower)) {
    return {
      slashCommand: '/mesh status',
      wireCommand: 'mesh status',
      readOnly: true,
      explanation: 'Inspect live server mesh nodes and map leases',
    };
  }
  if (/\b(who is online|active players|online pilots|world status|server status)\b/.test(lower)) {
    return {
      slashCommand: '/status',
      wireCommand: 'world status',
      readOnly: true,
      explanation: 'Show active pilots and world map status',
    };
  }

  // 8. Reset variable intent ("reset player base speed to default")
  if (/\b(reset|default)\b/.test(lower)) {
    const varKey = matchBestVariable(lower, catalog) || 'world.ship.pusat_plus_base_speed';
    return {
      slashCommand: `/resetvar ${varKey}`,
      wireCommand: `var reset ${varKey}`,
      readOnly: false,
      explanation: `Reset ${varKey} to its catalog default value`,
    };
  }

  // 9. History intent ("show speed history", "history of base speed")
  if (/\b(history|audit|changes|log)\b/.test(lower)) {
    const varKey = matchBestVariable(lower, catalog);
    return {
      slashCommand: varKey ? `/varhistory ${varKey}` : '/varhistory',
      wireCommand: varKey ? `var history ${varKey}` : 'var history',
      readOnly: true,
      explanation: varKey ? `Show change history for ${varKey}` : 'Show runtime variable change history',
    };
  }

  // 10. Set vs Get runtime variable intent ("set player base speed to 480", "what is the current player speed?")
  const varKey = matchBestVariable(lower, catalog);
  const numberTokens = [...lower.matchAll(/(?:to|=|is|at|\s)(-?\d+(?:\.\d+)?[kmb]?)\b/g)].map(m => m[1]);
  const isQuestion = /^(what|how|show|get|check|read|current|display|list)\b/.test(lower) || lower.endsWith('?');
  const isSetVerb = /\b(set|change|update|make|increase|decrease|tune|adjust)\b/.test(lower);

  if (varKey && (isSetVerb || (!isQuestion && numberTokens.length > 0))) {
    const rawVal = numberTokens[numberTokens.length - 1] || '370';
    const val = parseHumanNumber(rawVal);
    const displayAlias = lower.includes('player') && varKey === 'world.ship.pusat_plus_base_speed'
      ? 'world.player.base_speed'
      : varKey;
    return {
      slashCommand: `/setvar ${displayAlias} ${val}`,
      wireCommand: `var set ${varKey} ${val}`,
      readOnly: false,
      explanation: `Set ${varKey}${displayAlias !== varKey ? ` (alias ${displayAlias})` : ''} = ${val}`,
    };
  }

  if (varKey) {
    const displayAlias = lower.includes('player') && varKey === 'world.ship.pusat_plus_base_speed'
      ? 'world.player.base_speed'
      : varKey;
    return {
      slashCommand: `/getvar ${displayAlias}`,
      wireCommand: `var get ${varKey}`,
      readOnly: true,
      explanation: `Read current value of ${varKey}${displayAlias !== varKey ? ` (alias ${displayAlias})` : ''}`,
    };
  }

  return {
    slashCommand: '/vars',
    wireCommand: 'var list',
    readOnly: true,
    explanation: 'List all runtime variables and their current values',
  };
}

// â”€â”€â”€ Server API Client â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
class AdminApiClient {
  constructor(baseUri, username, password) {
    this.baseUri = baseUri;
    this.username = username;
    this.password = password;
    this.token = process.env.THDARKORBIT_ADMIN_TOKEN || '';
    this.variables = [...BUILTIN_VARIABLES];
    this.online = false;
  }

  async ensureToken() {
    if (this.token) return this.token;
    const candidates = [
      { u: this.username, p: this.password },
      { u: 'owner', p: 'owner' },
      { u: 'admin', p: 'Admin123!' },
    ];
    for (const c of candidates) {
      try {
        const res = await fetch(`${this.baseUri}/api/v1/identity/login`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ usernameOrEmail: c.u, password: c.p }),
          signal: AbortSignal.timeout(3500),
        });
        if (res.ok) {
          const data = await res.json();
          if (data?.accessToken) {
            this.token = data.accessToken;
            this.online = true;
            return this.token;
          }
        }
      } catch {}
    }
    // Fallback to local Keycloak
    for (const c of candidates) {
      try {
        const body = new URLSearchParams({
          grant_type: 'password',
          client_id: 'thdarkorbit-cms',
          username: c.u,
          password: c.p,
        });
        const res = await fetch('http://localhost:8081/realms/thdarkorbit/protocol/openid-connect/token', {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body,
          signal: AbortSignal.timeout(3500),
        });
        if (res.ok) {
          const data = await res.json();
          if (data?.access_token) {
            this.token = data.access_token;
            return this.token;
          }
        }
      } catch {}
    }
    return '';
  }

  async refreshVariables() {
    await this.ensureToken();
    if (!this.token) return this.variables;
    try {
      const res = await fetch(`${this.baseUri}/api/v1/backoffice/runtime-vars`, {
        headers: { Authorization: `Bearer ${this.token}` },
        signal: AbortSignal.timeout(3500),
      });
      if (res.ok) {
        const live = await res.json();
        if (Array.isArray(live) && live.length > 0) {
          this.online = true;
          const byKey = new Map(BUILTIN_VARIABLES.map(v => [v.key, v]));
          this.variables = live.map(item => {
            const meta = byKey.get(item.key) || {};
            return {
              key: item.key,
              category: item.category || meta.category || 'Dynamic',
              valueType: item.valueType || meta.valueType || 'string',
              defaultValue: item.defaultValue ?? meta.defaultValue ?? '',
              currentValue: item.currentValue ?? meta.currentValue ?? '',
              range: meta.range || '',
              desc: meta.desc || `${item.category} runtime variable`,
            };
          });
        }
      }
    } catch {}
    return this.variables;
  }

  async fetchHistory(key = null, limit = 10) {
    await this.ensureToken();
    if (!this.token) return [];
    try {
      const q = new URLSearchParams({ limit: String(limit) });
      if (key) q.set('key', resolveVariableAlias(key));
      const res = await fetch(`${this.baseUri}/api/v1/backoffice/runtime-vars/history?${q}`, {
        headers: { Authorization: `Bearer ${this.token}` },
        signal: AbortSignal.timeout(3500),
      });
      if (res.ok) return await res.json();
    } catch {}
    return [];
  }

  async executeCommand(rawOrWireCommand) {
    const wire = normalizeAdminCommand(rawOrWireCommand);
    await this.ensureToken();
    if (!this.token) {
      return {
        ok: false,
        status: 0,
        wireCommand: wire,
        error: `Server at ${this.baseUri} is unreachable or not running (start AppHost in Visual Studio with F5, or set THDARKORBIT_ADMIN_TOKEN).`,
      };
    }
    try {
      const res = await fetch(`${this.baseUri}/api/v1/backoffice/admin/command`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${this.token}`,
        },
        body: JSON.stringify({ text: wire, surface: 'ServerManagement' }),
        signal: AbortSignal.timeout(8000),
      });
      const text = await res.text();
      let json = null;
      try { json = JSON.parse(text); } catch {}
      if (!res.ok) {
        return {
          ok: false,
          status: res.status,
          wireCommand: wire,
          error: json?.error || text || `HTTP ${res.status}`,
        };
      }
      this.online = true;
      await this.refreshVariables();
      return { ok: true, status: res.status, wireCommand: wire, data: json ?? text };
    } catch (err) {
      return {
        ok: false,
        status: 0,
        wireCommand: wire,
        error: err?.message || String(err),
      };
    }
  }
}

// â”€â”€â”€ Tab Autocompletion Engine â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
export function getCompletions(line, variables = BUILTIN_VARIABLES) {
  const trimmedLeft = line.replace(/^\s+/, '');

  // Complete slash commands at root
  if (!trimmedLeft.includes(' ')) {
    const prefix = trimmedLeft.startsWith('/') ? trimmedLeft.toLowerCase() : '/' + trimmedLeft.toLowerCase();
    const hits = SLASH_COMMANDS
      .filter(s => s.cmd.startsWith(prefix))
      .map(s => s.cmd + ' ');
    return { hits: hits.length ? hits : SLASH_COMMANDS.map(s => s.cmd + ' '), token: trimmedLeft, contextInfo: hits.length === 1 ? SLASH_COMMANDS.find(s => s.cmd + ' ' === hits[0]) : null };
  }

  const parts = trimmedLeft.split(/\s+/);
  const cmd = parts[0].toLowerCase();
  const argIndex = parts.length - 1;
  const currentToken = parts[argIndex].toLowerCase();
  const prefixBeforeToken = line.slice(0, line.length - parts[argIndex].length);

  // Variable key completion for /getvar, /setvar, /resetvar, /varhistory
  if (['/getvar', '/setvar', '/resetvar', '/varhistory'].includes(cmd) && argIndex === 1) {
    const allKeys = [
      'world.player.base_speed',
      ...variables.map(v => v.key),
      ...Object.keys(VARIABLE_ALIASES),
    ];
    const uniqueKeys = [...new Set(allKeys)];
    const matching = uniqueKeys.filter(k => k.toLowerCase().startsWith(currentToken) || k.toLowerCase().includes(currentToken));
    const hits = matching.map(k => prefixBeforeToken + k + (cmd === '/setvar' ? ' ' : ''));
    const resolved = resolveVariableAlias(matching[0] || currentToken);
    const meta = variables.find(v => v.key === resolved);
    return { hits, token: line, variableMeta: meta, resolvedKey: resolved };
  }

  // Value suggestion for /setvar <key> <value>
  if (cmd === '/setvar' && argIndex === 2) {
    const resolved = resolveVariableAlias(parts[1]);
    const meta = variables.find(v => v.key === resolved);
    if (meta) {
      const candidates = [...new Set([meta.currentValue, meta.defaultValue])].filter(Boolean);
      return {
        hits: candidates.map(v => prefixBeforeToken + v),
        token: line,
        variableMeta: meta,
        resolvedKey: resolved,
      };
    }
  }

  // Currency completion for /grant <currency>
  if (cmd === '/grant' && argIndex === 1) {
    const matching = CURRENCIES.filter(c => c.startsWith(currentToken));
    return { hits: matching.map(c => prefixBeforeToken + c + ' '), token: line };
  }

  // NPC / Spawn kind completion for /spawn <kind> [map]
  if (cmd === '/spawn' && argIndex === 1) {
    const matching = SPAWN_KINDS.filter(k => k.startsWith(currentToken));
    return { hits: matching.map(k => prefixBeforeToken + k + ' '), token: line };
  }
  if (cmd === '/spawn' && argIndex === 2) {
    const matching = MAP_TOKENS.filter(m => m.startsWith(currentToken));
    return { hits: matching.map(m => prefixBeforeToken + m + ' '), token: line };
  }

  // Map completion for /tp and /status
  if ((cmd === '/tp' || cmd === '/status') && (argIndex === 1 || argIndex === 2)) {
    const matching = MAP_TOKENS.filter(m => m.startsWith(currentToken));
    return { hits: matching.map(m => prefixBeforeToken + m + ' '), token: line };
  }

  return { hits: [], token: line };
}

// â”€â”€â”€ Formatting Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function printBanner(client, aiProvider) {
  console.log(`\n${C.brightCyan}${C.bold}â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—${C.reset}`);
  console.log(`${C.brightCyan}${C.bold}â•‘   ThDarkOrbitEmulator â€” Interactive Server Admin CLI & Local AI Assistant    â•‘${C.reset}`);
  console.log(`${C.brightCyan}${C.bold}â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•${C.reset}`);
  const statusBadge = client.online
    ? `${C.brightGreen}â— ONLINE${C.reset} (${client.baseUri})`
    : `${C.yellow}â—‹ OFFLINE / CATALOG MODE${C.reset} (${client.baseUri})`;
  const aiBadge = aiProvider.type === 'builtin'
    ? `${C.magenta}${aiProvider.name}${C.reset}`
    : `${C.brightGreen}${aiProvider.type.toUpperCase()} (${aiProvider.model} @ ${aiProvider.url})${C.reset}`;
  console.log(`  ${C.bold}Gateway:${C.reset}  ${statusBadge}`);
  console.log(`  ${C.bold}Local AI:${C.reset} ${aiBadge}`);
  console.log(`  ${C.bold}Tips:${C.reset}     Press ${C.cyan}[Tab]${C.reset} to autocomplete commands, live runtime vars, NPCs, currencies & maps.`);
  console.log(`            Type ${C.cyan}/help${C.reset} for commands, or write ${C.green}plain English${C.reset} (e.g. ${C.dim}"set player base speed to 480"${C.reset}).\n`);
}

function printHelp(variables) {
  console.log(`\n${C.brightCyan}${C.bold}Slash Commands:${C.reset}`);
  for (const s of SLASH_COMMANDS) {
    console.log(`  ${C.cyan}${s.usage.padEnd(36)}${C.reset} ${s.desc}`);
  }
  console.log(`\n${C.brightCyan}${C.bold}Natural-Language AI Examples (type directly or prefix with ? or /ai):${C.reset}`);
  console.log(`  ${C.green}set player base speed to 480${C.reset}       ${C.dim}â†’ /setvar world.player.base_speed 480${C.reset}`);
  console.log(`  ${C.green}what is the current player speed?${C.reset}  ${C.dim}â†’ /getvar world.player.base_speed${C.reset}`);
  console.log(`  ${C.green}spawn 5 kristallons${C.reset}                ${C.dim}â†’ /spawn saimon 1-1 2000 1300${C.reset}`);
  console.log(`  ${C.green}give admin 500k uridium${C.reset}            ${C.dim}â†’ /grant uridium 500000 admin${C.reset}`);
  console.log(`  ${C.green}undo the last speed change${C.reset}         ${C.dim}â†’ /rollbackvar <latestChangeId>${C.reset}`);
  console.log(`\n${C.brightCyan}${C.bold}Live Runtime Variables (${variables.length}):${C.reset}`);
  for (const v of variables) {
    console.log(`  ${C.yellow}${v.key.padEnd(36)}${C.reset} = ${C.bold}${String(v.currentValue).padEnd(8)}${C.reset} ${C.dim}(default: ${v.defaultValue}, type: ${v.valueType}${v.range ? ', range: ' + v.range : ''}) â€” ${v.desc}${C.reset}`);
  }
  console.log('');
}

function printCommandResult(res) {
  if (!res.ok) {
    console.log(`${C.red}âœ– Error:${C.reset} ${res.error}`);
    console.log(`${C.dim}  Wire command: ${res.wireCommand}${C.reset}`);
    return;
  }
  const d = res.data;
  console.log(`${C.brightGreen}âœ” OK${C.reset} ${C.dim}[${res.wireCommand}]${C.reset}`);
  if (d && typeof d === 'object') {
    if (d.message) console.log(`  ${C.bold}${d.message}${C.reset}`);
    if (d.variable) {
      const v = d.variable;
      console.log(`  ${C.yellow}${v.key}${C.reset} = ${C.brightGreen}${C.bold}${v.currentValue}${C.reset} ${C.dim}(default: ${v.defaultValue}, type: ${v.valueType}, category: ${v.category})${C.reset}`);
    }
    if (d.change) {
      const c = d.change;
      console.log(`  ${C.cyan}Change #${c.changeId}:${C.reset} ${c.variableKey} ${C.dim}${c.previousValue}${C.reset} â†’ ${C.brightGreen}${C.bold}${c.newValue}${C.reset} ${C.dim}(by ${c.changedBy} on ${c.nodeId})${C.reset}`);
    }
    if (Array.isArray(d.variables)) {
      for (const v of d.variables) {
        console.log(`  ${C.yellow}${v.key.padEnd(36)}${C.reset} = ${C.bold}${String(v.currentValue).padEnd(8)}${C.reset} ${C.dim}(default: ${v.defaultValue}, ${v.valueType})${C.reset}`);
      }
    }
    if (Array.isArray(d.history)) {
      for (const h of d.history.slice(0, 15)) {
        console.log(`  ${C.cyan}#${String(h.changeId).padEnd(5)}${C.reset} ${C.yellow}${h.variableKey.padEnd(34)}${C.reset} ${h.previousValue} â†’ ${C.bold}${h.newValue}${C.reset} ${C.dim}(${h.changedAtUtc})${C.reset}`);
      }
    }
    if (!d.message && !d.variable && !d.change && !d.variables && !d.history) {
      console.log(JSON.stringify(d, null, 2));
    }
  } else {
    console.log(d);
  }
}

// â”€â”€â”€ Main Interactive Loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
async function main() {
  const opts = parseArgs(process.argv);
  const client = new AdminApiClient(opts.baseUri, opts.username, opts.password);
  await client.refreshVariables();
  const aiProvider = await detectLocalAiProvider();

  // Single-shot --command mode
  if (opts.commandText) {
    const res = await client.executeCommand(opts.commandText);
    printCommandResult(res);
    process.exit(res.ok ? 0 : 1);
  }

  // Single-shot --ai mode
  if (opts.aiQuery) {
    const resolved = await resolveNaturalLanguageCommand(
      opts.aiQuery,
      client.variables,
      key => client.fetchHistory(key, 5)
    );
    console.log(`${C.magenta}âœ¦ Local AI Translation:${C.reset} ${C.bold}${resolved.slashCommand}${C.reset} ${C.dim}â†’ [${resolved.wireCommand}]${C.reset}`);
    console.log(`${C.dim}  Intent: ${resolved.explanation}${C.reset}`);
    if (opts.autoExecute || resolved.readOnly) {
      const res = await client.executeCommand(resolved.wireCommand);
      printCommandResult(res);
    }
    process.exit(0);
  }

  printBanner(client, aiProvider);

  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
    prompt: `${C.brightCyan}thdo-admin>${C.reset} `,
    completer: (line) => {
      const result = getCompletions(line, client.variables);
      if (result.variableMeta) {
        const m = result.variableMeta;
        process.stdout.write(
          `\n${C.dim}  â„¹ ${m.key}: current=${C.reset}${C.bold}${m.currentValue}${C.reset}${C.dim}, default=${m.defaultValue}, type=${m.valueType}${m.range ? ', range=' + m.range : ''} â€” ${m.desc}${C.reset}\n`
        );
        rl.prompt(true);
      }
      return [result.hits, result.token];
    },
  });

  let pendingConfirmation = null;

  rl.prompt();

  rl.on('line', async (rawLine) => {
    const line = rawLine.trim();

    if (pendingConfirmation) {
      const pending = pendingConfirmation;
      pendingConfirmation = null;
      rl.setPrompt(`${C.brightCyan}thdo-admin>${C.reset} `);
      if (line === '' || /^y(es)?$/i.test(line)) {
        const res = await client.executeCommand(pending.wireCommand);
        printCommandResult(res);
      } else {
        console.log(`${C.yellow}Cancelled.${C.reset}`);
      }
      rl.prompt();
      return;
    }

    if (!line) {
      rl.prompt();
      return;
    }

    if (line === '/exit' || line === '/quit' || line === 'exit' || line === 'quit') {
      console.log(`${C.dim}Goodbye.${C.reset}`);
      rl.close();
      process.exit(0);
    }

    if (line === '/help' || line === 'help' || line === '/?') {
      await client.refreshVariables();
      printHelp(client.variables);
      rl.prompt();
      return;
    }

    // Check if input is a direct slash command or structured wire command
    const isDirectCommand =
      (line.startsWith('/') && !line.toLowerCase().startsWith('/ai ')) ||
      /^(var|world|mesh)\s+/i.test(line);

    if (isDirectCommand) {
      const res = await client.executeCommand(line);
      printCommandResult(res);
      rl.prompt();
      return;
    }

    // Natural-language AI translation
    const resolved = await resolveNaturalLanguageCommand(
      line,
      client.variables,
      key => client.fetchHistory(key, 5)
    );
    console.log(`  ${C.magenta}âœ¦ AI â†’${C.reset} ${C.bold}${C.cyan}${resolved.slashCommand}${C.reset} ${C.dim}(${resolved.wireCommand})${C.reset}`);
    console.log(`  ${C.dim}${resolved.explanation}${C.reset}`);

    if (resolved.readOnly || opts.autoExecute) {
      const res = await client.executeCommand(resolved.wireCommand);
      printCommandResult(res);
      rl.prompt();
    } else {
      pendingConfirmation = resolved;
      rl.setPrompt(`${C.yellow}  Execute? [Enter/y = run, n = cancel]>${C.reset} `);
      rl.prompt();
    }
  });

  rl.on('close', () => {
    process.exit(0);
  });
}

if (import.meta.url === `file://${process.argv[1]?.replace(/\\/g, '/')}` || process.argv[1]?.endsWith('admin-cli.mjs')) {
  main();
}