// Shapes of the staff API (server/Aion.Host/Authentication/GameplayEndpoints.cs) and the wardrobe export.

/** GET /api/admin/me. permissions: what this rank may do (Aion.Core/Security/StaffAccess.cs); the server checks every request. */
export interface StaffProfile { roles: string[]; buildId: string | null; rank?: string | null; rankLabel?: string; permissions?: string[] }

/** Whether the signed-in staff member holds a permission (camelCase names of StaffPermission). */
export function can(profile: StaffProfile, permission: string): boolean { return profile.permissions?.includes(permission) ?? false; }

/** The stored staff roles, lowest rank first. */
export const STAFF_ROLES = ['gamemaster', 'moderator', 'admin'] as const;

export interface AdminCharacter {
  characterId: string; name: string; accountId: string; accountName: string; worldId: number;
  level: number; race: number; gender: number; creationId: number; accountDisabled: boolean; accountRoles?: string[] | null; staff?: boolean;
  /** On the graveyard account (deleted); pendingDeleteAt: a deletion the owner asked for; blocked: deactivated by staff. */
  deleted?: boolean; pendingDeleteAt?: string | null; blocked?: boolean; blockReason?: string | null;
}

export interface InventoryItem { instanceId?: string; templateId: number; quantity: number; location: string; slot: number | null }
export interface LearnedSkill { skillId: number; level: number; slot: number | null }
export interface CharacterDetail {
  character: AdminCharacter;
  inventory: { items: InventoryItem[]; skills: LearnedSkill[] };
  appearance: unknown;
  className: string | null;
  classes: { creationId: number; name: string }[] | null;
  ascension: number;
}

export interface CatalogItem { templateId: number; name: string; category: string | null; grade: string | null; level: number | null }
export interface CatalogPage { items: CatalogItem[]; total: number; categories: string[] }

/** One item of the wardrobe export (/wardrobe/data; development hosts only). */
export interface WardrobeItem { id: number; name: string; category: string; grade: string; icon?: string; slot?: string; level?: number; parts: Record<string, unknown[]> }
export interface WardrobeData { items: WardrobeItem[]; skills: Record<string, { name?: string; icon?: string }> }

/** The wardrobe-viewer.js API (loaded at runtime from the host). */
export interface Viewer {
  readonly equipped: Record<string, WardrobeItem>;
  setLook(gender: string, items: WardrobeItem[], appearance?: unknown): Promise<void>;
  equip(item: WardrobeItem): boolean;
  /** Takes every equipped item off (the base body stays, with the current gender and appearance). */
  clear(): void;
  setPoseMode(mode: 'still' | 'bind'): void;
}
export interface ViewerModule {
  FILES: string;
  SLOT_LABEL: Record<string, string>;
  wearable(item: WardrobeItem): boolean;
  createViewer(stage: HTMLElement, data: WardrobeData, hooks: { onStatus?: (t: string) => void; onError?: (t: string) => void; onChange?: () => void }): Viewer;
}

export const ROLE_LABEL: Record<string, string> = { admin: 'Administrator', moderator: 'Moderator', gamemaster: 'Gamemaster' };
export const GENDER: Record<number, 'GM' | 'GF'> = { 1: 'GM', 2: 'GF' };   // EGender::Male = 1, Female = 2

