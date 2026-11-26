using System.Text.Json;
using System.Text.Json.Nodes;
using Aion.Application.Gameplay;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

// Moving a character to another world (docs/STAFF_CHARACTERS.md, migration 108). The tables that move are found in the
// catalogue, not listed by hand: every table with a foreign key on `character_id` to game.characters, or to such a table
// by a key that includes `character_id`, except the ones that stay in the world. A table added later moves with the
// character without a change here.
public sealed partial class PostgresGameplayStore
{
    /// <summary>Character tables that stay in the source world: legion, mail, the purchase ledger, creation retries, audit.</summary>
    private static readonly HashSet<string> StaysInWorld = ["audit.events", "game.guild_members", "game.guild_join_requests",
        "game.guild_storage_history", "game.character_posts", "game.shop_orders", "game.character_initializations"];

    private const string CharactersTable = "game.characters", ItemsTable = "game.item_instances";

    /// <param name="Columns">Every column (name → whether an insert may name it: not an identity or generated column).</param>
    /// <param name="UuidKey">A single-column uuid primary key other than character_id: the import gives each row a new one.</param>
    /// <param name="Refs">Single-column foreign keys to another moving table's <see cref="UuidKey"/>: remapped on import.</param>
    private sealed record MovingTable(string Name, Dictionary<string, bool> Columns, string? UuidKey, List<(string Column, string Table)> Refs);

    private static string Quoted(string name) => string.Join('.', name.Split('.').Select(p => "\"" + p.Replace("\"", "\"\"") + "\""));

