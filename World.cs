using System.Collections.Concurrent;
using System.Text.Json;
namespace ElanciaLocal;

public sealed record ShopEntry(string Key,uint Price,byte Flags,uint Id=0,string? Description=null,string? DisplayName=null,
    byte? RestockCapacity=null,ushort? Durability=null,bool FixedStock=false,uint? SellPrice=null);
public sealed record ShopRestock(int Map,string Merchant,string Item,long Due,int Quantity,uint MerchantId=0,uint EntryId=0);
internal static class MerchantRestock{
    internal const string LuckyPaper="recorded-8e17-c7e0bfeec0ccb1eab5e7c1bec0cc";
    internal const int IntervalMs=10*60*1000;
    internal static bool IsLuckyPaper(LocalNpc npc,ShopEntry entry)=>npc.Map%10000==201&&npc.Name=="행운장"&&entry.Key==LuckyPaper;
    internal static bool ReplenishesOnCatalog(LocalNpc npc,ShopEntry entry)=>npc.Category!=2&&npc.Map==10206&&npc.Name=="케이"&&!entry.FixedStock&&
        entry.Key is "recorded-8000-b4ebb0c5" or "recorded-8001-bed7bdba";
    internal static (string Item,uint EntryId) StockKey(LocalNpc npc,ShopEntry entry)=>
        (entry.Key,npc.Category==2?entry.Id:0);
    internal static bool HasFixedStock(LocalNpc npc,ShopEntry entry)=>npc.Category!=2&&entry.FixedStock&&!IsLuckyPaper(npc,entry);
    internal static byte? Capacity(LocalNpc npc,ShopEntry entry){
        if(npc.Category==2||HasFixedStock(npc,entry))return entry.Flags;
        if(IsLuckyPaper(npc,entry))return 10;
        // Sylvia's rod stays at 100 before and after the captured purchase.
        if(npc.Map%10000==216&&npc.Name=="실비아"&&entry.Key==Fishing.RodKey&&entry.Flags==100)return null;
        return entry.RestockCapacity;
    }
}
internal sealed record ShopOffer(byte Kind,byte Slot,ItemPlacement Item,uint Price);
public sealed record NpcChoice(string Text,ushort Id);
public sealed record NpcDialogData(byte Mode,byte Flags,byte PortraitKind,ushort Sprite,byte Color,uint HeaderData,string Text,
    NpcChoice[]? Options=null,string? Prompt=null,int X=0,int Y=0,ushort[]? MenuCodes=null,ushort ActionCode=0,string? ActionText=null);
public sealed record NpcDialogReply(byte Flags,ushort Menu,int Length,NpcDialogData Dialog);
public sealed record NpcPursuitReply(string Request,byte[] Packet);
public sealed record NpcPursuitTravel(string Request,StartLocation Destination,ushort? Sound=null);
public sealed record LocalNpc(uint Id,string Name,int Map,int X,int Y,byte Floor,ushort Sprite,byte Flags,byte Type,string Text,ShopEntry[]? Shop=null,bool ExtendedShop=false,bool Crafting=false,
    NpcDialogData? OpenDialog=null,NpcDialogReply[]? DialogReplies=null,NpcPursuitReply[]? PursuitReplies=null,NpcPursuitTravel[]? PursuitTravel=null,byte Category=1){
    internal bool UsesExtendedShop=>ExtendedShop||Category==2;
    internal bool HasValidShopCatalog=>!UsesExtendedShop||Shop!=null&&
        Shop.All(e=>e.Id!=0&&(e.Durability is null||Category==2&&e.Durability<=EquipmentItems.FullDurability))&&
        Shop.Select(e=>e.Id).Distinct().Count()==Shop.Length;
    public byte ShopFlags=>UsesExtendedShop?(byte)14:(byte)1;
    internal bool HasService(ushort action)=>OpenDialog is {Mode:13,MenuCodes:{} codes}
        ?codes.Contains(action):action==GamePackets.ShopSellAction&&Shop!=null;
}
internal static class RolanciaNpcDimensions
{
    // Permanent services with the same identity on the shared terrain maps.
    static readonly HashSet<(int Map,string Name)> Services=[
        (2,"길드만들기"),(4,"오크왕자"),(200,"무타이"),(201,"행운장"),(202,"수련장"),(203,"세크티"),(204,"슈"),(204,"카딜라"),(205,"칼라스"),(205,"초브"),
        (206,"케이"),(206,"실루엣"),(206,"휴"),(207,"피니"),(207,"테디"),(208,"라드"),(208,"할아버지"),(208,"예절보로"),
        (209,"스텔라"),(216,"실비아"),
        (210,"그레파"),(210,"홀리"),(210,"티포네"),(210,"미용바니"),(211,"테레즈"),(212,"레나"),
        (213,"나무보로"),(214,"네루아"),(214,"오블로"),(215,"루비"),(218,"메티니"),(219,"에레노아"),
        (220,"라체"),(256,"올드스미스"),(RanicheQuest.CourtMap,"렉스")];
    static readonly HashSet<int> SharedMaps=[..Services.Select(service=>service.Map),3,5,7,8,9,10,229,232,237,239,241,242,255,258,
        266,269,1500,2004,2008,2500];
    internal static bool SharesMap(int map)=>WorldSettings.AllowsDuel(map)||
        map is >=0 and <30000&&SharedMaps.Contains(map%10000);

    internal static LocalNpc[] Populate(IEnumerable<LocalNpc> source){
        var all=source.ToDictionary(n=>n.Id);
        var groups=all.Values.Where(n=>n.Map is >=0 and <30000&&Services.Contains((n.Map%10000,n.Name)))
            .GroupBy(n=>(Map:n.Map%10000,n.Name)).ToArray();
        foreach(var group in groups){
            var observed=group.OrderBy(n=>n.Map).ThenBy(n=>n.Id).ToArray();
            for(int dimension=0;dimension<3;dimension++){
                int map=dimension*10000+group.Key.Map;
                var local=observed.Where(n=>n.Map==map).ToArray();
                if(local.Length==0){
                    var seed=observed[0];
                    // Keep IDs stable while loading new dialogue and shop data.
                    uint id=0x63000000U|((uint)dimension<<20)|(seed.Id&0x000FFFFFU);
                    all.Add(id,Fill(seed with {Id=id,Map=map,OpenDialog=null,Shop=null,DialogReplies=null,
                        PursuitReplies=null,PursuitTravel=null},observed.Where(n=>n.Sprite==seed.Sprite).ToArray()));
                }else foreach(var npc in local)
                    all[npc.Id]=Fill(npc,observed.Where(n=>n.Map!=map&&n.Sprite==npc.Sprite).ToArray());
            }
        }
        return all.Values.ToArray();
    }

    internal static LocalNpc Fill(LocalNpc local,LocalNpc[] references){
        var shop=references.FirstOrDefault(n=>n.Shop is {Length:>0});
        var opening=references.Select(n=>n.OpenDialog).FirstOrDefault(d=>d!=null);
        if(opening?.Mode==13)opening=opening with {X=local.X,Y=local.Y};
        return local with {
            Shop=local.Shop??shop?.Shop,
            ExtendedShop=local.Shop!=null?local.ExtendedShop:shop?.ExtendedShop??false,
            Crafting=local.Crafting||references.Any(n=>n.Crafting),
            OpenDialog=local.OpenDialog??opening,
            DialogReplies=Merge(local.DialogReplies,references.SelectMany(n=>n.DialogReplies??[]),r=>(r.Flags,r.Menu,r.Length)),
            PursuitReplies=Merge(local.PursuitReplies,references.SelectMany(n=>n.PursuitReplies??[]),r=>r.Request),
            PursuitTravel=Merge(local.PursuitTravel,references.SelectMany(n=>(n.PursuitTravel??[])
                .Select(route=>WithinDimension(route,n.Map,local.Map))),r=>r.Request)
        };
    }

    static NpcPursuitTravel WithinDimension(NpcPursuitTravel route,int source,int target)=>
        route.Destination.Map/10000==source/10000
            ?route with {Destination=route.Destination with {Map=target/10000*10000+route.Destination.Map%10000}}
            :route;