export const ERRORS: Record<string, string> = {
  forbidden: 'Your staff rank does not allow this.',
  unknown_item: 'This item is not in the server catalogue build.',
  character_unavailable: 'That character is unavailable (deleted or its account is disabled).',
  invalid_grant: 'Quantity must be between 1 and 1,000,000.',
  request_conflict: 'This request ID was already used with different details. Discard it and grant again.',
  invalid_query: 'Searches are limited to 64 characters.',
  invalid_level: 'Level must be between 1 and 100.',
  invalid_ascension: 'Ascension grade must be between 1 and 6.',
  unknown_class: "That class is not available for this character's race and gender.",
  no_catalogue: 'No catalogue build is imported on the server.',
  character_online: 'The character is in the world. Ask the player to log out (or kick them) and try again.',
  character_deleted: 'That character is already deleted.',
  invalid_action: 'That character action is not known.',
  invalid_reason: 'The reason is limited to 200 characters.',
  not_pending: 'The character has no pending deletion.',
  already_blocked: 'The character is already deactivated.',
  not_blocked: 'The character is not deactivated.',
  unknown_account: 'That account was not found or is disabled.',
  same_account: 'The character is already on that account.',
  other_race: 'That account plays the other race on this world.',
  no_free_slot: 'That account has no free character slot on this world.',
  same_world: 'The character is already on that world.',
  invalid_move: 'The move is missing a character, world or account.',
  invalid_name: 'Names are 1 to 64 characters, without # or control characters.',
  reserved_name: 'GM-style names are for staff accounts.',
  name_unavailable: 'That name is taken on the target world. Give the character a new name.',
  character_blocked: 'The character is deactivated. Reactivate it before moving it.',
  move_in_progress: 'Another move of this character has not finished.',
  market_pending: 'The character has market listings or unclaimed sales. They must end first.',
  move_finished: 'This move has already completed.',
  move_cancelled: 'This move was cancelled. Start it again.',
  move_incomplete: 'The move did not finish: a world stopped answering. The character stays blocked; press Move again to finish it.',
  world_unreachable: 'A world server did not answer. Nothing was moved.',
  world_offline: 'That world is not running.',
  schema_mismatch: 'The two worlds are on different database versions. Update both first.',
  unsupported_schema: 'This world has a character table the move cannot copy safely.',
  no_worlds: 'This server runs no separate worlds.',
};

/** GET /api/admin/live: players in the world now (in-memory on the host). */
export interface LivePlayer {
  name: string; characterId: string | null; accountName: string | null; staffRole: string | null;
  level: number; className: string | null; mapId: number | null; area: string | null;
  position: [number, number, number] | null;
  hp: number; hpMax: number; mp: number; mpMax: number; invisible: boolean;
  partyLeader: string | null; partySize: number; inWorldSince: string;
  heading?: number; dead?: boolean; speedRejects?: number;
}
export interface LiveOverview { serverTime: string; startedAt: string; connections: number; players: LivePlayer[] }

/** GET /api/admin/maps/{id}/outline: subzone polygons, [x, y] in cm. */
export interface MapOutline { mapId: number; areas: { name: string; priority: number; points: [number, number][] }[] }

/** GET /api/admin/maps: the game's world-map images (tools/MapImageExport) by server map id. */
export interface MapImage {
  name: string; file: string; width: number; height: number; baseMapId?: number;
  /** px = a*x + b*y + c, py = d*x + e*y + f (image right = world +X, down = +Y). */
  world: { affine: [number, number, number, number, number, number]; minX: number; maxX: number; minY: number; maxY: number };
}
export type MapImages = Record<string, MapImage>;

/** GET /api/admin/server. */
export interface ServerHealth {
  players: number; connections: number; startedAt: string; cores: number; cpu: number; memoryMb: number; heapMb: number; threads: number;
  worldBusy: number; mapBusy: number; worldWaitMs: number; mapWaitMs: number; windowSeconds: number; restartAt: string | null;
}

/** GET /api/admin/audit. */
export interface AuditEntry { id: number; at: string; action: string; characterId: string | null; characterName: string | null; actorName: string | null; details: string }

ERRORS.invalid_timer = 'Give the shortest and longest wait in hours (the longest at least the shortest, at most 7 days).';
ERRORS.invalid_bug_report = 'Pick a status; the note can have up to 2000 characters.';
ERRORS.realm_only = 'Bug reports are kept by the auth host.';
ERRORS.world_only = 'Pick a world: this is answered by a world server.';
ERRORS.not_online = 'That player is no longer in the world.';
ERRORS.not_possible = 'The game could not do that right now.';
ERRORS.invalid_announcement = 'Write a message of up to 200 characters.';
ERRORS.invalid_reason = 'The reason is limited to 120 characters.';
ERRORS.invalid_destination = 'Give a map ID (and optionally X, Y and Z).';
ERRORS.invalid_restart = 'Choose between 0 and 120 minutes.';

/** GET /api/admin/maps/{id}/monsters. */
/** A field boss (GET /api/admin/field-bosses). Times are Unix ms; status "due": its time has come, it appears when a player is on the map. */
export interface FieldBoss {
  code: number; mapId: number; npcId: number; name: string; level: number; kind: 'named' | 'boss'; status: 'alive' | 'dead' | 'due'; mapLoaded: boolean;
  killedAt: number | null; respawnAt: number | null; minMinutes: number; maxMinutes: number; defaultMinMinutes: number; defaultMaxMinutes: number; customTimer: boolean;
  /** The official schedule (server local time, e.g. "Sun, Fri 21:00"), used instead of the default range; null for most bosses. */
  schedule: string | null;
}
export interface FieldBossList { loaded: boolean; now: number; bosses: FieldBoss[] }
export interface MonsterView { key: number; npcId: number; name: string; level: number; x: number; y: number; dead: boolean; hostile: boolean; staff: boolean; engaged: boolean }
export interface NpcChoice { npcId: number; name: string; level: number; hostile: boolean }