    /// <summary>The moving tables in insert order (a table after the tables it references).</summary>
    private static async Task<List<MovingTable>> MovingTables(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
    {
        const string Name = "n.nspname||'.'||cl.relname";
        var names=new HashSet<string>();
        // A table moves when one of its foreign keys includes character_id and points at game.characters or at a table that
        // moves: game.skill_slots hangs off game.character_skills by (character_id,skill_id), not off game.characters.
        for(var grew=true;grew;)
        {
            grew=false;
            await using var q=Query(c,t,$"""
                SELECT DISTINCT {Name} FROM pg_constraint con JOIN pg_class cl ON cl.oid=con.conrelid JOIN pg_namespace n ON n.oid=cl.relnamespace
                JOIN pg_class fc ON fc.oid=con.confrelid JOIN pg_namespace fn ON fn.oid=fc.relnamespace
                WHERE con.contype='f' AND fn.nspname||'.'||fc.relname=ANY($1)
                  AND EXISTS(SELECT FROM unnest(con.conkey) k JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=k WHERE a.attname='character_id')
                """,(object)names.Append(CharactersTable).ToArray());
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                if(r.GetString(0)!=CharactersTable && !StaysInWorld.Contains(r.GetString(0)) && names.Add(r.GetString(0))) grew=true;
        }
        var all=names.Append(CharactersTable).ToArray();
        // Each array below is ONE argument ($1): cast to object, or C# spreads it into Query's params object[].
        var tables=names.ToDictionary(n => n, n => new MovingTable(n, new(), null, new()));
        var columns=new Dictionary<string,Dictionary<string,bool>>();
        await using(var q=Query(c,t,$"""
            SELECT {Name},a.attname,a.attidentity='' AND a.attgenerated='' FROM pg_attribute a JOIN pg_class cl ON cl.oid=a.attrelid
            JOIN pg_namespace n ON n.oid=cl.relnamespace WHERE a.attnum>0 AND NOT a.attisdropped AND {Name}=ANY($1) ORDER BY a.attnum
            """,(object)all))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                if(!columns.TryGetValue(r.GetString(0),out var set)) columns[r.GetString(0)]=set=new();
                set[r.GetString(1)]=r.GetBoolean(2);
            }
        }
        foreach(var table in tables.Values) foreach(var (column,insertable) in columns[table.Name]) table.Columns[column]=insertable;
        var keyed=new Dictionary<string,string>();
        await using(var q=Query(c,t,$"""
            SELECT {Name},a.attname,format_type(a.atttypid,a.atttypmod),a.attidentity<>'' FROM pg_constraint con JOIN pg_class cl ON cl.oid=con.conrelid
            JOIN pg_namespace n ON n.oid=cl.relnamespace JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=con.conkey[1]
            WHERE con.contype='p' AND cardinality(con.conkey)=1 AND {Name}=ANY($1)
            """,(object)names.ToArray()))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                if(r.GetString(1)=="character_id") continue;
                // A uuid key is regenerated (and remapped); an identity key is regenerated by the target. Anything else (a
                // serial or natural key) could collide with the target's rows: refuse rather than guess.
                if(r.GetString(2)=="uuid") keyed[r.GetString(0)]=r.GetString(1);
                else if(!r.GetBoolean(3)) throw new GameplayException("unsupported_schema");
            }
        }
        var after=tables.Keys.ToDictionary(n => n, _ => new HashSet<string>());
        await using(var q=Query(c,t,$"""
            SELECT {Name},fn.nspname||'.'||fc.relname,a.attname,cardinality(con.conkey),
                   ARRAY(SELECT fa.attname::text FROM unnest(con.confkey) k JOIN pg_attribute fa ON fa.attrelid=con.confrelid AND fa.attnum=k) FROM pg_constraint con
            JOIN pg_class cl ON cl.oid=con.conrelid JOIN pg_namespace n ON n.oid=cl.relnamespace
            JOIN pg_class fc ON fc.oid=con.confrelid JOIN pg_namespace fn ON fn.oid=fc.relnamespace
            JOIN pg_attribute a ON a.attrelid=con.conrelid AND a.attnum=con.conkey[1]
            WHERE con.contype='f' AND {Name}=ANY($1)
            """,(object)names.ToArray()))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var (from,to,column,width)=(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt32(3));
                if(!tables.ContainsKey(to) || to==from) continue;
                after[from].Add(to);
                if(width==1) tables[from].Refs.Add((column,to));
                // A composite key stays valid only through columns the import keeps (character_id is rewritten everywhere);
                // one that includes a regenerated uuid key would need remapping: refuse rather than break it.
                else if(keyed.TryGetValue(to,out var key) && r.GetFieldValue<string[]>(4).Contains(key)) throw new GameplayException("unsupported_schema");
            }
        }
        foreach(var (name,key) in keyed) tables[name]=tables[name] with { UuidKey=key };
        // A reference into a moving table is remapped only through that table's uuid key.
        if(tables.Values.Any(x => x.Refs.Any(f => tables[f.Table].UuidKey is null))) throw new GameplayException("unsupported_schema");
        var ordered=new List<MovingTable>(); var placed=new HashSet<string>();
        while(ordered.Count<tables.Count)
        {
            var next=tables.Keys.Where(n => !placed.Contains(n) && after[n].All(placed.Contains)).OrderBy(n => n,StringComparer.Ordinal).FirstOrDefault()
                ?? throw new GameplayException("unsupported_schema");   // a cycle
            placed.Add(next); ordered.Add(tables[next]);
        }
        return ordered;
    }

    private static string MoveTag(Guid request) => "transfer:"+request.ToString("N");

    /// <summary>The actor when this world's account mirror has them (audit.events.actor_id is a foreign key), else null.</summary>
    private static async Task<object> MirroredActor(NpgsqlConnection c, NpgsqlTransaction t, Guid actor, CancellationToken ct) =>
        await Scalar(c,t,"SELECT account_id FROM auth.accounts WHERE account_id=$1",ct,actor) ?? DBNull.Value;

    private static async Task AuditMove(NpgsqlConnection c, NpgsqlTransaction t, string action, Guid character, Guid actor, object details, CancellationToken ct)
    {
        await using var q=Query(c,t,"INSERT INTO audit.events(action,character_id,actor_id,details) VALUES($1,$2,$3,$4)",action,character,await MirroredActor(c,t,actor,ct),Json(details));
        q.Parameters[2].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid;
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<CharacterMovePackage> ExportCharacterAsync(Guid requestId, Guid actor, Guid character, int targetWorld, string? reason, CancellationToken ct=default)
    {
        if(requestId==Guid.Empty || targetWorld<=0) throw new GameplayException("invalid_move");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        Guid owner; string name; int world, race; bool blocked; string blockReason;
        await using(var q=Query(c,t,"SELECT account_id,name,world_id,race,blocked_at IS NOT NULL,block_reason FROM game.characters WHERE character_id=$1 FOR UPDATE",character))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) throw new GameplayException("character_unavailable");
            owner=r.GetGuid(0); name=r.GetString(1); world=r.GetInt32(2); race=r.GetInt32(3); blocked=r.GetBoolean(4); blockReason=r.GetString(5);
        }
        var state=await Scalar(c,t,"SELECT state FROM game.character_world_moves WHERE request_id=$1 AND direction='out'",ct,requestId) as string;
        if(state=="finished") throw new GameplayException("move_finished");
        if(state=="cancelled") throw new GameplayException("move_cancelled");
        if(owner==Graveyard) throw new GameplayException("character_deleted");
        if(world==targetWorld) throw new GameplayException("same_world");
        var tag=MoveTag(requestId);
        if(state is null)
        {
            // A deactivated character, or one another move holds, stays where it is.
            if(blocked) throw new GameplayException(blockReason.StartsWith("transfer:",StringComparison.Ordinal) ? "move_in_progress" : "character_blocked");
            // Open market listings (their items are in escrow, expired ones too until taken back) and unpaid sales belong to
            // this world's market; sold, cancelled and paid ones are history and stay here.
            if(await Scalar(c,t,"SELECT EXISTS(SELECT FROM game.trade_listings WHERE seller_character_id=$1 AND NOT sold AND NOT cancelled) "
                +"OR EXISTS(SELECT FROM game.trade_deliveries WHERE seller_character_id=$1 AND NOT completed)",ct,character) is true)
                throw new GameplayException("market_pending");
            await Execute(c,t,"UPDATE game.characters SET blocked_at=now(),block_reason=$2 WHERE character_id=$1",ct,character,tag);
            await Execute(c,t,"INSERT INTO game.character_world_moves(request_id,direction,character_id,other_world,state) VALUES($1,'out',$2,$3,'exported')",ct,requestId,character,targetWorld);
            await AuditMove(c,t,"staff_character_world_move_out",character,actor,new {requestId,name,account=owner,targetWorld,reason=reason??"",actor},ct);
        }
        else if(blockReason!=tag) throw new GameplayException("request_conflict");
        var characterRow=JsonSerializer.Deserialize<JsonElement>((string)(await Scalar(c,t,"SELECT to_jsonb(x)::text FROM game.characters x WHERE x.character_id=$1",ct,character))!);
        var rows=new Dictionary<string,JsonElement>();
        foreach(var table in await MovingTables(c,t,ct))
        {
            // Account storage is the account's, in this world (server/DATABASES.md): it stays.
            var filter=table.Name==ItemsTable && table.Columns.ContainsKey("account_storage_owner") ? " AND x.account_storage_owner IS NULL" : "";
            var text=(string)(await Scalar(c,t,$"SELECT COALESCE(jsonb_agg(to_jsonb(x)),'[]'::jsonb)::text FROM {Quoted(table.Name)} x WHERE x.character_id=$1{filter}",ct,character))!;
            rows[table.Name]=JsonSerializer.Deserialize<JsonElement>(text);
        }
        await t.CommitAsync(ct);
        return new(requestId,world,character,name,race,characterRow,rows);
    }

    public async Task<CharacterMoveResult> ImportCharacterAsync(CharacterMoveImport input, int world, CancellationToken ct=default)
    {
        var package=input.Package;
        if(input.RequestId==Guid.Empty || package is null || package.RequestId!=input.RequestId || package.Character.ValueKind!=JsonValueKind.Object || package.Tables is null)
            throw new GameplayException("invalid_move");
        if(package.SourceWorld==world) throw new GameplayException("same_world");
        var name=(input.NewName is { Length: > 0 } renamed ? renamed : package.Name).Trim();
        if(name.Length is 0 or >64 || name.Any(char.IsControl) || name.Contains('#')) throw new GameplayException("invalid_name");
        var reason=(input.Reason??"").Trim();
        if(reason.Length>200) throw new GameplayException("invalid_reason");
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        await Execute(c,t,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",ct,"move:"+input.RequestId);
        // A repeated import answers the character it made.
        await using(var q=Query(c,t,"SELECT m.character_id,c.name,c.account_id,c.world_id,COALESCE(c.lobby_slot,-1) FROM game.character_world_moves m "
            +"JOIN game.characters c USING(character_id) WHERE m.request_id=$1 AND m.direction='in'",input.RequestId))
        {
            await using var r=await q.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) return new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.GetInt32(3),r.GetInt32(4));
        }
        var target=input.TargetAccount;
        if(target==Guid.Empty || target==Graveyard) throw new GameplayException("unknown_account");
        var staffTarget=(await StaffRoles(c,t,target,ct)).Length>0;
        if(Aion.Core.Characters.CharacterNames.IsStaffStyle(name) && !staffTarget) throw new GameplayException("reserved_name");
        if(await Scalar(c,t,"SELECT EXISTS(SELECT FROM game.characters WHERE world_id=$1 AND lower(name)=lower($2))",ct,world,name) is true)
            throw new GameplayException("name_unavailable");
        var slot=await FreeLobbySlot(c,t,target,world,package.Race,ct);

        // Both worlds run the same migrations; a column or table this world does not know would be lost, so it is refused.
        var tables=await MovingTables(c,t,ct);
        var known=tables.ToDictionary(x => x.Name);
        var characterColumns=new Dictionary<string,bool>();
        await using(var q=Query(c,t,"SELECT attname,attidentity='' AND attgenerated='' FROM pg_attribute WHERE attrelid='game.characters'::regclass AND attnum>0 AND NOT attisdropped"))
        { await using var r=await q.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) characterColumns[r.GetString(0)]=r.GetBoolean(1); }
        static bool Fits(JsonElement row, Dictionary<string,bool> columns) => row.ValueKind==JsonValueKind.Object && row.EnumerateObject().All(p => columns.ContainsKey(p.Name));
        if(!Fits(package.Character,characterColumns)) throw new GameplayException("schema_mismatch");
        foreach(var (table,rows) in package.Tables)
            if(!known.TryGetValue(table,out var moving) || rows.ValueKind!=JsonValueKind.Array || rows.EnumerateArray().Any(row => !Fits(row,moving.Columns)))
                throw new GameplayException("schema_mismatch");

        var id=Guid.NewGuid();
        var character=JsonNode.Parse(package.Character.GetRawText())!.AsObject();
        character["character_id"]=id; character["account_id"]=target; character["world_id"]=world; character["name"]=name;
        character["lobby_slot"]=slot; character["pending_delete_at"]=null; character["blocked_at"]=null; character["block_reason"]="";
        // Staff standing needs a staff account (migration 035).
        if(!staffTarget && character.ContainsKey("staff")) character["staff"]=false;
        await Insert(c,t,CharactersTable,characterColumns,new JsonArray(character),null,ct);

        // Rows the new character got from triggers (class skills for its level, their skill bar) give way to the moved ones,
        // also where the moved character had none: the package carries every moving table, empty ones included.
        foreach(var table in Enumerable.Reverse(tables))
            if(package.Tables.ContainsKey(table.Name)
               && await Scalar(c,t,$"SELECT EXISTS(SELECT FROM {Quoted(table.Name)} WHERE character_id=$1)",ct,id) is true)
                await Execute(c,t,$"DELETE FROM {Quoted(table.Name)} WHERE character_id=$1",ct,id);

        var keys=new Dictionary<string,Dictionary<string,string>>();   // table → old key → new key
        var wires=new Dictionary<long,long>();                             // old item wire id → new
        foreach(var table in tables)
        {
            if(!package.Tables.TryGetValue(table.Name,out var moved) || moved.GetArrayLength()==0) continue;
            var rows=new JsonArray();
            var oldWires=new Dictionary<string,long>();
            var map=table.UuidKey is null ? null : keys[table.Name]=new();
            foreach(var row in moved.EnumerateArray())
            {
                var node=JsonNode.Parse(row.GetRawText())!.AsObject();
                node["character_id"]=id;
                if(table.UuidKey is { } key && node[key]?.GetValue<string>() is { } old)
                {
                    var fresh=Guid.NewGuid().ToString();
                    map![old]=fresh; node[key]=fresh;
                    if(table.Name==ItemsTable && node["wire_id"] is { } wire) oldWires[fresh]=wire.GetValue<long>();
                }
                foreach(var (column,referenced) in table.Refs)
                    if(node[column]?.GetValue<string>() is { } oldRef)
                        node[column]=keys.TryGetValue(referenced,out var refs) && refs.TryGetValue(oldRef,out var freshRef) ? freshRef
                            : throw new GameplayException("unsupported_schema");   // a reference to a row that did not move
                rows.Add(node);
            }
            var returned=await Insert(c,t,table.Name,table.Columns,rows,table.Name==ItemsTable ? "instance_id::text,wire_id" : null,ct);
            foreach(var (instance,wire) in returned) if(oldWires.TryGetValue(instance,out var old)) wires[old]=wire;
        }
        // The hotbar names items by wire id (QuickSlotRecord.ItemUid): the items have new ones here.
        if(wires.Count>0 && known.ContainsKey("game.quick_slots"))
            await Execute(c,t,"""
                UPDATE game.quick_slots q SET item_uid=(m->>1)::bigint FROM jsonb_array_elements($2) m
                WHERE q.character_id=$1 AND q.item_uid<>0 AND q.item_uid=(m->>0)::bigint
                """,ct,id,JsonSerializer.SerializeToElement(wires.Select(w => new[]{w.Key,w.Value})));
        await Execute(c,t,"INSERT INTO game.character_world_moves(request_id,direction,character_id,other_world,other_character,state,finished_at) VALUES($1,'in',$2,$3,$4,'imported',now())",ct,
            input.RequestId,id,package.SourceWorld,package.CharacterId);
        await AuditMove(c,t,"staff_character_world_move_in",id,input.Actor,new {requestId=input.RequestId,name,fromName=package.Name,account=target,
            fromWorld=package.SourceWorld,fromCharacter=package.CharacterId,slot,reason,actor=input.Actor},ct);
        await t.CommitAsync(ct);
        return new(id,name,target,world,slot);
    }

    /// <summary>Inserts the rows (jsonb objects) into the columns they carry that an insert may name; the RETURNING pairs, if asked.</summary>
    private static async Task<List<(string,long)>> Insert(NpgsqlConnection c, NpgsqlTransaction t, string table, Dictionary<string,bool> columns, JsonArray rows,
        string? returning, CancellationToken ct)
    {
        var present=rows.SelectMany(r => r!.AsObject().Select(p => p.Key)).ToHashSet();
        var names=string.Join(',',columns.Where(x => x.Value && present.Contains(x.Key)).Select(x => Quoted(x.Key)));
        var sql=$"INSERT INTO {Quoted(table)}({names}) SELECT {names} FROM jsonb_populate_recordset(NULL::{Quoted(table)},$1)"
            +(returning is null ? "" : " RETURNING "+returning);
        var result=new List<(string,long)>();
        await using var q=Query(c,t,sql,JsonSerializer.SerializeToElement(rows));
        if(returning is null) { await q.ExecuteNonQueryAsync(ct); return result; }
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result.Add((r.GetString(0),r.GetInt64(1)));
        return result;
    }

    public async Task FinishCharacterMoveAsync(Guid requestId, Guid actor, Guid character, Guid? movedTo, CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct); await using var t=await c.BeginTransactionAsync(ct);
        string? state=null; int targetWorld=0;
        await using(var q=Query(c,t,"SELECT state,other_world FROM game.character_world_moves WHERE request_id=$1 AND direction='out' AND character_id=$2 FOR UPDATE",requestId,character))
        { await using var r=await q.ExecuteReaderAsync(ct); if(await r.ReadAsync(ct)) { state=r.GetString(0); targetWorld=r.GetInt32(1); } }
        if(state is null) throw new GameplayException("unknown_move");
        if(state=="finished" && movedTo is not null || state=="cancelled" && movedTo is null) { await t.CommitAsync(ct); return; }
        if(state!="exported") throw new GameplayException("request_conflict");
        var name=(string)(await Scalar(c,t,"SELECT name FROM game.characters WHERE character_id=$1 FOR UPDATE",ct,character))!;
        if(movedTo is { } moved)
        {
            // The original is deleted as a due lobby deletion is (CompleteDeletions); its rows stay here for the record.
            await Execute(c,t,"UPDATE game.characters SET account_id=$2,lobby_slot=NULL,pending_delete_at=NULL,blocked_at=NULL,block_reason='',"
                +"name=left(name,40)||'#deleted'||wire_id WHERE character_id=$1",ct,character,Graveyard);
            await Execute(c,t,"UPDATE game.character_world_moves SET state='finished',other_character=$2,finished_at=now() WHERE request_id=$1 AND direction='out'",ct,requestId,moved);
            await AuditMove(c,t,"staff_character_world_move_finish",character,actor,new {requestId,name,targetWorld,movedTo=moved,actor},ct);
        }
        else
        {
            await Execute(c,t,"UPDATE game.characters SET blocked_at=NULL,block_reason='' WHERE character_id=$1 AND block_reason=$2",ct,character,MoveTag(requestId));
            await Execute(c,t,"UPDATE game.character_world_moves SET state='cancelled',finished_at=now() WHERE request_id=$1 AND direction='out'",ct,requestId);
            await AuditMove(c,t,"staff_character_world_move_cancel",character,actor,new {requestId,name,targetWorld,actor},ct);
        }
        await t.CommitAsync(ct);
    }
}