    static T[]? Merge<T,TKey>(T[]? local,IEnumerable<T> fallback,Func<T,TKey> key){
        var merged=(local??[]).Concat(fallback).DistinctBy(key).ToArray();
        return merged.Length==0?local:merged;
    }
}
public sealed record FieldObject(uint Id,int Map,int X,int Y,byte Floor,ushort Sprite,byte Flags,byte Type,string Name,byte Category,uint Quantity=0,string? ItemKey=null,ushort? Durability=null,uint DropOwnerId=0,
    ushort? BaitRemaining=null){
    internal const ushort FightingRoosterSprite=0x43F2;
    internal bool IsFightingRooster=>Sprite==FightingRoosterSprite&&Name.EndsWith("의싸움닭",StringComparison.Ordinal);
    internal string? Invitation { get; init; }
    internal long? ExpiresAt { get; init; }
    internal ushort? NativeDurability { get; init; }
}
public sealed class FieldMonster(FieldObject appearance,int hp,string[] loot,
    int roamTick=0,int moveEvery=1,(int X,int Y)[]? alternatives=null,bool respawns=true){
    const int FirstRetaliationDelay=200;
    const int AttackInterval=1500;
    const int SpellInterval=4800;
    const int PursuitInterval=1700;
    const int PursuitTimeout=30_000;
    const int RoamRadius=10;
    const int PursuitRadius=16;
    readonly int meleeIntervalMs=appearance.Name switch {
        "암탉"=>1600,
        "트렌트"=>2050,
        "돼지"=>2100,
        "오크" or "푸푸" or "울보요정"=>1700,
        "오크(나이트)"=>1800,
        "다크오우거(영)"=>1900,
        "스톤고렘"=>2400,
        "실버고렘"=>2600,
        _=>AttackInterval
    };
    readonly int maxHp=RecordedWorld.MonsterHp(HealthProfile(appearance.Name),hp);
    readonly (int X,int Y)[] spawnPoints=[(appearance.X,appearance.Y),..(alternatives??[])];
    (int X,int Y) spawnAnchor=(appearance.X,appearance.Y);
    long nextAttackAt;
    long firstAttackAt;
    long nextPursuitAt;
    long pursuitUntil;
    long nextRoamAt;
    bool attackedTarget;
    public FieldObject Appearance { get; private set; }=appearance;
    public string[] Loot { get; }=loot;
    public int Hp { get; private set; }=RecordedWorld.MonsterHp(HealthProfile(appearance.Name),hp);
    internal ushort Defense {get;}=RecordedWorld.MonsterDefense(HealthProfile(appearance.Name));
    internal bool IsTreasureChest {get;}=TreasureChests.IsChest(appearance.Name,appearance.Sprite);
    internal bool IsPirateChest=>IsTreasureChest&&Appearance.Sprite==TreasureChests.Pirate;
    internal bool Respawns {get;}=respawns;
    internal FieldMonster? ReleasedZombie {get;set;}
    static string HealthProfile(string name)=>name==TreasureChests.ZombieName?"좀비":name;
    // Distances at which unprovoked approaches were seen in the capture.
    internal int SightRange {get;}=appearance.Name switch {
        "오크"=>9,"오크(셔먼)"=>7,"오크(나이트)"=>5,"다크오우거(영)"=>2,
        "곰" or "스톤고렘" or "실버고렘"=>1,_=>0
    };
    internal bool CastsSpell {get;}=appearance.Name=="오크(셔먼)";
    internal int ChaseStepMs=>PursuitInterval;
    internal int PreferredAttackRange=>CastsSpell?3:1;
    internal bool CanStrike(Character target)=>CastsSpell
        ?WorldHub.InMonsterReach(Appearance,target,attackedTarget?8:PreferredAttackRange)
        :WorldHub.InMeleeReach(Appearance,target);
    internal WorldSession? Target {get;private set;}
    internal ushort ContactDamage {get;}=appearance.Name switch {
        "곰"=>24,"스톤고렘"=>8,"실버고렘"=>10,"다크오우거(영)"=>15,_=>1
    };
    // Fixed fixture values for stationary tests. Live spawns roll once within the species' published range.
    internal int Level {get;private set;}=appearance.Sprite switch {
        17393=>5,17394=>7,17397=>16,17424=>25,18406=>26,18411=>29,_=>RecordedWorld.MonsterLevel(HealthProfile(appearance.Name))
    };
    internal int TrainingLimit=>Math.Clamp(Level,0,100)*100;
    internal void RollLevel()=>Level=RecordedWorld.RollMonsterLevel(HealthProfile(Appearance.Name),Level);
    public long RespawnAt { get; private set; }
    internal long RespawnDelayMs(int? configuredSeconds=null)=>
        (configuredSeconds??TreasureChests.ObservedRespawnSeconds(Appearance)??8)*1000L;
    internal FieldMonster AdditionalSpawn(FieldObject spawn)=>new(spawn,maxHp,Loot,roamTick,moveEvery);
    public ushort Hit(ushort damage,long respawnAt){
        ushort applied=(ushort)Math.Min(Hp,damage);
        Hp-=applied;
        if(Hp==0){RespawnAt=respawnAt;ClearTarget();}
        return applied;
    }
    internal void Provoke(WorldSession attacker,long now){
        // Stationary first replies for 푸푸 were recorded in both Alpha and Beta South Plain.
        int delay=Appearance.Name=="푸푸"?1800:FirstRetaliationDelay;
        if(Target!=attacker)SetTarget(attacker,now,delay);
        else firstAttackAt=Math.Min(firstAttackAt,now+delay);
        pursuitUntil=now+PursuitTimeout;
    }
    internal void Notice(WorldSession target,long now)=>SetTarget(target,now,AttackInterval);
    void SetTarget(WorldSession target,long now,int delay){
        Target=target;
        attackedTarget=false;
        firstAttackAt=now+delay;
        nextPursuitAt=now+PursuitInterval;
        pursuitUntil=now+PursuitTimeout;
    }
    internal void ClearTarget()=>Target=null;
    internal bool CanPursue(Character target,long now)=>(!IsTreasureChest||IsPirateChest)&&now<pursuitUntil&&WithinPursuitRange(target.X,target.Y);
    internal bool PursuitReady(long now){
        if(now<nextPursuitAt)return false;
        nextPursuitAt=now+PursuitInterval;
        return true;
    }
    internal void DelayAfterPursuit(long now){
        if(!CastsSpell)nextAttackAt=Math.Max(nextAttackAt,now+Math.Max(PursuitInterval,meleeIntervalMs));
    }
    public bool TryRespawn(uint id,Func<int,int,bool> available,Func<int,int,IEnumerable<(int X,int Y)>>? nearby=null){
        var candidates=spawnPoints.Where(point=>point!=spawnAnchor).ToArray();
        if(nearby!=null)Random.Shared.Shuffle(candidates);
        var point=nearby==null||spawnPoints.Length>1?FirstAvailable(candidates):null;
        if(point==null&&nearby!=null){
            var (x,y)=spawnPoints[0];
            var reachable=nearby(x,y).Prepend((x,y)).Where(tile=>tile!=spawnAnchor).ToArray();
            Random.Shared.Shuffle(reachable);
            point=FirstAvailable(reachable);
        }
        if(point==null&&available(spawnAnchor.X,spawnAnchor.Y))point=spawnAnchor;
        if(point is not {} chosen)return false;
        spawnAnchor=chosen;
        Appearance=Appearance with {Id=id,X=chosen.X,Y=chosen.Y};
        if(nearby!=null)RollLevel();
        Hp=maxHp;
        ClearTarget();
        nextAttackAt=0;
        nextRoamAt=0;
        return true;

        (int X,int Y)? FirstAvailable((int X,int Y)[] positions){
            foreach(var p in positions)if(available(p.X,p.Y))return p;
            return null;
        }
    }
    public bool RoamReady(long now){
        if(roamTick==0||Target!=null)return false;
        if(nextRoamAt==0){nextRoamAt=now+Random.Shared.Next(roamTick);return false;}
        if(now<nextRoamAt)return false;
        nextRoamAt=now+roamTick-(now-nextRoamAt)%roamTick;
        return true;
    }
    internal void ScheduleRoam(long at)=>nextRoamAt=at;
    public bool WantsStep()=>Random.Shared.Next(moveEvery)==0;
    int DistanceFromSpawn(int x,int y)=>Math.Max(Math.Abs(x-spawnAnchor.X),Math.Abs(y-spawnAnchor.Y));
    public bool WithinRoam(int x,int y)=>DistanceFromSpawn(x,y)<=RoamRadius;
    internal bool WithinPursuitRange(int x,int y)=>DistanceFromSpawn(x,y)<=PursuitRadius;
    public void Move(int x,int y,byte direction)=>Appearance=Appearance with {X=x,Y=y,Type=direction};
    internal bool AttackReady(long now){
        if(now<firstAttackAt||now<nextAttackAt)return false;
        // A slightly late field tick keeps the melee phase. Long gaps start a new interval.
        long phase=!CastsSpell&&nextAttackAt>0&&nextAttackAt>=firstAttackAt&&
            now-nextAttackAt<WorldSettings.FieldUpdateMs?nextAttackAt:now;
        nextAttackAt=phase+(CastsSpell?SpellInterval:meleeIntervalMs);
        attackedTarget=true;
        pursuitUntil=now+PursuitTimeout;
        return true;
    }
    public bool Face(int x,int y){
        var delta=(x-Appearance.X,y-Appearance.Y);
        for(byte direction=0;direction<8;direction++){
            if(LocalMap.Delta(direction)!=delta)continue;
            if(Appearance.Type==direction)return false;
            Appearance=Appearance with {Type=direction};
            return true;
        }
        return false;
    }
}
public sealed class TrainingDoll(FieldObject appearance,int? durability,int abilityLimit,bool durabilityUsesDamage=false){
    public FieldObject Appearance {get;}=appearance;
    int? remainingDurability=durability;
    public bool IsPermanent=>remainingDurability==null;
    public int AbilityLimit {get;}=abilityLimit;
    public bool Hit(ushort damage){
        if(remainingDurability is not int remaining||damage==0)return false;
        remainingDurability=Math.Max(0,remaining-(durabilityUsesDamage?damage:1));
        return remainingDurability==0;
    }
}
public sealed class WorldData {
    static readonly Dictionary<(int Map,int X,int Y,byte Floor),RecordedTrainingTarget> baseTrainingTargets=
        RecordedWorld.TrainingTargets.SelectMany(target=>Enumerable.Range(0,3)
            .Select(dimension=>target with {Map=target.Map+dimension*10000}))
        .ToDictionary(target=>(target.Map,target.X,target.Y,target.Floor));
    readonly string directory;
    readonly ConcurrentDictionary<int,LocalMap> maps=new();
    readonly Dictionary<uint,FieldObject> drops=new();
    readonly List<TrainingDoll> trainingDolls=[];
    readonly Dictionary<(int Map,int X,int Y,byte Floor),TrainingDoll> fixedTrainingDolls=[];
    IReadOnlyDictionary<(int Map,int X,int Y,byte Floor),RecordedTrainingTarget> trainingTargets=baseTrainingTargets;
    IEnumerable<TrainingDoll> TrainingDolls=>fixedTrainingDolls.Values.Concat(trainingDolls);
    readonly Dictionary<uint,(FieldObject Sign,uint Owner,long ExpiresAt,bool Open)> roomSigns=[];
    uint nextTrainingId=0x63000000;
    uint nextRoomSignId=0x64000000;
    uint nextMonsterId=0x61000008,nextDropId=0x62000000;
    // Sightings and loot types come from the recorded South Plain sessions.
    FieldMonster[] monsters=[
        new(new(0x61000001,5,163,10,1,17394,6,3,"수탉",0),20,["chicken-meat"],roamTick:1500,moveEvery:14),
        new(new(0x61000002,5,147,14,1,17393,6,7,"암탉",0),12,["egg","healing-potion-1ml"],roamTick:1600,moveEvery:8),
        // The south field guide identifies the bear as aggressive. Position and hit strength are local settings.
        new(new(0x61000003,5,178,7,1,0x47E6,6,3,"곰",0),140,[]),
        new(new(0x61000004,5,184,11,1,17395,6,0,"돼지",0),15,["recorded-8be1-b5c5c1f6b0edb1e228c1b6b0a229",RanicheQuest.WaterKey],roamTick:1500,moveEvery:8),
        new(new(0x61000005,5,183,41,1,17394,6,0,"수탉",0),20,["chicken-meat"],
            roamTick:1500,moveEvery:14,alternatives:[(204,22),(205,53),(176,63)]),
        new(new(0x61000006,5,183,42,1,17393,6,5,"암탉",0),12,["egg","healing-potion-1ml"],
            roamTick:1600,moveEvery:8,alternatives:[(167,66),(174,58)]),
        new(new(0x61000007,5,191,47,1,17396,6,3,"사슴",0),50,[],roamTick:900,moveEvery:2),
        new(new(0x61000008,5,197,40,1,17397,6,6,"푸푸",0),50,[],roamTick:1700,moveEvery:3)
    ];
    readonly LocalNpc[] baseNpcs;
    readonly LocalNpc[] atlasGuildFlags;
    readonly string? livePath;
    readonly bool loadRecordedMonsters;
    readonly HashSet<(int Map,int X,int Y,byte Floor)> registeredMonsterTiles=[];
    List<RecordedMonster> pendingMonsters=[];
    DateTime liveStamp;
    Dictionary<int,RecordedMap> liveMaps=[];
    Dictionary<(int,int,int,byte),StartLocation> livePortals=[];
    Dictionary<(int Source,int Destination),DoorArrivalProfile> liveDoorArrivals=[];
    Dictionary<(int Map,string Name,ushort Sprite),(RecordedLootRule Rule,int Weight)[]> liveMonsterLoot=[];
    Dictionary<(string Name,ushort Sprite),MeleeTable[]> liveMonsterMelee=[];

    sealed class MeleeTable(ushort armor,RecordedMeleeRule[] outcomes,int samples){
        readonly RecordedMeleeRule[] ranked=outcomes.OrderBy(outcome=>outcome.Damage).ToArray();
        internal ushort Armor {get;}=armor;