export interface Sanction { id: number; accountId: string; accountName: string; kind: 'ban' | 'mute'; reason: string; createdAt: string; expiresAt: string | null; createdBy: string | null; revokedAt: string | null; revokedBy: string | null }
export interface ChatLine { id: number; at: string; channel: string; sender: string; receiver: string | null; content: string; accountId: string | null }
export interface Ticket { id: number; createdAt: string; accountId: string | null; accountName: string | null; characterName: string | null; category: string; content: string; status: 'open' | 'closed'; staffNote: string; handledBy: string | null; handledAt: string | null }
export type BugStatus = 'open' | 'in_progress' | 'fixed' | 'wont_fix';
/** A "/bug" report as listed (migration 106). */
export interface BugReportSummary {
  id: number; createdAt: string; accountId: string | null; accountName: string | null; characterId: string | null; characterName: string | null;
  worldId: number; mapId: number | null; mapName: string | null; quests: string[]; text: string; status: BugStatus; staffNote: string;
  handledBy: string | null; handledAt: string | null; commit: string | null; screenshotType: string | null;
}
export interface BugSource { kind: string; file?: string; map?: number; index?: number; uniqueKey?: number; home?: number[]; homeYaw?: number; ownerKey?: number }
export interface BugSpawn { key: number; npcId: number; name: string; level: number; x: number; y: number; z: number; heading: number; distance: number; dead: boolean; hostile: boolean; relationship: number; function: string; seen: boolean; source: BugSource }
export interface BugObject { key: number; env: number; name: string; envName?: string; x: number; y: number; z: number; yaw: number; distance: number; shown: boolean; file: string; map: number; index: number; uniqueKey: number }
export interface BugQuest { code: string; id: number; title?: string; type?: string; status: number; statusName: string; step: number; goals: number[]; questMap?: number; startNpc?: number; finishNpc?: number; stepNpc?: number; stepObjectives: { type: string; npc?: number; npcs?: number[]; item?: number; target?: string; count: number }[] }
export interface BugPacket { agoMs: number; dir: 'C' | 'S'; op: string; name?: string; length: number; hex?: string }
/** What the world server captured at the moment of the report (GameSession.BugReport.cs). */
export interface BugContext {
  reportedAt: string; serverMs: number;
  server: { commit?: string; worldId: number; sharedWorld: boolean };
  character: { accountId?: string; id?: string; name?: string; race: number; raceName: string; className?: string; level: number; staffRole?: string; hp: number; hpMax: number; dead: boolean; inCombat: boolean };
  location?: { mapId: number; mapName: string; instance?: number; x: number; y: number; z: number; heading: number; subzoneId: number; subzone?: string; area?: string };
  quests: { active: BugQuest[]; recentEvents: { agoMs: number; kind: string; code: string; step: number; status: number; goals?: number[] }[]; lastRewarded: BugQuest[] };
  mapEvents: { id: number; name: string; status: number; stage: number; step: number; goals: number[]; ageMs: number }[];
  nearbyRadius: number; nearby: BugSpawn[]; nearbyObjects: BugObject[];
  packets: { capacity: number; previewBytes: number; recent: BugPacket[] };
}
export interface BugReportDetail { report: BugReportSummary; context: BugContext }
export interface Maintenance { enabled: boolean; message: string; since: string | null; by: string | null }
export interface ScheduledEvent { id: number; kind: 'announce' | 'exp' | 'drop'; startsAt: string; endsAt: string | null; repeatMinutes: number | null; text: string; multiplier: number | null; createdBy: string | null; cancelledAt: string | null; lastRunAt: string | null }
export interface PopulationPoint { at: string; players: number; connections: number }
export interface Economy {
  report: {
    totalKina: number; characters: number; accounts: number;
    richest: { characterId: string; name: string; accountName: string; level: number; kina: number }[];
    items: { templateId: number; name: string | null; quantity: number; holders: number; largestStack: number }[];
    levels: { level: number; count: number }[];
    newAccounts: { day: string; count: number }[];
    newCharacters: { day: string; count: number }[];
  };
  suspects: { name: string; characterId: string | null; level: number; mapId: number | null; speedRejects: number }[];
}
export interface Backups { configured: boolean; files: { name: string; length: number; at: string }[] }
/** A world server's debug logging (GET/POST /api/admin/debug): the folder, what is being written now, and the files. */
export interface DebugState {
  directory: string | null;
  mapEventLog: string | null;
  traces: boolean;
  files: { name: string; length: number; at: string; kind: 'trace' | 'map-events' }[];
}