        internal RecordedMeleeRule Pick(double sample,bool byDamage=false){
            var order=byDamage?ranked:outcomes;
            int roll=(int)(sample*samples);
            foreach(var outcome in order){
                roll-=outcome.Samples.Length;
                if(roll<0)return outcome;
            }
            return order[^1];
        }
    }
    Dictionary<(string Name,ushort Sprite,ushort MagicDefense),ushort[]> liveMonsterSpellDamage=[];
    Dictionary<(string Name,ushort Sprite),ushort> liveKillExperience=[];
    public LocalNpc[] Npcs {get;private set;}
    internal RecordedClock Clock {get;private set;}=RecordedClock.Reference;
    public WorldData(string clientDirectory,string configPath,string? livePath=null,bool expandField=true){
        directory=Path.Combine(clientDirectory,"Maps");
        this.livePath=livePath;
        loadRecordedMonsters=expandField;
        baseNpcs=File.Exists(configPath)?JsonSerializer.Deserialize<LocalNpc[]>(File.ReadAllText(configPath))??[]:[];
        if(baseNpcs.Select(n=>n.Id).Distinct().Count()!=baseNpcs.Length)throw new InvalidDataException("Duplicate NPC ID");
        foreach(var npc in baseNpcs){
            if(npc.Id<0x60000000||npc.Category is not (1 or 2)||!GetMap(npc.Map).ContainsTile(npc.X,npc.Y,npc.Floor))
                throw new InvalidDataException("Invalid local NPC placement");
            if(!npc.HasValidShopCatalog)
                throw new InvalidDataException("Invalid extended shop entry ID or durability");
            foreach(var entry in npc.Shop??[])EquipmentItems.Get(entry.Key);
        }
        atlasGuildFlags=Enumerable.Range(0,10000).Where(WorldSettings.IsGuildHouse)
            .SelectMany(map=>Enumerable.Range(0,3).Select(dimension=>map+dimension*10000))
            .Select(AtlasGuildFlag).OfType<LocalNpc>().ToArray();
        Npcs=PopulateNpcs(baseNpcs);
        var southHuntSpawns=monsters.Where(m=>RaddHunt.Beginner.Any(hunt=>hunt.Counts(m.Appearance))).ToArray();
        monsters=[..monsters,..southHuntSpawns.SelectMany(m=>new[]{10005,20005}.Select(map=>
            m.AdditionalSpawn(m.Appearance with {Id=++nextMonsterId,Map=map})))];
        // The HP catalog takes precedence over the fallback for recorded and local spawns.
        monsters=[..monsters,..RecordedWorld.Monsters.Concat(RaddHunt.SewerSpawns).SelectMany(m=>m.Map is 4 or 9 or 232 or 241 or 258 or 1500 or 2004 or 2008 or 2500
            ?new[]{m,m with {Map=m.Map+10000},m with {Map=m.Map+20000}}:new[]{m}).Select(m=>new FieldMonster(
            new(++nextMonsterId,m.Map,m.X,m.Y,m.Floor,m.Sprite,m.Flags,m.Direction,m.Name,0),
            100,m.Name switch {
                "슬라임디"=>[KayGatheringQuest.MaterialKey],
                "큰쥐"=>[GatheringQuest.RatTailKey],
                "회색쥐"=>[GatheringQuest.RatEyeKey],
                "머맨"=>[GatheringQuest.ScaleKey],
                "좀비"=>[GatheringQuest.ZombieBoneKey],
                "이상한좀비"=>[OldSmithQuest.WaterKey],
                "유익인" or "유익인(이글)"=>[TeddyTailoring.WingKey],
                _=>SektiSmithing.MaterialLoot(m.Name)
            },roamTick:1500,moveEvery:8))];
        foreach(var monster in monsters){
            var o=monster.Appearance;
            registeredMonsterTiles.Add((o.Map,o.X,o.Y,o.Floor));
        }
        ReloadLive();
        ApplyRecordedTrainingTargets(_=>false);
        if(expandField){
            var occupied=monsters.Select(m=>(m.Appearance.Map,m.Appearance.X,m.Appearance.Y,m.Appearance.Floor))
                .Concat(Npcs.Select(n=>(n.Map,n.X,n.Y,n.Floor))).ToHashSet();
            foreach(var entry in pendingMonsters)
                if(TreasureChests.ObservedRespawnSeconds(entry.Map,entry.X,entry.Y,entry.Floor,entry.Sprite,entry.Name).HasValue)
                    occupied.Add((entry.Map,entry.X,entry.Y,entry.Floor));
            var extra=new List<FieldMonster>();
            bool recordedSouthPlain=pendingMonsters.Any(m=>m.Map==5);
            foreach(var monster in monsters){
                var origin=monster.Appearance;
                if(recordedSouthPlain&&origin.Map==5)continue;
                var map=GetMap(origin.Map);
                foreach(var (x,y) in NearbySpawnTiles(map,origin,origin.X,origin.Y)
                    .OrderByDescending(tile=>Math.Max(Math.Abs(tile.X-origin.X),Math.Abs(tile.Y-origin.Y)))){
                    if(!occupied.Add((origin.Map,x,y,origin.Floor)))continue;
                    extra.Add(monster.AdditionalSpawn(origin with {Id=++nextMonsterId,X=x,Y=y}));
                    break;
                }
            }
            monsters=[..monsters,..extra];
            foreach(var monster in monsters)monster.RollLevel();
        }
        AddRecordedMonsters(_=>false);
    }
    LocalNpc? AtlasGuildFlag(int map){
        if(!File.Exists(Path.Combine(directory,$"{map%10000:D5}.map"))||GetMap(map).GuildFlagTile() is not {} tile)return null;
        return new(0x64000000U+(uint)map,GuildHouseCapture.FlagName,map,tile.X,tile.Y,0,GuildHouseCapture.FlagSprite,0,2,GuildHouseCapture.FlagName);
    }
    LocalNpc[] PopulateNpcs(IEnumerable<LocalNpc> source){
        var npcs=RolanciaNpcDimensions.Populate(source).ToList();
        if(File.Exists(Path.Combine(directory,$"{MercenaryHiring.EpiloriaPubMap:D5}.map"))){
            for(int dimension=0;dimension<3;dimension++){
                int map=dimension*10000+MercenaryHiring.EpiloriaPubMap;
                int vendor=npcs.FindIndex(npc=>npc.Map==map&&npc.Name==MercenaryHiring.CopeName);
                if(vendor<0)npcs.Add(MercenaryHiring.PubMerchant(map));
                else if(npcs[vendor].Shop==null)
                    npcs[vendor]=npcs[vendor] with {Shop=[MercenaryHiring.SummonScrollOffer]};
            }
        }
        var recordedFlags=npcs.Where(GuildHouseCapture.IsFlag).ToLookup(npc=>npc.Map);
        var fallbacks=atlasGuildFlags.Where(flag=>!recordedFlags.Contains(flag.Map));
        return [..npcs,..fallbacks];
    }
    internal RecordedMap? ObservedMap(int id){
        RecordedMap? Exact(int map)=>liveMaps.GetValueOrDefault(map)??RecordedWorld.Maps.GetValueOrDefault(map);
        var exact=Exact(id);
        if(exact!=null||!RolanciaNpcDimensions.SharesMap(id))return exact;
        for(int source=0;source<3;source++){
            if(source==id/10000||Exact(source*10000+id%10000) is not {} original)continue;
            string prefix=$"[{(char)('A'+source)}]";
            return original with {Id=id,Name=original.Name.StartsWith(prefix,StringComparison.Ordinal)
                ?$"[{(char)('A'+id/10000)}]"+original.Name[3..]:original.Name};
        }
        return null;
    }
    internal bool BlocksFieldPlacement(int map){
        int localMap=map%10000;
        if(localMap is 2 or 1002 or 2002 or 3002 or 4002||WorldSettings.ShrineName(map)!=null)return true;
        var recorded=ObservedMap(map)??ObservedMap(localMap);
        return recorded!=null&&(recorded.Flags&4)==0;
    }
    internal bool ReloadLive(){
        if(livePath==null||!File.Exists(livePath))return false;
        try{
            var stamp=File.GetLastWriteTimeUtc(livePath);
            if(stamp==liveStamp)return false;
            string contents=File.ReadAllText(livePath);
            liveStamp=stamp;
            var data=JsonSerializer.Deserialize<LiveWorldData>(contents)
                ??throw new InvalidDataException("Empty recorded world snapshot");
            var maps=data.Maps.ToDictionary(m=>m.Id);
            var portals=data.Portals.ToDictionary(p=>(p.Map,p.X,p.Y,p.Floor),p=>p.Destination);
            var doorArrivals=(data.DoorArrivals??[]).Where(p=>p.Source is >=0 and <30000&&
                p.Destination is >=0 and <30000&&p.Source!=p.Destination&&p.Source/10000==p.Destination/10000&&
                (p.Source%10000<1000&&p.Destination%10000<1000||WorldSettings.IsGuildHouseDoor(p.Source,p.Destination))&&
                p.Track is null or >0 and <0x8000)
                .GroupBy(p=>(p.Source,p.Destination)).Where(g=>g.Select(p=>p.Track).Distinct().Count()==1)
                .ToDictionary(g=>g.Key,g=>new DoorArrivalProfile(
                    g.Select(p=>p.MapPadding).Distinct().Count()==1?g.First().MapPadding:null,g.First().Track,
                    g.Any(p=>p.OmitEmptyObjects),g.Any(p=>!string.IsNullOrEmpty(p.BuffStatusPacket)),
                    g.SelectMany(p=>p.OptionStatuses??[]).Where(p=>!string.IsNullOrEmpty(p.Packet)&&
                        p.Mask is 0x08 or 0x28&&p.Flags is 0x2823F or 0x282BF&&p.Direction<8)
                        .DistinctBy(p=>(p.Mask,p.Flags,p.Direction)).ToArray()));
            var items=data.Items.ToDictionary(i=>i.Key);
            var melee=(data.MonsterMelee??[]).Where(r=>!string.IsNullOrEmpty(r.Name)&&r.Sprite!=0&&r.Variant<=3&&
                r.Motion is 1 or 4&&r.Samples is {Length:>0}&&(r.Damage==0?r.StatusMask==0:r.StatusMask is 0x20 or 0x60))
                .GroupBy(r=>(r.Name,r.Sprite,r.Armor)).Select(g=>(g.Key,Outcomes:g.ToArray(),Samples:g.Sum(r=>r.Samples.Length)))
                // Zero-only samples still describe the strike animation and absence of an HP update.
                .Where(p=>p.Samples>=(p.Key is (TreasureChests.ChestName,TreasureChests.Pirate,_)?2:4))
                .GroupBy(p=>(p.Key.Name,p.Key.Sprite)).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.Key.Armor)
                    .Select(p=>new MeleeTable(p.Key.Armor,p.Outcomes,p.Samples)).ToArray());
            var spellDamage=(data.MonsterSpellDamage??[]).Where(r=>!string.IsNullOrEmpty(r.Name)&&r.Sprite!=0&&
                r.Damage>0&&r.Samples is {Length:>0}).GroupBy(r=>(r.Name,r.Sprite,r.MagicDefense))
                .Select(g=>(g.Key,Outcomes:g.SelectMany(r=>r.Samples.Select(id=>(id,r.Damage))).DistinctBy(s=>s.id)
                    .Select(s=>s.Damage).ToArray())).Where(p=>p.Outcomes.Length>=3).ToDictionary(p=>p.Key,p=>p.Outcomes);
            var experience=(data.MonsterKillExperience??[]).Where(r=>!string.IsNullOrEmpty(r.Name)&&r.Sprite!=0&&
                r.Experience>0&&r.Samples is {Length:>0}).GroupBy(r=>(r.Name,r.Sprite))
                .Where(g=>g.Select(r=>r.Experience).Distinct().Count()==1&&g.SelectMany(r=>r.Samples).Distinct().Count()>=2)
                .ToDictionary(g=>g.Key,g=>g.First().Experience);
            var npcs=baseNpcs.ToDictionary(n=>n.Id);
            foreach(var item in items.Values)
                if(item.AppearanceLayer is <-1 or >9||string.IsNullOrEmpty(item.Name)||item.Sprite==0||item.Flag is >1)
                    throw new InvalidDataException("Invalid recorded item");
            foreach(var ((map,x,y,floor),target) in portals)
                if(!GetMap(map).ContainsTile(x,y,floor)||!GetMap(target.Map).CanStand(target.X,target.Y,target.Floor))
                    throw new InvalidDataException("Invalid recorded portal");
            foreach(var rule in data.MonsterLoot??[]){
                if(rule.Map is <0 or >=10000||rule.Sprite==0||string.IsNullOrEmpty(rule.Name)||rule.Samples.Length==0||
                    rule.Drops.Length==0&&rule.ConfirmedSamples is not {Length:>0}||
                    (rule.ConfirmedSamples??[]).Except(rule.Samples).Any()||
                    rule.Drops.Any(drop=>drop.Quantity==0||(drop.Key!=WorldSettings.CurrencyKey&&
                        !items.ContainsKey(drop.Key)&&!EquipmentItems.HasKey(drop.Key))))
                    throw new InvalidDataException("Invalid recorded monster loot");
            }
            var loot=(data.MonsterLoot??[]).GroupBy(r=>(r.Map,r.Name,r.Sprite))
                .Select(g=>(g.Key,Outcomes:WeightLoot(g.ToArray()))).Where(g=>g.Outcomes.Length>0)
                .ToDictionary(g=>g.Key,g=>g.Outcomes);
            foreach(var npc in data.Npcs.ToDictionary(n=>n.Id).Values){
                if(npc.Id<0x60000000||npc.Category is not (1 or 2)||!GetMap(npc.Map).ContainsTile(npc.X,npc.Y,npc.Floor))
                    throw new InvalidDataException("Invalid recorded NPC");
                if(!npc.HasValidShopCatalog)throw new InvalidDataException("Invalid extended shop entry ID or durability");
                foreach(var entry in npc.Shop??[])
                    if(!items.ContainsKey(entry.Key)&&!EquipmentItems.HasKey(entry.Key))
                        throw new InvalidDataException("Unknown recorded shop item");
                npcs[npc.Id]=npcs.TryGetValue(npc.Id,out var baseline)&&baseline.Map==npc.Map&&
                    baseline.Name==npc.Name&&baseline.Sprite==npc.Sprite?RolanciaNpcDimensions.Fill(npc,[baseline]):npc;
            }
            var recordedTargets=(data.TrainingTargets??[]).ToDictionary(target=>(target.Map,target.X,target.Y,target.Floor));
            foreach(var target in recordedTargets.Values)
                if(!WorldSettings.IsArcherTrainingRoom(target.Map)||target.Floor!=0||target.Direction>=8||
                    !GetMap(target.Map).CanWalk(target.X,target.Y,target.Floor))
                    throw new InvalidDataException("Invalid recorded training target");
            var mergedTargets=new Dictionary<(int Map,int X,int Y,byte Floor),RecordedTrainingTarget>(baseTrainingTargets);
            foreach(var (tile,target) in recordedTargets)mergedTargets[tile]=target;
            var loadedNpcs=PopulateNpcs(npcs.Values);
            EquipmentItems.RegisterLive(items.Values);
            liveMaps=maps;
            livePortals=portals;
            liveDoorArrivals=doorArrivals;
            liveMonsterLoot=loot;
            liveMonsterMelee=melee;
            liveMonsterSpellDamage=spellDamage;
            liveKillExperience=experience;
            Npcs=loadedNpcs;
            trainingTargets=mergedTargets;
            if(data.Clock is {Valid:true} clock&&clock.ObservedAt>=Clock.ObservedAt.AddSeconds(-10))Clock=clock;
            pendingMonsters=(data.Monsters??[]).Where(m=>m.Map is >=0 and <=65535&&m.Sprite!=0&&m.Floor<4&&m.Direction<8&&
                !string.IsNullOrEmpty(m.Name)&&(loadRecordedMonsters||TreasureChests.IsChest(m.Name,m.Sprite))&&
                !registeredMonsterTiles.Contains((m.Map,m.X,m.Y,m.Floor))).ToList();
            Console.WriteLine($"Recorded world reloaded: {maps.Count} maps, {portals.Count} portals, {data.Npcs.Length} NPC updates, {items.Count} items, {recordedTargets.Count} training targets");
            return true;
        }catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or ArgumentException or NullReferenceException or UnauthorizedAccessException){
            Console.Error.WriteLine($"Recorded world reload: {ex.Message}");
            return false;
        }
    }
    internal FieldObject[] AddRecordedMonsters(Func<FieldObject,bool> playerAt){
        if(pendingMonsters.Count==0)return [];
        var occupied=monsters.Where(m=>m.Hp>0).Select(m=>(m.Appearance.Map,m.Appearance.X,m.Appearance.Y,m.Appearance.Floor))
            .Concat(Npcs.Select(n=>(n.Map,n.X,n.Y,n.Floor)))
            .Concat(TrainingDolls.Select(d=>(d.Appearance.Map,d.Appearance.X,d.Appearance.Y,d.Appearance.Floor))).ToHashSet();
        var added=new List<FieldMonster>();
        var waiting=new List<RecordedMonster>();
        var reserved=pendingMonsters.Select(m=>(m.Map,m.X,m.Y,m.Floor)).ToHashSet();
        foreach(var entry in pendingMonsters.OrderByDescending(m=>TreasureChests.IsChest(m.Name,m.Sprite))){
            var origin=(entry.Map,entry.X,entry.Y,entry.Floor);
            if(registeredMonsterTiles.Contains(origin))continue;
            LocalMap map;
            try{map=GetMap(entry.Map);}
            catch(Exception ex) when(ex is IOException or InvalidDataException){continue;}
            if(!map.ContainsTile(entry.X,entry.Y,entry.Floor))continue;
            var anchor=new FieldObject(0,entry.Map,entry.X,entry.Y,entry.Floor,entry.Sprite,entry.Flags,entry.Direction,entry.Name,0);
            IEnumerable<(int X,int Y)> candidates=TreasureChests.ObservedRespawnSeconds(anchor).HasValue
                ?[(entry.X,entry.Y)]:NearbySpawnTiles(map,anchor,entry.X,entry.Y).Prepend((entry.X,entry.Y));
            bool placed=false;
            foreach(var (x,y) in candidates){
                var at=(entry.Map,x,y,entry.Floor);
                var spawn=anchor with {X=x,Y=y};
                if((at!=origin&&reserved.Contains(at))||!IsMonsterSpawnTile(map,anchor,x,y)||occupied.Contains(at)||playerAt(spawn))continue;
                spawn=spawn with {Id=++nextMonsterId};
                var template=monsters.FirstOrDefault(m=>m.Appearance.Sprite==entry.Sprite&&m.Appearance.Name==entry.Name);
                var monster=template?.AdditionalSpawn(spawn)??(TreasureChests.IsChest(entry.Name,entry.Sprite)&&entry.Sprite==TreasureChests.Stationary
                    ?new(spawn,20,[]):new(spawn,100,SektiSmithing.MaterialLoot(entry.Name),roamTick:1500,moveEvery:8));
                monster.RollLevel();
                added.Add(monster);
                occupied.Add(at);
                registeredMonsterTiles.Add(origin);
                placed=true;
                break;
            }
            if(!placed)waiting.Add(entry);
        }
        pendingMonsters=waiting;
        if(added.Count>0)monsters=[..monsters,..added];
        return added.Select(m=>m.Appearance).ToArray();
    }
    public LocalMap GetMap(int id){
        id=WorldSettings.ClientMap(id);
        if(id>65535)throw new InvalidDataException("Invalid map ID");
        // Native 0047F311 maps IDs below 60000 to the region-independent file ID.
        int fileId=id<60000?id%10000:id;
        return maps.GetOrAdd(fileId,value=>new LocalMap(Path.Combine(directory,$"{value:D5}.map")));
    }
    public LocalNpc[] InMap(int id)=>Npcs.Where(n=>n.Map==id).ToArray();
    internal uint RepairBasePrice(int map,string key){
        // The 10% Tis Battle Axe quote is 22,500, consistent with 25% of its 100,000 catalog value.
        if(key==EquipmentItems.TisBattleAxeKey)return 25_000;
        var prices=Npcs.SelectMany(n=>(n.Shop??[]).Where(entry=>entry.Key==key&&entry.Price!=0)
            .Select(entry=>(Local:n.Map/10000==map/10000,entry.Price))).ToLookup(entry=>entry.Local,entry=>entry.Price);
        return prices[true].DefaultIfEmpty(prices[false].DefaultIfEmpty(100u).Min()).Min();
    }
    public FieldMonster? MonsterAt(int map,int x,int y,int floor)=>monsters.FirstOrDefault(m=>
        m.Hp>0&&m.Appearance.Map==map&&m.Appearance.X==x&&m.Appearance.Y==y&&m.Appearance.Floor==floor);
    internal FieldMonster? MonsterById(uint id)=>monsters.FirstOrDefault(m=>m.Hp>0&&m.Appearance.Id==id);
    public IEnumerable<FieldMonster> ActiveAttackers(IReadOnlySet<int> activeMaps)=>monsters.Where(m=>
        m.Hp>0&&(m.Target!=null||m.SightRange>0&&activeMaps.Contains(m.Appearance.Map)));
    internal ushort? MonsterExperience(FieldObject monster)=>liveKillExperience.TryGetValue((monster.Name,monster.Sprite),out ushort reward)
        ?reward:RecordedWorld.MonsterExperience(monster.Name);
    internal MonsterMelee RollMonsterMelee(FieldMonster monster,Character target,double sample,double? roundingSample=null){
        static MonsterMelee FromSample(RecordedMeleeRule outcome,ushort damage)=>new(damage,outcome.Variant,outcome.Motion,
            outcome.StatusMask,outcome.MotionPadded??true,outcome.StatusPadded??true);

        var at=monster.Appearance;
        ushort armor=EquipmentItems.PhysicalDefense(EquipmentItems.EquippedEffects(target));
        if(liveMonsterMelee.TryGetValue((at.Name,at.Sprite),out var tables)){
            MeleeTable? lower=null;
            foreach(var table in tables){
                if(armor==table.Armor){
                    var outcome=table.Pick(sample);
                    return FromSample(outcome,outcome.Damage);
                }
                if(armor<table.Armor){
                    if(lower==null)break;
                    double fraction=(double)(armor-lower.Armor)/(table.Armor-lower.Armor);
                    var lo=lower.Pick(sample,byDamage:true);
                    var hi=table.Pick(sample,byDamage:true);
                    // Blend equal percentiles. Stochastic rounding preserves the miss rate between 0 and 1.
                    double estimate=lo.Damage+(hi.Damage-lo.Damage)*fraction;
                    ushort damage=(ushort)Math.Floor(estimate+(roundingSample??Random.Shared.NextDouble()));
                    var nearer=fraction<0.5?lo:hi;
                    var other=fraction<0.5?hi:lo;
                    return FromSample((damage==0)==(nearer.Damage==0)?nearer:other,damage);
                }
                lower=table;
            }
        }
        if(monster.IsPirateChest&&armor>0)return new(0);
        return new(monster.ContactDamage);
    }
    internal ushort RollMonsterSpellDamage(FieldMonster monster,Character target,double sample){
        var at=monster.Appearance;
        ushort defense=EquipmentItems.MagicDefense(EquipmentItems.EquippedEffects(target));
        return liveMonsterSpellDamage.TryGetValue((at.Name,at.Sprite,defense),out var outcomes)
            ?outcomes[(int)(sample*outcomes.Length)]:(ushort)(sample<.5?2:3);
    }
    public IEnumerable<FieldObject> FieldObjects(int map)=>
        monsters.Where(m=>m.Hp>0&&m.Appearance.Map==map).Select(m=>m.Appearance)
            .Concat(drops.Values.Where(o=>o.Map==map))
            .Concat(TrainingDolls.Select(d=>d.Appearance).Where(o=>o.Map==map))
            .Concat(roomSigns.Values.Select(entry=>entry.Sign).Where(o=>o.Map==map));
    internal bool HasRoomSign(int map,int x,int y,byte floor)=>
        roomSigns.Values.Any(entry=>entry.Sign is var sign&&sign.Map==map&&sign.X==x&&sign.Y==y&&sign.Floor==floor);
    internal (FieldObject Sign,uint Owner,ushort Minutes,bool Open)? InspectRoomSign(uint id,long now){
        if(!roomSigns.TryGetValue(id,out var entry)||now>=entry.ExpiresAt)return null;
        return (entry.Sign,entry.Owner,(ushort)((entry.ExpiresAt-now+59_999)/60_000),entry.Open);
    }
    internal FieldObject PlaceRoomSign(Character owner,int x,int y,PersonalRoomSign kind,long now){
        var sign=new FieldObject(++nextRoomSignId,owner.Map,x,y,owner.Floor,kind.Sprite,0x0B,2,$"{owner.Name}의개인방팻말",6);
        roomSigns.Add(sign.Id,(sign,owner.Id,now+kind.Minutes*60_000L,true));
        return sign;
    }
    internal FieldObject? RemoveRoomSign(uint id,uint owner){
        if(!roomSigns.TryGetValue(id,out var entry)||entry.Owner!=owner)return null;
        roomSigns.Remove(id);
        return entry.Sign;
    }
    internal FieldObject? SetRoomSignInvitation(uint id,uint owner,string invitation){
        if(!roomSigns.TryGetValue(id,out var entry)||entry.Owner!=owner||entry.Sign.Invitation==invitation)return null;
        var sign=entry.Sign with {Invitation=invitation};
        roomSigns[id]=(sign,entry.Owner,entry.ExpiresAt,entry.Open);
        return sign;
    }
    internal void ToggleRoomSign(uint id,uint owner){
        if(roomSigns.TryGetValue(id,out var entry)&&entry.Owner==owner)
            roomSigns[id]=(entry.Sign,entry.Owner,entry.ExpiresAt,!entry.Open);
    }
    internal IEnumerable<FieldObject> ExpireRoomSigns(long now){
        foreach(var entry in roomSigns.Values.Where(entry=>now>=entry.ExpiresAt).ToArray()){
            roomSigns.Remove(entry.Sign.Id);
            yield return entry.Sign;
        }
    }
    internal List<(FieldObject? Previous,FieldObject? Current)> ApplyRecordedTrainingTargets(Func<FieldObject,bool> playerAt){
        var changes=new List<(FieldObject?,FieldObject?)>();
        foreach(var tile in fixedTrainingDolls.Keys.Where(tile=>!trainingTargets.ContainsKey(tile)).ToArray()){
            var previous=fixedTrainingDolls[tile].Appearance;
            fixedTrainingDolls.Remove(tile);
            changes.Add((previous,null));
        }
        foreach(var (tile,target) in trainingTargets){
            var previous=fixedTrainingDolls.GetValueOrDefault(tile)?.Appearance;
            if(previous?.Type==target.Direction)continue;
            var current=previous is {} existing?existing with {Type=target.Direction}:
                new FieldObject(nextTrainingId+1,target.Map,target.X,target.Y,target.Floor,0x400A,6,target.Direction,"수련인형",0);
            if(previous==null){
                if(IsOccupied(target.Map,target.X,target.Y,target.Floor)||playerAt(current))continue;
                nextTrainingId++;
            }
            fixedTrainingDolls[tile]=new(current,null,0);
            changes.Add((previous,current));
        }
        return changes;
    }
    public TrainingDoll? TrainingDollAt(int map,int x,int y,int floor)=>
        trainingDolls.LastOrDefault(d=>d.Appearance is var o&&o.Map==map&&o.X==x&&o.Y==y&&o.Floor==floor)??
        (floor is >=0 and <=byte.MaxValue?fixedTrainingDolls.GetValueOrDefault((map,x,y,(byte)floor)):null);
    internal TrainingDoll? TrainingDollById(uint id)=>TrainingDolls.FirstOrDefault(d=>d.Appearance.Id==id);
    public void RemoveTrainingDoll(TrainingDoll doll)=>trainingDolls.Remove(doll);
    public FieldObject PlaceTrainingDoll(Character owner,int x,int y,ushort sprite,int durability,int abilityLimit,bool durabilityUsesDamage){
        // The four cardinal and four diagonal directions each form their own cycle.
        var doll=new FieldObject(++nextTrainingId,owner.Map,x,y,owner.Floor,sprite,6,
            (byte)(owner.Direction^2),$"{owner.Name}의수련인형",0);
        trainingDolls.Add(new(doll,durability,abilityLimit,durabilityUsesDamage));
        return doll;
    }
    internal FieldMonster PlaceFightingRooster(Character owner,int x,int y){
        var appearance=new FieldObject(++nextMonsterId,owner.Map,x,y,owner.Floor,FieldObject.FightingRoosterSprite,6,0,
            $"{owner.Name}의싸움닭",0);
        var rooster=new FieldMonster(appearance,30,[],roamTick:1500,respawns:false);
        rooster.ScheduleRoam(long.MaxValue);
        monsters=[..monsters,rooster];
        return rooster;
    }
    internal static (RecordedLootRule Rule,int Weight)[] WeightLoot(RecordedLootRule[] rules){
        // Use a single cohort for the zero-drop rate and all item outcomes.
        bool confirmed=rules.Sum(rule=>rule.ConfirmedSamples?.Length??0)>=4&&
            rules.Any(rule=>rule.Drops.Length==0&&rule.ConfirmedSamples is {Length:>0})&&
            rules.Any(rule=>rule.Drops.Length>0&&rule.ConfirmedSamples is {Length:>0});
        return rules.Select(rule=>(Rule:rule,Weight:confirmed?rule.ConfirmedSamples?.Length??0:
            rule.Drops.Length==0?0:rule.Samples.Length)).Where(outcome=>outcome.Weight>0).ToArray();
    }
    public FieldObject[] DropLoot(FieldMonster monster){
        var at=monster.Appearance;
        IEnumerable<(string Key,uint Quantity)> loot=monster.IsTreasureChest
            ?TreasureChests.Loot(at.Sprite):monster.Loot.Select(key=>(key,1u));
        (RecordedLootRule Rule,int Weight)[]? rules=null;
        if(!monster.IsTreasureChest&&liveMonsterLoot.TryGetValue((at.Map%10000,at.Name,at.Sprite),out rules)){
            int roll=Random.Shared.Next(rules.Sum(outcome=>outcome.Weight));
            foreach(var outcome in rules){
                roll-=outcome.Weight;
                if(roll>=0)continue;
                loot=outcome.Rule.Drops.Select(drop=>(drop.Key,drop.Quantity));
                break;
            }
        }
        if(!monster.IsTreasureChest&&rules==null&&(at.Map%10000 is >=3 and <=10 or 232 or 241 or 258)&&
            RecordedWorld.RolanciaDropPools.TryGetValue((at.Name,at.Sprite),out var pool)){
            var candidates=pool.Except(monster.Loot).ToArray();
            // Published listings identify the items, while recorded rolls
            // take precedence on maps where the recorder captured them.
            if(candidates.Length>0&&(!loot.Any()||Random.Shared.Next(8)==0)){
                var regular=candidates.Where(key=>EquipmentItems.Get(key).EquipmentSlot==0).ToArray();
                var gear=candidates.Where(key=>EquipmentItems.Get(key).EquipmentSlot!=0).ToArray();
                var choices=regular.Length==0||gear.Length>0&&Random.Shared.Next(6)==0?gear:regular;
                loot=loot.Append((choices[Random.Shared.Next(choices.Length)],1u));
            }
        }
        return loot.Select(item=>AddDrop(new(NextDropId(),at.Map,at.X,at.Y,at.Floor,item.Key,item.Quantity,null))).ToArray();
    }
    internal FieldObject? TryReleaseChestZombie(FieldMonster chest,int roll){
        if(roll!=0||chest.Hp!=0||!chest.IsTreasureChest||chest.IsPirateChest||chest.ReleasedZombie is {Hp:>0})return null;
        var spawn=chest.Appearance with {Id=++nextMonsterId,Sprite=TreasureChests.ZombieSprite,Type=0,Name=TreasureChests.ZombieName};
        var zombie=new FieldMonster(spawn,120,[],roamTick:1500,moveEvery:8,respawns:false);
        zombie.RollLevel();
        chest.ReleasedZombie=zombie;
        monsters=[..monsters,zombie];
        return spawn;
    }
    internal void RemoveDefeatedTemporaryMonster(FieldMonster monster){
        if(!monster.Respawns)monsters=monsters.Where(m=>m!=monster).ToArray();
    }
    internal uint NextDropId()=>++nextDropId;
    internal FieldObject AddDrop(GroundItem item,uint ownerId=0){
        var equipment=item.Key==WorldSettings.CurrencyKey?null:EquipmentItems.Get(item.Key);
        var drop=new FieldObject(item.Id,item.Map,item.X,item.Y,item.Floor,equipment?.Sprite??0x8BB8,0,0,
            equipment?.Name??"GALRID",3,item.Quantity,item.Key,item.Durability,ownerId,item.BaitRemaining){
            ExpiresAt=item.ExpiresAt,NativeDurability=item.NativeDurability};
        drops.Add(drop.Id,drop);
        nextDropId=Math.Max(nextDropId,drop.Id);
        return drop;
    }
    public FieldObject? DropAt(int map,int x,int y,byte floor)=>
        drops.Values.Where(o=>o.Map==map&&o.X==x&&o.Y==y&&o.Floor==floor).MaxBy(o=>o.Id);
    internal FieldObject[] ExpiredDrops(long now)=>drops.Values.Where(drop=>drop.ExpiresAt is long deadline&&deadline<=now).ToArray();
    public void RemoveDrop(uint id)=>drops.Remove(id);
    internal void UpdateDrop(FieldObject drop)=>drops[drop.Id]=drop;
    public IEnumerable<FieldObject> RespawnReady(long now,Func<FieldObject,bool> playerAt,bool varyLocation=true){
        foreach(var monster in monsters){
            var at=monster.Appearance;
            if(monster.Hp>0||now<monster.RespawnAt)continue;
            var map=GetMap(at.Map);
            if(!monster.TryRespawn(nextMonsterId+1,(x,y)=>IsMonsterSpawnTile(map,at,x,y)
                &&!IsOccupied(at.Map,x,y,at.Floor)&&!playerAt(at with {X=x,Y=y}),
                varyLocation&&!(monster.IsTreasureChest&&at.Sprite==TreasureChests.Stationary)?
                    (x,y)=>NearbySpawnTiles(map,at,x,y):null))continue;
            nextMonsterId++;
            yield return monster.Appearance;
        }
    }
    IEnumerable<(int X,int Y)> NearbySpawnTiles(LocalMap map,FieldObject at,int x,int y){
        var visited=new HashSet<(int,int)>{(x,y)};
        var queue=new Queue<(int X,int Y,int Steps)>();
        queue.Enqueue((x,y,0));
        while(queue.TryDequeue(out var tile)){
            if(tile.Steps==3)continue;
            for(byte direction=0;direction<8;direction++){
                if(!map.TryStep(tile.X,tile.Y,at.Floor,direction,out var next)||next.Floor!=at.Floor||
                    !IsMonsterSpawnTile(map,at,next.X,next.Y)||
                    !visited.Add((next.X,next.Y)))continue;
                queue.Enqueue((next.X,next.Y,tile.Steps+1));
                yield return (next.X,next.Y);
            }
        }
    }
    bool IsMonsterSpawnTile(LocalMap map,FieldObject at,int x,int y)=>
        (map.CanWalk(x,y,at.Floor)||(at.Map%10000 is 232 or 258)&&map.IsOpenDungeonFloor(x,y,at.Floor))&&
        StepDestination(at.Map,x,y,at.Floor)==null&&!IsPortalArrival(at.Map,x,y,at.Floor);
    bool IsPortalArrival(int map,int x,int y,byte floor){
        if(map is >=0 and <30000){
            if((map%10000,y,floor) is (5,7,1) or (2,242,1))return true;
            if(x is 13 or 236||y is 7 or 242){
                for(byte direction=0;direction<4;direction++){
                    var (dx,dy)=LocalMap.Delta(direction);
                    if(RolanciaBoundary(map,x+dx,y+dy,floor,direction)!=null)return true;
                }
            }
        }
        var tile=new StartLocation(map,x,y,floor);
        if(RecordedWorld.Portals.Values.Contains(tile)||livePortals.Values.Contains(tile))return true;
        if(map<10000||map%10000!=232)return false;
        var original=tile with {Map=map%10000};
        return RecordedWorld.Portals.Values.Contains(original)||livePortals.Values.Contains(original);
    }
    internal bool TryPursue(FieldMonster monster,Character target,Func<FieldObject,bool> playerAt){
        var at=monster.Appearance;
        if(target.Map!=at.Map||target.Floor!=at.Floor||!monster.WithinPursuitRange(target.X,target.Y))return false;
        var map=GetMap(at.Map);
        var visited=new HashSet<(int,int)>{(at.X,at.Y),(target.X,target.Y)};
        var queue=new Queue<(int X,int Y,byte FirstDirection)>();
        queue.Enqueue((at.X,at.Y,byte.MaxValue));
        while(queue.TryDequeue(out var tile)){
            for(byte direction=0;direction<8;direction++){
                if(!map.TryStep(tile.X,tile.Y,at.Floor,direction,out var next)||next.Floor!=at.Floor||
                    !visited.Add((next.X,next.Y))||!monster.WithinPursuitRange(next.X,next.Y)||
                    !IsMonsterSpawnTile(map,at,next.X,next.Y)||IsOccupied(at.Map,next.X,next.Y,at.Floor)||
                    playerAt(at with {X=next.X,Y=next.Y}))continue;
                byte first=tile.FirstDirection==byte.MaxValue?direction:tile.FirstDirection;
                int dxToTarget=Math.Abs(next.X-target.X),dyToTarget=Math.Abs(next.Y-target.Y);
                int distance=monster.CastsSpell?Math.Max(dxToTarget,dyToTarget):dxToTarget+dyToTarget;
                if(distance<=monster.PreferredAttackRange){
                    var (dx,dy)=LocalMap.Delta(first);
                    monster.Move(at.X+dx,at.Y+dy,first);
                    return true;
                }
                queue.Enqueue((next.X,next.Y,first));
            }
        }
        return false;
    }
    public IEnumerable<(FieldObject Previous,FieldObject Current)> Wander(long now,Func<FieldObject,bool> playerAt,IReadOnlySet<int>? activeMaps=null){
        foreach(var monster in monsters){
            if((activeMaps!=null&&!activeMaps.Contains(monster.Appearance.Map))||monster.Hp==0||!monster.RoamReady(now))continue;
            var at=monster.Appearance;
            if(at.IsFightingRooster)TryFightingRoosterStep(monster,playerAt,first:false);
            else{
                byte direction=(byte)Random.Shared.Next(8);
                if((!monster.WantsStep()||!TryRoamStep(monster,playerAt,direction))&&at.Type!=direction)
                    monster.Move(at.X,at.Y,direction);
            }
            if(monster.Appearance!=at)yield return (at,monster.Appearance);
        }
    }
    internal bool TryFightingRoosterStep(FieldMonster rooster,Func<FieldObject,bool> playerAt,bool first){
        int start=first||Random.Shared.Next(4)==0?Random.Shared.Next(8):rooster.Appearance.Type;
        for(int i=0;i<8;i++)
            if(TryRoamStep(rooster,playerAt,(byte)((start+i)%8)))return true;
        return false;
    }
    bool TryRoamStep(FieldMonster monster,Func<FieldObject,bool> playerAt,byte direction){
        var at=monster.Appearance;
        if(!GetMap(at.Map).TryStep(at.X,at.Y,at.Floor,direction,out var next)||next.Floor!=at.Floor||
            !monster.WithinRoam(next.X,next.Y)||IsPortalArrival(at.Map,next.X,next.Y,next.Floor)||
            IsOccupied(at.Map,next.X,next.Y,next.Floor)||playerAt(at with {X=next.X,Y=next.Y}))return false;
        monster.Move(next.X,next.Y,direction);
        return true;
    }
    public bool IsOccupied(int map,int x,int y,int floor,bool ignoreTrainingDolls=false)=>
        Npcs.Any(n=>n.Map==map&&n.X==x&&n.Y==y&&n.Floor==floor)||MonsterAt(map,x,y,floor)!=null||
        !ignoreTrainingDolls&&TrainingDollAt(map,x,y,floor)!=null;
    internal bool TryKnockbackTile(int mapId,int x,int y,byte floor,byte direction,out (int X,int Y,byte Floor) next){
        var map=GetMap(mapId);
        return map.TryStep(x,y,floor,direction,out next)&&next.Floor==floor&&
            (map.CanWalk(next.X,next.Y,floor)||map.IsOpenDungeonFloor(next.X,next.Y,floor))&&
            StepDestination(mapId,next.X,next.Y,floor)==null&&!IsPortalArrival(mapId,next.X,next.Y,floor)&&
            !IsOccupied(mapId,next.X,next.Y,floor);
    }
    internal static bool IsSouthGate(int source,int destination)=>source is >=0 and <30000&&source/10000==destination/10000&&
        (source%10000,destination%10000) is (2,5) or (5,2);
    internal static bool UsesRecordedDoorArrival(int source,int destination)=>IsArcherTrainingDoor(source,destination)||
        source/10000==destination/10000&&
        (source%10000,destination%10000) is
            (2,3 or 4 or 202 or 205 or 206 or 208 or 209 or 210 or 213 or 214 or 215 or 216 or 218 or 219 or 229) or
            (3 or 4 or 202 or 205 or 208 or 209 or 210 or 213 or 214 or 215 or 216 or 218 or 219 or 229,2) or
            (2,203) or (203,2) or
            (1002,1209) or (1209,1002) or
            (4,7) or (7,4) or (5,10) or (10,5 or 256) or (256,10);
    internal static bool IsArcherTrainingDoor(int source,int destination)=>source/10000==destination/10000&&
        (source%10000,destination%10000) is (202,297) or (297,202) or (297,298) or (298,297) or (298,299) or (299,298);
    internal DoorArrivalProfile? DoorArrival(int source,int destination)=>liveDoorArrivals.GetValueOrDefault((source,destination));
    internal StartLocation? AvailableDoorArrival(StartLocation desired,Func<int,int,byte,bool> playerAt){
        if(!playerAt(desired.X,desired.Y,desired.Floor))return desired;
        var map=GetMap(desired.Map);
        for(byte step=0;step<4;step++){
            if(!map.TryStep(desired.X,desired.Y,desired.Floor,step,out var next)||next.Floor!=desired.Floor||
                StepDestination(desired.Map,next.X,next.Y,next.Floor)!=null||
                IsOccupied(desired.Map,next.X,next.Y,next.Floor)||playerAt(next.X,next.Y,next.Floor))continue;
            return desired with {X=next.X,Y=next.Y};
        }
        return null;
    }
    public StartLocation? StepDestination(int map,int x,int y,byte floor,byte? direction=null){
        // The captured crossings preserve X. Use the paired map tiles to extend
        // the gate across its walkable spans, including diagonal approaches.
        var delta=direction is byte step?LocalMap.Delta(step):(0,0);
        int baseMap=map%10000,domain=map-baseMap;
        if(map is >=0 and <30000&&(baseMap,y,floor) is (2,243,1) or (5,6,1) &&
            (direction==null||(baseMap==2?delta.Item2>0:delta.Item2<0))){
            if(domain!=0&&ObservedPortal(map,x,y,floor) is {} recorded)return recorded;
            var gate=baseMap==2?new StartLocation(domain+5,x,7,1):new StartLocation(domain+2,x,242,1);
            if(GetMap(map).CanStand(x,y,floor)&&GetMap(gate.Map).CanStand(gate.X,gate.Y,gate.Floor))
                return gate;
        }
        if(ObservedPortal(map,x,y,floor) is {} exact)return exact;
        // The bank has two separate two-tile doors. Keep inferred lanes behind recorded crossings.
        var bankDoor=(map,x,y,floor) switch {
            (1002,67,49,1)=>new StartLocation(1209,48,31,0),
            (1209,47,32,0)=>new StartLocation(1002,66,50,1),
            (1209,28,22,0)=>new StartLocation(1002,58,53,0),
            _=>null
        };
        if(bankDoor!=null&&GetMap(map).CanWalk(x,y,floor)&&
            GetMap(bankDoor.Map).CanWalk(bankDoor.X,bankDoor.Y,bankDoor.Floor))return bankDoor;
        if(map is >=0 and <30000&&x is 4 or 5&&floor==0){
            var trainingDoor=(baseMap,y) switch {
                (202,3) when direction==null||delta.Item2<0=>new StartLocation(domain+297,x,28,0),
                (297 or 298,3) when direction==null||delta.Item2<0=>new StartLocation(map+1,x,28,0),
                (297,29) when direction==null||delta.Item2>0=>new StartLocation(domain+202,x,4,0),
                (298 or 299,29) when direction==null||delta.Item2>0=>new StartLocation(map-1,x,4,0),
                _=>null
            };
            if(trainingDoor!=null&&GetMap(map).CanWalk(x,y,floor)&&
                GetMap(trainingDoor.Map).CanWalk(trainingDoor.X,trainingDoor.Y,trainingDoor.Floor))
                return trainingDoor;
        }
        if(map is >=0 and <30000){
            // Main castle doors, west branch of the upper hall, and throne-room carpet.
            var castle=(baseMap,x,y,floor) switch {
                (3,>=123 and <=126,156,0) when direction==null||delta.Item2<0=>new StartLocation(domain+266,18,36,1),
                (266,>=17 and <=19,37,1) when direction==null||delta.Item2>0=>new StartLocation(domain+3,124,157,0),
                (266,>=26 and <=28,6,1) when direction==null||delta.Item2<0=>new StartLocation(domain+269,8,45,1),
                (269,>=7 and <=9,46,1) when direction==null||delta.Item2>0=>new StartLocation(domain+266,27,7,1),
                (269,>=62 and <=67,51,1) when direction==null||delta.Item2<0=>new StartLocation(domain+RanicheQuest.CourtMap,26,46,0),
                (RanicheQuest.CourtMap,>=22 and <=30,47,0) when direction==null||delta.Item2>0=>new StartLocation(domain+269,64,52,1),
                _=>null
            };
            if(castle!=null&&GetMap(map).CanStand(x,y,floor)&&GetMap(castle.Map).CanStand(castle.X,castle.Y,castle.Floor))
                return castle;
        }
        if(WestFieldGate(map,x,y,floor,direction) is {} westField)return westField;
        if(map is >=0 and <30000){
            int dimension=map/10000*10000;
            var cave=(map%10000,x,y,floor) switch {
                (2,>=47 and <=50,71,1) when direction==null||delta.Item2<0=>new StartLocation(dimension+242,x-19,29,0),
                (242,>=28 and <=31,30,0) when direction==null||delta.Item2>0=>new StartLocation(dimension+2,x+19,72,1),
                (242,>=24 and <=26,8,0) when direction==null||delta.Item2<0=>new StartLocation(dimension+241,74,225,0),
                (241,>=73 and <=75,226,0) when direction==null||delta.Item2>0=>new StartLocation(dimension+242,25,9,0),
                _=>null
            };
            if(cave!=null&&GetMap(map).CanStand(x,y,floor)&&GetMap(cave.Map).CanStand(cave.X,cave.Y,cave.Floor))
                return cave;
        }
        if(RolanciaBoundary(map,x,y,floor,direction) is {} boundary)return boundary;
        if(map is >=0 and <30000&&baseMap is >=200 and <=229&&(direction==null||delta.Item2>0)&&
            PairedRolanciaExit(map,x,y,floor) is {} pairedExit)return pairedExit;
        // The inn's two doorways have matching left and right tiles at both ends.
        var inn=(map%10000,x,y,floor) switch {
            (2,143 or 144,175 or 176,1)=>new StartLocation(domain+210,x-124,43,0),
            (2,165 or 166,175 or 176,1)=>new StartLocation(domain+210,x-86,43,0),
            (210,19 or 20,44,0)=>new StartLocation(domain+2,x+124,176,1),
            (210,79 or 80,44,0)=>new StartLocation(domain+2,x+86,176,1),
            _=>null
        };
        if(inn!=null&&GetMap(map).CanStand(x,y,floor)&&GetMap(inn.Map).CanStand(inn.X,inn.Y,inn.Floor))return inn;
        if(map is >=0 and <30000&&baseMap==2&&GetMap(map).PairedRolanciaDoor(x,y,floor) is int adjacent&&
            ObservedPortal(map,adjacent,y,floor) is {} entrance){
            var matchingSide=entrance with {X=entrance.X+x-adjacent};
            if(entrance.Map/10000==map/10000&&entrance.Map%10000 is >=200 and <=229&&
                GetMap(entrance.Map).PairedIndoorExit(entrance.X,entrance.Y+1,entrance.Floor)==matchingSide.X){
                var returnOnThisSide=ObservedPortal(entrance.Map,matchingSide.X,matchingSide.Y+1,matchingSide.Floor);
                if(returnOnThisSide==new StartLocation(map,x,y+1,floor)||
                    returnOnThisSide==null&&ObservedPortal(entrance.Map,entrance.X,entrance.Y+1,entrance.Floor)==
                        new StartLocation(map,adjacent,y+1,floor))
                    return matchingSide;
            }
            return entrance;
        }
        return null;
    }
    StartLocation? PairedRolanciaExit(int map,int x,int y,byte floor){
        if(GetMap(map).PairedIndoorExit(x,y,floor) is not int adjacent||
            ObservedPortal(map,adjacent,y,floor) is not {} anchor||anchor.Map!=map/10000*10000+2)
            return null;
        int townX=anchor.X+x-adjacent;
        if(GetMap(anchor.Map).PairedRolanciaDoor(anchor.X,anchor.Y,anchor.Floor)!=townX)return null;
        var entrance=ObservedPortal(anchor.Map,townX,anchor.Y-1,anchor.Floor);
        bool matches=entrance!=null?entrance==new StartLocation(map,x,y-1,floor):
            ObservedPortal(anchor.Map,anchor.X,anchor.Y-1,anchor.Floor)==new StartLocation(map,adjacent,y-1,floor);
        return matches?anchor with {X=townX}:null;
    }
    StartLocation? WestFieldGate(int map,int x,int y,byte floor,byte? direction){
        if(map is <0 or >=30000||floor>1)return null;
        int dy=direction is byte step?LocalMap.Delta(step).Y:0;
        var gate=(map%10000,y) switch {
            (8,243) when direction==null||dy>0=>new StartLocation(map+1,x,7,floor),
            (9,6) when direction==null||dy<0=>new StartLocation(map-1,x,242,floor),
            _=>null
        };
        if(gate==null)return null;
        int approach=y==243?242:7;
        int opposite=y==243?6:243;
        var from=GetMap(map);
        var to=GetMap(gate.Map);
        return from.CanStand(x,y,floor)&&from.CanStand(x,approach,floor)&&
            to.CanStand(x,opposite,floor)&&to.CanStand(x,gate.Y,floor)?gate:null;
    }
    StartLocation? RolanciaBoundary(int map,int x,int y,byte floor,byte? direction){
        if(map is <0 or >=30000)return null;
        // Paired atlas tiles extend each observed crossing along its connected span.
        (StartLocation? Gate,int Dx,int Dy) edge=(map%10000,x,y,floor) switch {
            (2,237,_,1)=>(new(map+2,13,y,floor),1,0),
            (4,12,_,1)=>(new(map-2,236,y,floor),-1,0),
            (2,>=123 and <=126,6,1)=>(new(map+1,x,242,floor),0,-1),
            (3,>=123 and <=126,243,1)=>(new(map-1,x,7,floor),0,1),
            (4,>=164 and <=176,243,2)=>(new(map+3,x,7,floor),0,1),
            (7,>=164 and <=176,6,2)=>(new(map-3,x,242,floor),0,-1),
            (5,12,>=158 and <=216,1)=>(new(map+5,236,y,floor),-1,0),
            (10,237,>=158 and <=216,1)=>(new(map-5,13,y,floor),1,0),
            _=>(null,0,0)
        };
        if(edge.Gate is not {} gate)return null;
        if(direction is byte step){
            var (dx,dy)=LocalMap.Delta(step);
            if(dx*edge.Dx+dy*edge.Dy<=0)return null;
        }
        var sourceMap=GetMap(map);
        var targetMap=GetMap(gate.Map);
        return sourceMap.CanStand(x,y,floor)&&sourceMap.CanStand(x-edge.Dx,y-edge.Dy,floor)&&
            targetMap.CanStand(gate.X,gate.Y,floor)&&targetMap.CanStand(gate.X-edge.Dx,gate.Y-edge.Dy,floor)?gate:null;
    }
    StartLocation? ObservedPortal(int map,int x,int y,byte floor){
        StartLocation? Exact(int id)=>livePortals.GetValueOrDefault((id,x,y,floor))??
            RecordedWorld.Portals.GetValueOrDefault((id,x,y,floor));
        if(Exact(map) is {} exact)return exact;
        if(!RolanciaNpcDimensions.SharesMap(map))return null;
        int dimension=map/10000;
        for(int source=0;source<3;source++){
            if(source==dimension||Exact(source*10000+map%10000) is not {} original)continue;
            if(original.Map/10000==source&&RolanciaNpcDimensions.SharesMap(original.Map))
                return original with {Map=dimension*10000+original.Map%10000};
        }
        return null;
    }
}

public sealed class WorldSession(Character character,Func<byte[],Task> send,Func<List<byte[]>,Task>? sendBatch=null) {
    internal const ushort ComboSeconds=60;
    const int HealthRecoveryMs=6_000, ManaRecoveryMs=21_000, SatietyDecayMs=121_000;
    const int ArcherTrainingRecoveryMs=10_900;
    internal const int CrcIntervalMs=30_250;
    internal const int OnlineFameIntervalMs=180_000;
    long nextCrcChallenge=Environment.TickCount64+CrcIntervalMs;
    long? nextOnlineFame;
    long? nextOnlineFameRetry;
    ushort? pendingCrc;
    (ushort Sprite,string Name) comboSpecies;
    byte comboCount;
    long comboDeadline;
    static int HealthRecoveryInterval(int map)=>map==297?ArcherTrainingRecoveryMs:HealthRecoveryMs;
    int healthRecoveryInterval=HealthRecoveryInterval(character.Map);
    long nextHealthRecovery=Environment.TickCount64+HealthRecoveryInterval(character.Map);
    long nextManaRecovery=Environment.TickCount64+ManaRecoveryMs;
    long nextSatiety=Environment.TickCount64+SatietyDecayMs;

    internal static ushort ComboWindow(int monsterLevel,int skillLevel)=>monsterLevel==0?ComboSeconds:
        (ushort)Math.Clamp(30+(int)Math.Floor((monsterLevel-skillLevel)*1.5),1,ComboSeconds);