Object.assign(ERRORS, {
  invalid_sanction: 'Choose ban or mute, a reason of up to 200 characters and a valid duration.',
  unknown_player: 'No character or account has that name.',
  invalid_ticket: 'Write between 3 and 1000 characters.',
  too_many_tickets: 'There are already 10 open tickets from this account.',
  invalid_event: 'Check the times: boosts need an end after the start and a multiplier between 0.1 and 10.',
  invalid_message: 'The message is limited to 200 characters.',
  invalid_reward: 'Add 1 to 10 items (or Kina) and a message of up to 200 characters.',
  quna_item: "Quna is the account's wallet: add it on the account's page (Accounts), not as an item.",
  quna_realm_only: "Quna is the account's wallet: add it on the account's page (Accounts), not as an item.",
  invalid_quna_change: 'Enter an amount other than 0 and a note of up to 200 characters.',
  quna_insufficient: 'The wallet holds less Quna than that.',
  quna_request_conflict: 'That change was already made with other details. Try again.',
  unknown_account: 'That account does not exist.',
  reward_too_large: 'That reward is too large for one request (over 20,000 grants).',
  map_not_loaded: 'Nobody has loaded that map since the server started.',
  invalid_area: 'The radius must be between 1 m and 200 m.',
  invalid_spawn: 'Spawn between 1 and 20 monsters.',
  unknown_npc: 'That NPC is not placed on any map, so it has no template.',
  backups_not_configured: 'Backups are not configured on this host.',
  debug_not_configured: 'This world server has no debug folder (Aion:Native:DebugDirectory).',
  backup_failed: 'The backup failed; see the server log.',
});

/** GET /api/admin/worlds (World Management). */
export interface WorldRuntime { state: string; processId: number | null; since: string | null; players: number | null; connections: number | null; lastError: string | null; restarts: number }
export interface WorldEntry {
  worldId: number; name: string; lightServerId: number; darkServerId: number; kind: 'managed' | 'remote'; host: string | null;
  gamePort: number | null; internalPort: number | null; enabled: boolean; levelCap: number | null; createdAt: string; updatedAt: string;
  characters: number; listed: boolean; runtime: WorldRuntime;
  /** Reported by the world when it signs in to the realm link (remote: its control address), its last sign-in and build. */
  controlHost: string | null; lastSeenAt: string | null; version: string | null; hasToken: boolean;
}
export interface WorldList { canManage: boolean; supervised: boolean; publicAddress: string | null; worlds: WorldEntry[] }
Object.assign(ERRORS, {
  unknown_world: 'That world no longer exists.',
  not_remote: 'Only a remote world signs in with a token; this host gives managed worlds theirs.',
  world_required: 'Choose a world server first.',
  world_offline: 'That world server is not running. Start it on the Worlds page.',
  world_enabled: 'Stop the world first.',
  world_has_characters: 'The world still has characters, so it cannot be removed.',
  world_id_in_use: 'That world id is already used.',
  invalid_world_id: 'The world id must be between 1 and 65535 and cannot change.',
  invalid_name: 'Give a name of 1 to 32 characters without leading or trailing spaces.',
  name_in_use: 'Another world already has that name.',
  invalid_server_ids: 'The Light and Dark row ids must differ and be between 1 and 65535.',
  server_id_in_use: 'Another world already uses one of these server list rows.',
  invalid_kind: 'Choose where the world runs.',
  invalid_host: 'The address must be an IPv4 address such as 203.0.113.7.',
  game_port_required: 'Give the world a game port.',
  internal_port_required: 'A world on this host needs a control port.',
  invalid_port: 'Ports are between 1 and 65535.',
  port_in_use: 'That port is already used by another world.',
  invalid_level_cap: 'The level cap is between 1 and 100.',
  not_managed: 'Only worlds on this host can be restarted from here.',
  not_supervised: 'This host does not start worlds.',
});