    internal byte RegisterKill((ushort Sprite,string Name) species,ushort seconds,long now){
        comboCount=species==comboSpecies&&now<comboDeadline&&comboCount<10?(byte)(comboCount+1):(byte)1;
        comboSpecies=species;
        comboDeadline=now+seconds*1000L;
        return comboCount;
    }
    public Character Character {get;set;}=character;
    internal Character? Leon {get;set;}
    internal bool LeonDismissed {get;set;}
    internal byte LeonCommandSlot {get;set;}=MercenaryHiring.AttackCommandSlot;
    internal long NextLeonStepAt {get;set;}
    internal long NextLeonControlRefreshAt {get;set;}
    internal byte? LeonBackstepDirection {get;set;}
    internal (long Due,byte Direction)? PendingLeonArrival {get;set;}
    internal Character SpawnLeon(byte direction=0){
        PendingLeonArrival=null;
        long now=Environment.TickCount64;
        NextLeonStepAt=now+MercenaryHiring.FollowIntervalMs;
        NextLeonControlRefreshAt=now+MercenaryHiring.ControlRefreshIntervalMs;
        LeonBackstepDirection=null;
        return Leon=MercenaryHiring.LeonAt(Character,direction);
    }
    public bool Ready {get;set;}
    internal byte CombatMode {get;set;}=1;
    internal int StatusDimension {get;set;}
    internal uint VitalsAux {get;private set;}
    long guildNpcRetaliationAt;
    int guildNpcRetaliationMap;
    bool guildNpcRetaliationPending;
    internal void ProvokeGuildNpc(long now){
        if(guildNpcRetaliationMap!=Character.Map){
            guildNpcRetaliationMap=Character.Map;
            guildNpcRetaliationPending=false;
            guildNpcRetaliationAt=0;
        }
        if(guildNpcRetaliationPending)return;
        if(guildNpcRetaliationAt<=now)guildNpcRetaliationAt=now+7000;
        guildNpcRetaliationPending=true;
    }
    internal bool TakeGuildNpcRetaliation(long now){
        if(Character.AwaitingRevival||Character.Map!=guildNpcRetaliationMap){
            guildNpcRetaliationPending=false;
            guildNpcRetaliationAt=0;
        }
        if(!guildNpcRetaliationPending||now<guildNpcRetaliationAt)return false;
        guildNpcRetaliationPending=false;
        guildNpcRetaliationAt+=12300;
        if(guildNpcRetaliationAt<=now)guildNpcRetaliationAt=now+12300;
        return true;
    }
    internal uint AuxiliaryAfterDamage(uint hp,uint maximum,uint damage){
        uint scale=Math.Max(1u,maximum/10);
        ulong increase=(ulong)(maximum-hp)*damage/scale;
        return (uint)Math.Min(1000UL,VitalsAux+increase);
    }
    internal uint? LastStatusFame {get;private set;}
    internal uint? StatusEffectFlags {get;private set;}
    internal void ObserveSentStatus(ReadOnlySpan<byte> packet){
        if(packet.Length<3||packet[0]!=8)return;
        byte mask=packet[1];
        int offset=(mask&0x40)==0?3:49;
        if((mask&0x40)!=0&&packet.Length>=offset)
            LastStatusFame=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[35..]);
        if((mask&0x20)!=0){
            if(packet.Length<offset+16)return;
            VitalsAux=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[(offset+12)..]);
            offset+=16;
        }
        if((mask&0x10)!=0)offset+=4;
        if((mask&8)!=0&&packet.Length>=offset+12)
            StatusEffectFlags=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[(offset+6)..]);
    }
    internal bool TrainingHealthStatusPending {get;set;}
    internal byte[][] PeerAppearance()=>CombatMode==1?[GamePackets.Human(Character)]:
        [GamePackets.Human(Character),GamePackets.CombatMode(Character.Id,CombatMode)];
    public HashSet<uint> FarSpawns {get;}=[];
    internal ConcurrentDictionary<uint,byte> RefreshedObjects {get;}=new();
    (int Map,bool Entry,bool RoomExit)? pendingMapCheck;
    int pendingDisplacementChecks;
    internal void ExpectMapCheck(int map,bool entry=false,bool roomExit=false,bool displacement=false){
        if(displacement&&pendingMapCheck is {} pending&&pending.Map==map){
            pendingDisplacementChecks++;
            return;
        }
        pendingMapCheck=(map,entry,roomExit);
        pendingDisplacementChecks=0;
    }
    internal bool HasPendingMapCheck(int map)=>pendingMapCheck?.Map==map;
    internal (int Map,bool Entry,bool RoomExit)? TakeMapCheck(int map){
        if(pendingMapCheck is not {} pending||pending.Map!=map)return null;
        if(pendingDisplacementChecks>0){
            pendingDisplacementChecks--;
            pendingMapCheck=(map,false,false);
        }else pendingMapCheck=null;
        return pending;
    }
    public WorldTrade? Trade {get;set;}
    internal WorldParty? Party {get;set;}
    internal bool PoisonShown {get;set;}
    internal byte[]? TakeCrcChallenge(long now){
        if(now<nextCrcChallenge)return null;
        nextCrcChallenge=now+CrcIntervalMs;
        ushort challenge=(ushort)Random.Shared.Next(ushort.MaxValue+1);
        // 0047C787 folds the low byte before the high byte from the little-endian native word.
        pendingCrc=System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(challenge);
        return GamePackets.CrcChallenge(challenge);
    }
    internal bool? CheckCrcReply(ushort reply){
        if(pendingCrc is not ushort expected)return null;
        if(reply!=expected)return false;
        pendingCrc=null;return true;
    }
    internal bool OnlineFameDue(long now)=>Ready&&!Character.AwaitingRevival&&
        (nextOnlineFame is not long due||now>=due)&&
        (nextOnlineFameRetry is not long retry||now>=retry);
    internal void ScheduleOnlineFame(long now,bool persisted){
        if(!persisted){
            nextOnlineFame??=now;
            nextOnlineFameRetry=now+5_000;
            return;
        }
        nextOnlineFameRetry=null;
        long due=nextOnlineFame??now;
        // Advance to the next session deadline even if this grant ran late.
        long periods=(now-due)/OnlineFameIntervalMs+1;
        nextOnlineFame=due+periods*OnlineFameIntervalMs;
    }
    internal Character? PeriodicVitals(long now,out bool emptySatietyTick){
        var current=Character;
        uint damage=current.DamageTaken,mana=current.ManaSpent,satiety=current.Satiety;
        bool consumesSatiety=current.Map!=297;
        bool satietyDue=now>=nextSatiety;
        if(satietyDue){
            nextSatiety=now+SatietyDecayMs;
            if(consumesSatiety&&satiety>0&&!current.AwaitingRevival)satiety--;
        }
        int interval=HealthRecoveryInterval(current.Map);
        if(healthRecoveryInterval!=interval){
            healthRecoveryInterval=interval;
            nextHealthRecovery=now+interval;
        }
        // S08 1:222479 onwards records +3 HP at FP 1, with FP unchanged.
        if(now>=nextHealthRecovery){
            nextHealthRecovery=current.Map==297?nextHealthRecovery+((now-nextHealthRecovery)/interval+1)*interval:now+interval;
            if(satiety>0&&!current.AwaitingRevival&&damage>0)
                damage-=Math.Min(damage,EquipmentItems.NaturalHealthRecovery(current));
        }
        if(now>=nextManaRecovery){
            nextManaRecovery=now+ManaRecoveryMs;
            // S08 1:129004 onward also restores MP at zero FP.
            if(!current.AwaitingRevival&&mana>0)mana-=Math.Min(mana,EquipmentItems.NaturalManaRecovery(current));
        }
        bool changed=damage!=current.DamageTaken||mana!=current.ManaSpent||satiety!=current.Satiety;
        emptySatietyTick=consumesSatiety&&satietyDue&&satiety==0&&!current.AwaitingRevival&&!changed;
        return changed?current with {DamageTaken=damage,ManaSpent=mana,Satiety=satiety}:null;
    }
    internal Func<Task>? FinishRevival {get;set;}
    internal Func<Task>? SupersedeConnection {get;set;}
    internal uint? RequestedRoomSign {get;set;}
    // An owner ID admits this session. An empty owner returns a kicked guest to town.
    internal Func<uint?,Task>? MovePersonalRoom {get;set;}
    internal long RevivalAt {get;set;}
    internal long NextStunEffectAt {get;set;}
    internal bool RevivalUnconsciousSent {get;set;}
    public LocalNpc? CraftStation {get;set;}
    public Task Send(byte[] packet)=>send(packet);
    internal Task SendPair(byte[] first,byte[] second)=>SendPackets([first,second]);
    internal async Task SendPackets(List<byte[]> packets){
        if(sendBatch!=null)await sendBatch(packets);
        else foreach(var packet in packets)await send(packet);
    }
    internal void Reconnect(Func<byte[],Task> sender,Func<Task> revive,Func<List<byte[]>,Task>? batchSender=null){
        send=sender;sendBatch=batchSender;FinishRevival=revive;
        pendingCrc=null;nextCrcChallenge=Environment.TickCount64+CrcIntervalMs;
        nextOnlineFame=nextOnlineFameRetry=null;
    }
}
public sealed class WorldHub {
    readonly ConcurrentDictionary<uint,WorldSession> sessions=new();
    public bool Enter(WorldSession session)=>sessions.TryAdd(session.Character.Id,session);
    internal WorldSession? Attached(uint id)=>sessions.GetValueOrDefault(id);
    public IEnumerable<WorldSession> Online=>sessions.Values.Where(s=>s.Ready);
    public WorldSession? FindOnline(string name)=>Online.FirstOrDefault(s=>s.Character.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
    public WorldSession[] InMap(int map)=>sessions.Values.Where(s=>s.Ready&&s.Character.Map==map).ToArray();
    public WorldSession? FindOnline(uint id)=>
        sessions.TryGetValue(id,out var session)&&session.Ready?session:null;
    public WorldSession? FindVisible(Character observer,uint id)=>
        FindOnline(id) is {} session&&CanSee(observer,session.Character)?session:null;
    internal static bool InMonsterReach(FieldObject monster,Character character,int range){
        if(character.Map!=monster.Map||character.Floor!=monster.Floor||character.CurrentHp()==0)return false;
        int distance=MonsterDistance(monster,character);
        return distance>0&&distance<=range;
    }
    internal static bool InMeleeReach(FieldObject monster,Character character)=>
        character.Map==monster.Map&&character.Floor==monster.Floor&&character.CurrentHp()>0&&
        Math.Abs(character.X-monster.X)+Math.Abs(character.Y-monster.Y)==1;
    static int MonsterDistance(FieldObject monster,Character character)=>
        Math.Max(Math.Abs(character.X-monster.X),Math.Abs(character.Y-monster.Y));
    internal WorldSession? FindMonsterTarget(FieldMonster monster,int range){
        var at=monster.Appearance;
        return InMap(at.Map).Where(s=>s.Character.Floor==at.Floor&&s.Character.CurrentHp()>0)
            .Select(s=>(Session:s,Distance:MonsterDistance(at,s.Character)))
            .Where(s=>s.Distance>0&&s.Distance<=range&&
                (s.Distance==1||monster.WithinRoam(s.Session.Character.X,s.Session.Character.Y)))
            .OrderBy(s=>s.Distance).ThenBy(s=>s.Session.Character.Id).Select(s=>s.Session).FirstOrDefault();
    }
    // Peer steps are tracked through ±16 columns and ±9 rows.
    // Cross-floor S33/S0C pairs were observed in Rolancia and its pub.
    public static bool CanSee(Character observer,Character target)=>
        observer.Map==target.Map&&WithinMovingObjectRange(observer,target.X,target.Y)&&
        (observer.Floor==target.Floor||(observer.Map%10000,observer.Floor,target.Floor) is (2,1,2) or (2,2,0 or 1) or (214,0,1));
    public static bool CanSee(Character observer,LocalNpc target)=>
        observer.Map==target.Map&&(observer.Floor==target.Floor||target.Flags==0||HairSalonDialog.IsBarber(target)||
            target.Category==2&&(observer.Map%10000,observer.Floor,target.Floor) is (2,2,1) or (210,0,1))&&
        WithinViewport(observer,target.X,target.Y);
    public static bool CanSee(Character observer,FieldObject target)=>
        observer.Map==target.Map&&observer.Floor==target.Floor&&(target.Category is 0 or 6
            ?WithinMovingObjectRange(observer,target.X,target.Y)
            :Math.Abs(observer.X-target.X)<=17&&Math.Abs(observer.Y-target.Y)<=10);
    // Explicit C0C replies have separate limits from ordinary movement and spawn visibility.
    internal static bool CanRefreshPeer(Character observer,Character target){
        if(observer.Map!=target.Map||observer.Id==target.Id)return false;
        int dx=target.X-observer.X,dy=target.Y-observer.Y;
        bool sameFloor=observer.Floor==target.Floor;
        if(observer.Map%10000==2){
            return dx is >=-16 and <=17&&dy>=-11&&(sameFloor?dy<=11:
                dy<=10&&(observer.Floor,target.Floor) is (1,0 or 2) or (2,0 or 1));
        }
        if(!sameFloor)return false;
        return (observer.Map%10000) switch {
            5=>dx is 17 or 18&&Math.Abs(dy)<=9,
            2002=>Math.Abs(dx)<=16&&dy is >=-12 and <=10,
            4002=>Math.Abs(dx)<=16&&dy is >=-11 and <=9,
            _=>false
        };
    }
    internal static bool CanRefreshMerchant(Character observer,LocalNpc target,bool includeDistant=false){
        if(includeDistant&&(CanRefreshDistantHiredMerchant(observer,target)||CanRefreshBankHiredMerchant(observer,target)))return true;
        if(observer.Map!=target.Map||observer.Map%10000!=2||target.Category!=2||
            target.X-observer.X is <-GamePackets.CameraX or >=32-GamePackets.CameraX)return false;
        int dy=target.Y-observer.Y;
        if(observer.Floor==target.Floor)
            return dy>=-10&&dy<=(target.Sprite==0x40C1?20:10);
        return Math.Abs(dy)<=10&&(observer.Floor,target.Floor) is (1,2);
    }
    internal static bool CanRefreshDistantHiredMerchant(Character observer,LocalNpc target){
        if(observer.Map!=target.Map||observer.Floor!=target.Floor||target.Category!=2||target.Sprite!=0x40C1||
            WithinViewport(observer,target.X,target.Y))return false;
        int dx=target.X-observer.X,dy=target.Y-observer.Y;
        return (observer.Map%10000) switch {
            2=>dx is >=-16 and <=19&&dy is >=-31 and <=20&&(dx>=16||dy< -10),
            4002=>dx is >=-26 and <=15&&dy is >=-9 and <=21,
            _=>false
        };
    }
    internal static bool CanRefreshBankHiredMerchant(Character observer,LocalNpc target)=>
        observer.Map==target.Map&&observer.Map%10000==1209&&observer.Floor==0&&target.Floor==0&&
        target.Category==2&&target.Sprite==0x40C1&&target.X-observer.X is >=-17 and <=15&&
        target.Y-observer.Y is >=-10 and <=9;
    // S11 and its C0C/S07 follow-up can reach beyond normal step tracking and unsolicited S07.
    internal static bool CanRefreshMonster(Character observer,FieldObject target)=>
        target.Category==0&&observer.Map==target.Map&&observer.Floor==target.Floor&&
        target.X-observer.X is >=-17 and <=18&&Math.Abs(observer.Y-target.Y)<=11;
    internal static bool CanRefreshRoomSign(Character observer,FieldObject target)=>
        target.Category==6&&observer.Map==target.Map&&observer.Floor==target.Floor&&
        Math.Abs(observer.X-target.X)<=17&&Math.Abs(observer.Y-target.Y)<=10;
    internal static bool CanRefreshFieldObject(Character observer,FieldObject target)=>
        CanRefreshMonster(observer,target)||CanRefreshRoomSign(observer,target);
    internal static bool TrackFieldRefresh(WorldSession observer,FieldObject target){
        if(CanSee(observer.Character,target))return true;
        if(!CanRefreshFieldObject(observer.Character,target))return false;
        observer.FarSpawns.Add(target.Id);
        return true;
    }
    static bool WithinMovingObjectRange(Character observer,int x,int y,int margin=0)=>
        Math.Abs(observer.X-x)<=16+margin&&Math.Abs(observer.Y-y)<=9+margin;
    static bool WithinViewport(Character observer,int x,int y){
        int dx=x-observer.X,dy=y-observer.Y;
        return dx>=-GamePackets.CameraX&&dx<32-GamePackets.CameraX&&dy>=-GamePackets.CameraY&&dy<19-GamePackets.CameraY;
    }
    public bool IsOccupied(int map,int x,int y,int floor,uint except)=>sessions.Values.Any(s=>s.Character.Id!=except&&s.Character.Map==map&&s.Character.X==x&&s.Character.Y==y&&s.Character.Floor==floor);
    public async Task Broadcast(int map,byte[] packet,uint except=0){
        foreach(var session in InMap(map))if(session.Character.Id!=except)await Notify(session,packet);
    }
    public async Task BroadcastVisible(Character target,byte[] packet,uint except=0){
        foreach(var session in InMap(target.Map))
            if(session.Character.Id!=except&&CanSee(session.Character,target))await Notify(session,packet);
    }
    internal async Task BroadcastCombatMode(Character target,byte mode,WorldSession? paddedSelf=null){
        var packet=GamePackets.CombatMode(target.Id,mode);
        if(paddedSelf!=null)await Notify(paddedSelf,[..packet,0]);
        foreach(var session in InMap(target.Map)){
            var observer=session.Character;
            // In Rolancia town, entering combat can trigger C0C from one row outside step tracking.
            bool seesCombatStart=mode==2&&target.Map%10000==2&&observer.Floor==target.Floor&&
                Math.Abs(observer.X-target.X)<=16&&Math.Abs(observer.Y-target.Y)==10;
            if(observer.Id!=paddedSelf?.Character.Id&&(CanSee(observer,target)||seesCombatStart))
                await Notify(session,packet);
        }
    }
    internal async Task RemovePeerAppearance(Character subject){
        foreach(var observer in InMap(subject.Map)){
            if(observer.Character.Id==subject.Id)continue;
            if(observer.RefreshedObjects.TryRemove(subject.Id,out _)||CanSee(observer.Character,subject))
                await Notify(observer,GamePackets.Remove(subject.Id));
        }
    }
    internal async Task BroadcastAppearance(WorldSession subject){
        foreach(var packet in subject.PeerAppearance())await BroadcastVisible(subject.Character,packet,subject.Character.Id);
        if(subject.Leon is {} leon)await BroadcastVisible(leon,GamePackets.Human(leon),subject.Character.Id);
    }
    internal async Task RemoveLeon(WorldSession owner){
        owner.PendingLeonArrival=null;
        if(owner.Leon is not {} leon)return;
        owner.Leon=null;
        await RemoveLeonAppearance(owner,leon);
    }
    async Task RemoveLeonAppearance(WorldSession owner,Character leon){
        foreach(var observer in InMap(leon.Map)){
            if(observer!=owner&&CanSee(observer.Character,leon))await Notify(observer,GamePackets.Remove(leon.Id));
        }
    }
    internal async Task FollowLeon(WorldSession session,WorldData world,long now){
        if(!session.Ready)return;
        if(session.PendingLeonArrival is {} arrival){
            if(now<arrival.Due)return;
            var arrived=session.SpawnLeon(arrival.Direction);
            var appearance=GamePackets.Human(arrived);
            await NotifyPackets(session,[MercenaryHiring.ShowControls(),appearance]);
            if(session.Ready&&session.Leon==arrived)await BroadcastVisible(arrived,appearance,session.Character.Id);
            return;
        }
        if(session.Leon is not {} previous||now<session.NextLeonStepAt)return;
        long lastDue=session.NextLeonStepAt;
        session.NextLeonStepAt=(now-lastDue<WorldSettings.FieldUpdateMs?lastDue:now)+MercenaryHiring.FollowIntervalMs;
        var owner=session.Character;
        if(previous.Map!=owner.Map||previous.Floor!=owner.Floor||!CanSee(owner,previous)){
            if(!session.Ready||session.Character!=owner||session.Leon!=previous)return;
            var returned=session.SpawnLeon();
            await RemoveLeonAppearance(session,previous);
            if(!session.Ready||session.Leon!=returned)return;
            await NotifyPackets(session,[GamePackets.Remove(previous.Id),MercenaryHiring.ShowControls(),GamePackets.Human(returned)]);
            await BroadcastVisible(returned,GamePackets.Human(returned),owner.Id);
            return;
        }
        int Distance(int x,int y)=>Math.Max(Math.Abs(owner.X-x),Math.Abs(owner.Y-y));
        Character? Walk(byte direction){
            if(!world.TryKnockbackTile(previous.Map,previous.X,previous.Y,previous.Floor,direction,out var tile)||
                IsOccupied(owner.Map,tile.X,tile.Y,tile.Floor,0))return null;
            return previous with {X=tile.X,Y=tile.Y,Direction=direction};
        }
        int distance=Distance(previous.X,previous.Y);
        bool backstep=distance==MercenaryHiring.FollowDistance&&session.LeonBackstepDirection.HasValue;
        Character? next=null;
        if(backstep){
            // Rolancia capture 1:5016/1:5024 retraces the previous step at four tiles.
            next=Walk(session.LeonBackstepDirection.GetValueOrDefault());
            if(next!=null&&Distance(next.X,next.Y)!=MercenaryHiring.FollowDistance+1)next=null;
        }else if(distance>MercenaryHiring.FollowDistance){
            int best=distance;
            for(byte direction=0;direction<8;direction++){
                if(Walk(direction) is not {} candidate)continue;
                int candidateDistance=Distance(candidate.X,candidate.Y);
                if(candidateDistance>=best)continue;
                best=candidateDistance;
                next=candidate;
            }
        }
        if(!session.Ready||session.Character!=owner||session.Leon!=previous)return;
        if(next==null){
            if(distance!=MercenaryHiring.FollowDistance)session.LeonBackstepDirection=null;
            return;
        }
        session.LeonBackstepDirection=!backstep&&Distance(next.X,next.Y)==MercenaryHiring.FollowDistance
            ?(byte)(next.Direction^2):null;
        session.Leon=next;
        var move=GamePackets.ObjectMove(previous,next.Direction);
        var turn=backstep||previous.Direction==next.Direction?null:GamePackets.Turn(next);
        foreach(var observer in InMap(next.Map)){
            if(!session.Ready||session.Leon!=next)break;
            bool wasVisible=CanSee(observer.Character,previous),visible=CanSee(observer.Character,next);
            if(wasVisible){
                var packets=new List<byte[]>();
                if(turn!=null)packets.Add(turn);
                packets.Add(move);
                if(observer==session&&now>=session.NextLeonControlRefreshAt){
                    packets.Add(MercenaryHiring.ShowControls(padded:true));
                    session.NextLeonControlRefreshAt=now+MercenaryHiring.ControlRefreshIntervalMs;
                }
                if(!visible)packets.Add(GamePackets.Remove(next.Id));
                await NotifyPackets(observer,packets);
            }else if(visible){
                await Notify(observer,GamePackets.Human(next));
            }
        }
    }
    public async Task BroadcastVisible(FieldObject target,byte[] packet){
        foreach(var session in InMap(target.Map)){
            bool spawnedHere=packet[0]==0x5F&&session.FarSpawns.Remove(target.Id);
            bool restoredSign=target.Category==6&&session.FarSpawns.Contains(target.Id);
            if(restoredSign&&packet[0]==0x0E)session.FarSpawns.Remove(target.Id);
            if(spawnedHere||restoredSign||CanSee(session.Character,target)||packet[0]==0x11&&CanRefreshMonster(session.Character,target))
                await Notify(session,packet);
        }
    }
    public async Task BroadcastSpawn(FieldObject monster){
        var packet=GamePackets.Objects([],[monster]);
        foreach(var session in InMap(monster.Map)){
            var observer=session.Character;
            // Unsolicited S07 can announce a monster one tile beyond movement tracking.
            if(observer.Floor!=monster.Floor||!WithinMovingObjectRange(observer,monster.X,monster.Y,margin:1))continue;
            if(!CanSee(observer,monster))session.FarSpawns.Add(monster.Id);
            await Notify(session,packet);
        }
    }
    public async Task MoveField(FieldObject previous,FieldObject current,bool firstFightingRoosterStep=false){
        bool moved=previous.X!=current.X||previous.Y!=current.Y;
        byte[] update=moved?GamePackets.ObjectMove(previous.Id,previous.X,previous.Y,previous.Floor,current.Type,0)
            :GamePackets.Turn(current.Id,current.Type);
        byte[]? turn=moved&&current.IsFightingRooster&&(firstFightingRoosterStep||previous.Type!=current.Type)
            ?[..GamePackets.Turn(current.Id,current.Type),0]:null;
        if(firstFightingRoosterStep&&moved)update=[..update,0];
        foreach(var session in InMap(current.Map)){
            bool saw=CanSee(session.Character,previous),sees=CanSee(session.Character,current);
            if(sees){
                session.FarSpawns.Remove(current.Id);
                if(!saw)await Notify(session,GamePackets.Objects([],[current]));
                else if(turn!=null)await Notify(session,turn,update);
                else await Notify(session,update);
            }else if(saw&&moved){
                session.FarSpawns.Remove(current.Id);
                if(turn!=null)await NotifyPackets(session,[turn,update,GamePackets.Remove(current.Id)]);
                else await Notify(session,update,GamePackets.Remove(current.Id));
            }else if(saw||moved&&session.FarSpawns.Contains(current.Id)){
                session.FarSpawns.Remove(current.Id);
                await Notify(session,GamePackets.Remove(current.Id));
            }else if(!moved&&CanRefreshMonster(session.Character,current)){
                await Notify(session,update);
            }
        }
    }
    internal void AppendMovedPeerPackets(WorldSession moving,Character previous,List<byte[]> packets){
        var current=moving.Character;
        foreach(var other in InMap(current.Map)){
            if(other.Leon is {} leon){
                bool sawLeon=CanSee(previous,leon),seesLeon=CanSee(current,leon);
                if(!sawLeon&&seesLeon)packets.Add(GamePackets.Human(leon));
                else if(sawLeon&&!seesLeon)packets.Add(GamePackets.Remove(leon.Id));
            }
            var observer=other.Character;
            if(observer.Id==current.Id)continue;
            bool sawOther=CanSee(previous,observer),seesOther=CanSee(current,observer);
            if(seesOther&&!sawOther){
                moving.RefreshedObjects.TryRemove(observer.Id,out _);
                packets.AddRange(other.PeerAppearance());
            }else if(!seesOther&&(sawOther||!CanRefreshPeer(current,observer)&&moving.RefreshedObjects.TryRemove(observer.Id,out _))){
                packets.Add(GamePackets.Remove(observer.Id));
            }
        }
    }
    internal async Task BroadcastPeerStep(WorldSession moving,Character previous,byte motion,byte? direction=null){
        var current=moving.Character;
        foreach(var other in InMap(current.Map)){
            var observer=other.Character;
            if(observer.Id==current.Id)continue;
            bool sawMover=CanSee(observer,previous),seesMover=CanSee(observer,current);
            bool refreshedMover=other.RefreshedObjects.TryRemove(current.Id,out _);
            if(sawMover){
                // The last step is delivered before the object leaves the observer's view.
                await Notify(other,GamePackets.ObjectMove(previous,direction??current.Direction,motion),
                    seesMover?null:GamePackets.Remove(current.Id));
            }else if(seesMover){
                foreach(var packet in moving.PeerAppearance())await Notify(other,packet);
            }else if(refreshedMover){
                await Notify(other,GamePackets.Remove(current.Id));
            }
        }
    }
    public static async Task Notify(WorldSession session,byte[] packet,byte[]? following=null){
        try{
            if(following==null)await session.Send(packet);
            else await session.SendPair(packet,following);
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException){}
    }
    internal static async Task NotifyPackets(WorldSession session,List<byte[]> packets){
        try{await session.SendPackets(packets);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException){}
    }
    public async Task Leave(WorldSession session){
        if(sessions.TryRemove(new KeyValuePair<uint,WorldSession>(session.Character.Id,session))){
            session.Ready=false;
            await RemoveLeon(session);
            await RemovePeerAppearance(session.Character);
        }
    }
}
