using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElanciaLocal;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#if !ELANCIA_RELEASE_BUNDLE
if(args.Contains("--self-test")){await SelfTests.Run();return;}
if(args.Contains("--integration-test")){await IntegrationTests.Run();return;}
if(args.Contains("--packet-fixtures")){Console.WriteLine(JsonSerializer.Serialize(PacketFixtures.All().ToDictionary(p=>p.Key,p=>Convert.ToHexString(p.Value))));return;}
#endif
string Option(string name,string fallback){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
if(args.Contains("--make-tls-cert")){
    string output=Option("--make-tls-cert","");
    if(string.IsNullOrWhiteSpace(output))throw new ArgumentException("Specify a PFX output path.");
    using var key=RSA.Create(2048);
    var request=new CertificateRequest("CN=Elancia Server",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
    using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
    using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    file.Write(generated.Export(X509ContentType.Pfx,Environment.GetEnvironmentVariable("ELANCIA_TLS_PASSWORD")));
    Console.WriteLine(generated.GetCertHashString(HashAlgorithmName.SHA256));
    return;
}
int offset=int.Parse(Option("--port-offset","0"));
bool publicServer=args.Contains("--public");
#if ELANCIA_RELEASE_BUNDLE
if(!publicServer){Console.Error.WriteLine("Start the bundled server with Start-Server.cmd.");Environment.ExitCode=1;return;}
IPAddress gatewayAddress=IPAddress.Loopback;
if(Option("--bind","127.0.0.1")!="127.0.0.1"){Console.Error.WriteLine("The bundled server uses 127.0.0.1.");Environment.ExitCode=1;return;}
#else
IPAddress gatewayAddress=IPAddress.Parse(Option("--bind","0.0.0.0"));
#endif
using var certificate=publicServer?X509CertificateLoader.LoadPkcs12FromFile(Option("--tls-cert",""),
    Environment.GetEnvironmentVariable("ELANCIA_TLS_PASSWORD")):null;
int worldPort=10011+offset;
Directory.CreateDirectory("logs");
Directory.CreateDirectory("logs/control");
var tickets=new System.Collections.Concurrent.ConcurrentDictionary<string,(string Account,uint? CharacterId,DateTimeOffset Expires,WorldHandoff? Handoff)>();
var startLocation=StartLocation.Load(Option("--start-location","start-location.json"));
var dataDirectory=Option("--data","data");
bool allowUnreleasedRegions=args.Contains("--allow-unreleased-regions");
bool CanEnterRegion(int map)=>allowUnreleasedRegions||!WorldSettings.IsUnreleasedRegion(map);
var appearanceProfiles=AppearanceProfile.Load(Option("--appearance-profiles","appearance-profiles.json"));
string clientDirectory=Option("--client-dir",Environment.GetEnvironmentVariable("ELANCIA_CLIENT_DIR")??@"C:\Nexon\Elancia");
var metadata=new MetadataCache(Path.Combine(clientDirectory,"Profiles","Meta.dat"),
    Path.Combine(dataDirectory,"recorded-meta.dat"));
EquipmentItems.UseNativeMetadata(metadata);
var emotionCommands=metadata.EmotionCommands();
var emotionMotions=emotionCommands.Values.ToHashSet();
bool fieldWanders=!args.Contains("--stationary-field");
var world=new WorldData(clientDirectory,Option("--world-npcs","world-npcs.json"),
    Option("--recorded-live",Path.Combine(dataDirectory,"world-live.json")),expandField:fieldWanders);
var database=publicServer?new AccountDatabase(Path.Combine(dataDirectory,"elancia.sqlite3")):null;
var gateway=publicServer?new SecureGateway(database!):null;
var characters=new CharacterStore(Path.Combine(dataDirectory,"characters.json"),startLocation,database);
foreach(var (ownerId,item) in characters.GroundItems())world.AddDrop(item,ownerId);
DateTimeOffset WorldTime()=>args.Contains("--fixed-world-clock")?RecordedClock.Reference.ObservedAt:DateTimeOffset.UtcNow;
bool awardOnlineFame=!args.Contains("--no-online-fame");
var hub=new WorldHub();
const int FriendLimit=50;
int? respawnSeconds=args.Contains("--field-respawn-seconds")?int.Parse(Option("--field-respawn-seconds","8")):null;
if(respawnSeconds is <1 or >3600)throw new ArgumentOutOfRangeException("--field-respawn-seconds");
int trainingHealthRoll=int.Parse(Option("--training-health-roll",CombatAbilities.TrainingHealthRoll.ToString()));
if(trainingHealthRoll<0)throw new ArgumentOutOfRangeException("--training-health-roll");
int fixedDollHealthRoll=args.Contains("--training-health-roll")?trainingHealthRoll:CombatAbilities.FixedDollHealthRoll;
int trainingManaRoll=int.Parse(Option("--training-mana-roll",LocalSpell.TrainingManaRoll.ToString()));
if(trainingManaRoll<0)throw new ArgumentOutOfRangeException("--training-mana-roll");
ushort abilityHitCount=ushort.Parse(Option("--ability-hit-count",CombatAbilities.DefaultHitCounter.ToString()));
if(abilityHitCount is <1 or >CombatAbilities.DefaultHitCounter)throw new ArgumentOutOfRangeException("--ability-hit-count");
bool overrideAbilityHitCount=args.Contains("--ability-hit-count");
int guildHoldSeconds=int.Parse(Option("--guild-capture-seconds",GuildHouseCapture.HoldSeconds.ToString()));
if(guildHoldSeconds is <1 or >GuildHouseCapture.HoldSeconds)throw new ArgumentOutOfRangeException("--guild-capture-seconds");
var guildCaptures=new Dictionary<int,GuildHouseCapture>();
var guildFoundings=new Dictionary<ushort,GuildFoundingRequest>();
ushort nextGuildFoundingScript=0x6000;
using var worldUpdateLock=new SemaphoreSlim(1,1);
if(!world.GetMap(startLocation.Map).CanWalk(startLocation.X,startLocation.Y,startLocation.Floor))throw new InvalidDataException("Configured start location is not walkable");
using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
var services=new List<Task>{
    publicServer
        ? gateway!.Run(gatewayAddress,7500+offset,certificate!, (stream,role,connection)=>Serve(stream,role,connection),stop.Token)
        : Task.WhenAll(Listen(7500+offset,"Bootstrap"),Listen(7501+offset,"Login"),Listen(worldPort,"World")),
    UpdateField()
};
if(!publicServer&&!args.Contains("--no-web"))services.Add(LocalWeb.Run(characters,7503+offset,stop.Token));
if(database!=null&&!args.Contains("--no-admin"))
    services.Add(AccountManagement.Run(database,characters,hub,worldUpdateLock,gateway!,7500+offset,stop.Token));
if(publicServer){await Task.WhenAny(services);stop.Cancel();}
await Task.WhenAll(services);

bool TryAwardOnlineFame(WorldSession session,long now){
    if(!awardOnlineFame||!session.OnlineFameDue(now))return false;
    var previous=session.Character;
    try{session.Character=characters.AwardOnlineFame(previous.Id);}
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        session.ScheduleOnlineFame(now,persisted:false);
        Console.Error.WriteLine($"Online fame persistence: {ex.Message}");
        return false;
    }
    session.ScheduleOnlineFame(now,persisted:true);
    return CombatAbilities.Fame(session.Character)!=CombatAbilities.Fame(previous);
}
Task SendOnlineFame(WorldSession session)=>
    WorldHub.Notify(session,GamePackets.Status(session.Character,mask:0x40,state:session.CombatMode));

async Task StartFightingRoosterWalk(FieldMonster rooster,CancellationToken cancellation){
    try{
        // Both recorded hatches walked about 123 ms after the item was consumed.
        await Task.Delay(120,cancellation);
        await worldUpdateLock.WaitAsync(cancellation);
        try{
            if(rooster.Hp==0)return;
            rooster.ScheduleRoam(Environment.TickCount64+2900);
            var previous=rooster.Appearance;
            if(rooster.Target==null&&world.TryFightingRoosterStep(rooster,
                at=>hub.IsOccupied(at.Map,at.X,at.Y,at.Floor,0),first:true))
                await hub.MoveField(previous,rooster.Appearance,firstFightingRoosterStep:true);
        }finally{worldUpdateLock.Release();}
    }catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
}

StartLocation RevivalArrival(StartLocation desired,uint characterId){
    var map=world.GetMap(desired.Map);
    bool Available(int x,int y,byte floor)=>!world.IsOccupied(desired.Map,x,y,floor)&&!hub.IsOccupied(desired.Map,x,y,floor,characterId);
    if(map.CanStand(desired.X,desired.Y,desired.Floor)&&Available(desired.X,desired.Y,desired.Floor))return desired;
    for(byte direction=0;direction<8;direction++)
        if(map.TryStep(desired.X,desired.Y,desired.Floor,direction,out var neighbor)&&neighbor.Floor==desired.Floor&&
            map.CanStand(neighbor.X,neighbor.Y,neighbor.Floor)&&Available(neighbor.X,neighbor.Y,neighbor.Floor))
            return new(desired.Map,neighbor.X,neighbor.Y,neighbor.Floor);
    var spawn=map.FindSpawn(Available)??throw new InvalidDataException("Revival map has no available spawn");
    return new(desired.Map,spawn.X,spawn.Y,spawn.Floor);
}

async Task UpdateField(){
    using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(WorldSettings.FieldUpdateMs));
    long nextRecordedCheck=0;
    var clock=world.Clock;
    long lastClockPulse=clock.Pulse(WorldTime());
    bool PlayerAt(FieldObject at)=>hub.IsOccupied(at.Map,at.X,at.Y,at.Floor,0);
    try{
        while(await timer.WaitForNextTickAsync(stop.Token)){
            await worldUpdateLock.WaitAsync(stop.Token);
            try{
                long now=Environment.TickCount64;
                long itemNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if(now>=nextRecordedCheck){
                    nextRecordedCheck=now+1000;
                    var previousNpcs=world.Npcs;
                    if(world.ReloadLive())await UpdateVisibleNpcs(previousNpcs);
                    foreach(var (previous,current) in world.ApplyRecordedTrainingTargets(PlayerAt)){
                        if(current==null)await hub.BroadcastVisible(previous!,GamePackets.Remove(previous!.Id));
                        else if(previous==null)await hub.BroadcastVisible(current,GamePackets.Objects([],[current]));
                        else await hub.BroadcastVisible(current,GamePackets.Turn(current.Id,current.Type));
                    }
                    foreach(var monster in world.AddRecordedMonsters(PlayerAt))await hub.BroadcastSpawn(monster);
                    foreach(var sign in world.ExpireRoomSigns(now))await hub.BroadcastVisible(sign,GamePackets.Remove(sign.Id));
                    foreach(var drop in world.ExpiredDrops(itemNow))await RemoveExpiredDrop(drop,itemNow);
                }
                var utc=WorldTime();
                var currentClock=world.Clock;
                long pulse=currentClock.Pulse(utc);
                if(currentClock.HourlyPulseAt!=clock.HourlyPulseAt)lastClockPulse=pulse;
                clock=currentClock;
                if(pulse>lastClockPulse){
                    lastClockPulse=pulse;
                    var (hour,day)=GamePackets.WorldClockPulse(clock,pulse);
                    foreach(var session in hub.Online)await WorldHub.Notify(session,hour,day);
                }
                foreach(var monster in world.RespawnReady(now,PlayerAt,varyLocation:fieldWanders))
                    await hub.BroadcastSpawn(monster);
                if(guildCaptures.Count>0)await UpdateGuildCaptures(now,utc);
                foreach(var request in guildFoundings.Values.ToArray()){
                    if(now>=request.Due)await FinishGuildFounding(request,GuildRegistration.Expired);
                    else if(!request.Participants.All(member=>CanFoundGuildHere(member,request.Npc)))
                        await FinishGuildFounding(request,GuildRegistration.MissingMembers);
                }
                foreach(var session in hub.Online){
                    if(session.TakeCrcChallenge(now) is {} crc)await WorldHub.Notify(session,crc);
                    await UpdateTimedItems(session,itemNow);
                    await ClearExpiredDaggerPoison(session);
                    await ClearExpiredPotionBuffs(session,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await ClearExpiredPlayerDebuffs(session,itemNow);
                    if(session.TakeGuildNpcRetaliation(now))await GuildNpcStrike(session);
                    var periodic=session.PeriodicVitals(now,out bool emptySatietyTick);
                    if(session.Character.AwaitingRevival){
                        if(now>=session.RevivalAt&&session.FinishRevival is {} revive)await revive();
                        else if(now>=session.RevivalAt-WorldSettings.RevivalPulseMs)await ShowUnconscious(session);
                        else if(now>=session.NextStunEffectAt){
                            // Keep the effects on the original timeline even if a field tick runs late.
                            long missed=(now-session.NextStunEffectAt)/WorldSettings.RevivalPulseMs;
                            session.NextStunEffectAt+=(missed+1)*WorldSettings.RevivalPulseMs;
                            var downed=session.Character;
                            // The guild house 2 self recording uses the padded S29.
                            bool paddedSelf=downed.Map==2222&&downed.ElBlessingUntil>0;
                            if(paddedSelf)
                                await WorldHub.Notify(session,GamePackets.RevivalStunEffect(downed.Id,padded:true));
                            await hub.BroadcastVisible(downed,GamePackets.RevivalStunEffect(downed.Id),paddedSelf?downed.Id:0);
                        }
                        continue;
                    }
                    if(session.Leon!=null||session.PendingLeonArrival!=null)await hub.FollowLeon(session,world,now);
                    bool onlineFameAwarded=TryAwardOnlineFame(session,now);
                    if(periodic==null){
                        if(onlineFameAwarded)await SendOnlineFame(session);
                        if(emptySatietyTick)await WorldHub.Notify(session,GamePackets.EmptySatietyStatus(session.CombatMode));
                        continue;
                    }
                    var before=session.Character;
                    bool healed=periodic.DamageTaken<before.DamageTaken;
                    try{session.Character=characters.SetHealth(periodic.Id,periodic.DamageTaken,
                        satiety:periodic.Satiety,manaSpent:periodic.ManaSpent);}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                        Console.Error.WriteLine($"Periodic vitals persistence: {ex.Message}");
                        if(onlineFameAwarded)await SendOnlineFame(session);
                        continue;
                    }
                    bool famePending=session.LastStatusFame is uint lastFame&&lastFame!=CombatAbilities.Fame(session.Character);
                    var (status,following)=healed&&session.TrainingHealthStatusPending&&periodic.Map==297&&periodic.SpeedPotionUntil==0
                        ?(GamePackets.PaddedStatus(session.Character,0x60,session.CombatMode,session.VitalsAux),(byte[]?)null)
                        :GamePackets.PeriodicStatus(before,session.Character,session.CombatMode,
                            session.VitalsAux,session.StatusEffectFlags,session.StatusDimension,famePending,onlineFameAwarded);
                    if(onlineFameAwarded&&(status[1]&0x40)==0)await SendOnlineFame(session);
                    await WorldHub.Notify(session,status,following);
                    if(healed&&session.Party is {} party)await party.UpdateHealth(session);
                }
                var activeMaps=hub.Online.Select(s=>s.Character.Map).ToHashSet();
                foreach(var monster in world.ActiveAttackers(activeMaps)){
                    var at=monster.Appearance;
                    if(monster.Target is {} target){
                        var character=target.Character;
                        if(hub.FindOnline(character.Id)==target&&character.Map==at.Map&&character.Floor==at.Floor&&character.CurrentHp()>0){
                            bool inRange=monster.CanStrike(character);
                            if(!inRange&&fieldWanders&&monster.CanPursue(character,now)){
                                if(monster.PursuitReady(now)&&world.TryPursue(monster,character,PlayerAt)){
                                    monster.DelayAfterPursuit(now);
                                    await hub.MoveField(at,monster.Appearance);
                                    inRange=monster.CanStrike(character);
                                }
                                if(!inRange)continue;
                            }
                            if(inRange){
                                if(monster.AttackReady(now))await MonsterStrike(monster,target);
                                continue;
                            }
                        }
                        monster.ClearTarget();
                    }
                    if(monster.SightRange>0&&hub.FindMonsterTarget(monster,fieldWanders?monster.SightRange:1) is {} nearby)
                        monster.Notice(nearby,now);
                }
                if(fieldWanders)
                    foreach(var (previous,current) in world.Wander(now,PlayerAt,activeMaps))
                        await hub.MoveField(previous,current);
            }finally{worldUpdateLock.Release();}
        }
    }catch(OperationCanceledException) when(stop.IsCancellationRequested){}
}

bool CanFoundGuildHere(WorldSession session,LocalNpc npc)=>hub.FindOnline(session.Character.Id)==session&&
    WorldHub.CanSee(session.Character,npc)&&session.Character.CurrentHp()>0;

async Task FinishGuildFounding(GuildFoundingRequest request,GuildRegistration result){
    guildFoundings.Remove(request.Script);
    var founder=request.Founder;
    if(result==GuildRegistration.Created){
        var before=founder.Character;
        try{
            var registered=characters.RegisterGuild(before.Id,request.First.Character.Id,request.Second.Character.Id,request.Name);
            result=registered.Result;
            if(result==GuildRegistration.Created){
                founder.Character=registered.Members[0];
                request.First.Character=registered.Members[1];
                request.Second.Character=registered.Members[2];
                if(founder.Character.WalletSlot!=0)await WorldHub.Notify(founder,founder.Character.Gold==0
                    ?GamePackets.InventoryRemove(founder.Character.WalletSlot,EquipmentItems.CarriedWeight(founder.Character)):GamePackets.Wallet(founder.Character));
                await WorldHub.Notify(founder,GamePackets.Gold(founder.Character.Gold,1));
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            Console.Error.WriteLine($"Guild founding save failed: {ex.Message}");
            result=GuildRegistration.SaveFailed;
        }
    }
    request.Form.Finish(result);
    if(hub.FindOnline(founder.Character.Id)==founder){
        if(CanFoundGuildHere(founder,request.Npc))await WorldHub.Notify(founder,request.Form.Packet(request.Npc));
        else await WorldHub.Notify(founder,GamePackets.Message("길드 창립이 취소되었습니다."));
    }
    if(result==GuildRegistration.Created){
        foreach(var participant in request.Participants)
            await WorldHub.Notify(participant,GamePackets.SelfLook(participant.Character));
    }else{
        foreach(var member in new[]{request.First,request.Second}){
            if(hub.FindOnline(member.Character.Id)==member)
                await WorldHub.Notify(member,GamePackets.Message("길드 창립이 취소되었습니다."));
        }
    }
}

async Task InterruptGuildCapture(WorldSession session,string reason,bool notify=true){
    if(!guildCaptures.TryGetValue(session.Character.Map,out var attempt)||attempt.Challenger!=session)return;
    guildCaptures.Remove(attempt.Post.Map);
    if(notify)await WorldHub.Notify(session,GamePackets.Message($"길드집 점령이 중단됐습니다. {reason}"));
}

async Task UpdateGuildCaptures(long now,DateTimeOffset utc){
    foreach(var attempt in guildCaptures.Values.ToArray()){
        var session=attempt.Challenger;
        var current=session.Character;
        var post=attempt.Post;
        if(current.Map!=post.Map||!session.Ready||hub.FindOnline(current.Id)!=session){
            guildCaptures.Remove(post.Map);
            continue;
        }
        if(current.X!=post.X||current.Y!=post.Y||current.Floor!=post.Floor||current.AwaitingRevival||
            !GuildHouseCapture.IsOpen(utc)||!GuildHouseCapture.IsGuildMaster(current)){
            await InterruptGuildCapture(session,"깃발 앞에서 점령 조건을 유지해야 합니다.");
            continue;
        }
        if(now<attempt.Due)continue;
        Character? winner;
        try{winner=characters.CaptureGuildHouse(current.Id,post);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
            Console.Error.WriteLine($"Guild house capture save failed for map {post.Map}: {ex.Message}");
            await InterruptGuildCapture(session,"점령 결과를 저장하지 못했습니다.");
            continue;
        }
        if(winner==null){
            await InterruptGuildCapture(session,"점령 결과를 확정할 수 없습니다.");
            continue;
        }
        guildCaptures.Remove(post.Map);
        session.Character=winner;
        if(winner.WalletSlot!=0)await WorldHub.Notify(session,GamePackets.Wallet(winner));
        await WorldHub.Notify(session,GamePackets.Gold(winner.Gold));
        await hub.Broadcast(post.Map,GamePackets.Message($"[{winner.GuildName}] 길드가 길드집을 점령했습니다."));
        await WorldHub.Notify(session,GamePackets.Message("점령에 성공해 500갈리드를 돌려받았습니다."));
    }
}

async Task GuildNpcStrike(WorldSession target){
    var victim=target.Character;
    uint maximum=victim.MaxHp(EquipmentItems.EquippedEffects(victim));
    uint hp=victim.CurrentHp(maximum);
    if(hp==0)return;
    uint auxiliary=target.AuxiliaryAfterDamage(hp,maximum,100);
    bool lethal=hp<=100;
    try{
        target.Character=lethal?characters.KnockOut(victim.Id):characters.SetHealth(victim.Id,victim.DamageTaken+100);
    }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        Console.Error.WriteLine($"Guild NPC damage persistence: {ex.Message}");return;
    }
    await WorldHub.NotifyPackets(target,GamePackets.GuildNpcRetaliation(target.Character,target.CombatMode,target.StatusDimension,auxiliary));
    if(lethal){
        await ShowDowned(target);
        await hub.BroadcastVisible(target.Character,GamePackets.Human(target.Character),victim.Id);
    }
    if(target.Party is {} party)await party.UpdateHealth(target);
}

async Task MonsterStrike(FieldMonster monster,WorldSession target){
    var victim=target.Character;
    uint maximum=victim.MaxHp(EquipmentItems.EquippedEffects(victim));
    uint hp=victim.CurrentHp(maximum);
    var melee=monster.CastsSpell?default:world.RollMonsterMelee(monster,victim,Random.Shared.NextDouble());
    ushort damage=monster.CastsSpell?(ushort)Math.Min(world.RollMonsterSpellDamage(monster,victim,Random.Shared.NextDouble()),hp):melee.Damage;
    bool lethal=damage>=hp;
    uint auxiliary=target.AuxiliaryAfterDamage(hp,maximum,damage);
    if(damage>0){
        try{
            target.Character=characters.RecordMonsterDamage(victim.Id,damage,physical:!monster.CastsSpell);
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
            Console.Error.WriteLine($"Field damage persistence: {ex.Message}");return;
        }
    }
    if(monster.CastsSpell){
        await WorldHub.Notify(target,lethal
            ?GamePackets.RevivalVitals(target.Character,target.CombatMode,includeMaxima:true)
            :GamePackets.PaddedStatus(target.Character,0x60,target.CombatMode,auxiliary));
        await WorldHub.Notify(target,GamePackets.PaddedStatus(target.Character,0x40,target.CombatMode));
        await hub.BroadcastVisible(monster.Appearance,GamePackets.OrcShamanSpell(monster.Appearance.Id,victim.Id,damage));
    }else{
        if(monster.Face(victim.X,victim.Y))
            await hub.BroadcastVisible(monster.Appearance,GamePackets.Turn(monster.Appearance.Id,monster.Appearance.Type));
        await hub.BroadcastVisible(monster.Appearance,GamePackets.FieldStrikeMotion(monster.Appearance.Id,melee.MotionPadded));
        if(damage>0){
            bool includeMaxima=target.TrainingHealthStatusPending||melee.StatusMask==0x60;
            var status=lethal?GamePackets.RevivalVitals(target.Character,target.CombatMode,includeMaxima:includeMaxima)
                :GamePackets.VitalsStatus(target.Character,includeMaxima?(byte)0x60:melee.StatusMask,target.CombatMode,auxiliary,
                    padded:melee.StatusPadded||includeMaxima&&melee.StatusMask==0x20);
            await WorldHub.Notify(target,status);
        }
        await hub.BroadcastVisible(monster.Appearance,GamePackets.MeleeEffect(monster.Appearance.Id,victim.Id,damage,melee.Variant,melee.Motion));
    }
    if(lethal){
        await ShowDowned(target);
        await hub.BroadcastVisible(target.Character,GamePackets.Human(target.Character),victim.Id);
    }
    if(damage>0&&target.Party is {} party)await party.UpdateHealth(target);
}

static async Task ClearExpiredDaggerPoison(WorldSession session){
    if(!session.PoisonShown||session.Character.DaggerPoisonActive())return;
    session.PoisonShown=false;
    await WorldHub.Notify(session,GamePackets.TimedSpell(EquipmentItems.DaggerPoisonTimer with {Duration=0}));
    if(!session.Character.AwaitingRevival)await WorldHub.Notify(session,GamePackets.Message("단검용독 효과가 끝났습니다."));
}

async Task ClearExpiredPotionBuffs(WorldSession session,long now){
    var previous=session.Character;
    bool speed=previous.SpeedPotionUntil>0&&previous.SpeedPotionUntil<=now;
    bool health=previous.HealthPotionUntil>0&&previous.HealthPotionUntil<=now;
    bool blessing=previous.ElBlessingUntil>0&&previous.ElBlessingUntil<=now;
    bool guardianStrength=previous.Guardian is {StrengthUntil:>0} g&&g.StrengthUntil<=now;
    bool guardianCounter=previous.Guardian is {CounterUntil:>0} c&&c.CounterUntil<=now;
    if(guardianStrength||guardianCounter){
        try{session.Character=characters.ExpireGuardianRewards(previous.Id,now);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
            Console.Error.WriteLine($"Guardian reward expiry save failed: {ex.Message}");return;
        }
        if(guardianStrength){
            await WorldHub.Notify(session,GamePackets.TimedSpell(GuardianQuest.Timer(GuardianReward.Strength,0)));
            await WorldHub.Notify(session,GamePackets.Status(session.Character,0x40));
        }
        if(guardianCounter)await WorldHub.Notify(session,GamePackets.TimedSpell(GuardianQuest.Timer(GuardianReward.Counters,0)));
    }
    if(!speed&&!health&&!blessing)return;
    try{session.Character=characters.ExpirePotionBuffs(previous.Id,now);}
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        Console.Error.WriteLine($"Potion expiry save failed: {ex.Message}");return;
    }
    if(blessing)await WorldHub.Notify(session,GamePackets.TimedSpell(ElBlessing.Timer(0)));
    if(speed){
        await WorldHub.Notify(session,GamePackets.TimedSpell(PotionBuffs.Timer(PotionBuff.Speed,0)));
        await WorldHub.Notify(session,GamePackets.PotionBuffStatus(session.Character,state:session.Character.AwaitingRevival?(byte)2:session.CombatMode));
    }
    if(health){
        await WorldHub.Notify(session,GamePackets.TimedSpell(PotionBuffs.Timer(PotionBuff.Health,0)));
        byte state=session.Character.AwaitingRevival?(byte)2:session.CombatMode;
        await WorldHub.Notify(session,GamePackets.PaddedStatus(session.Character,0x60,state,session.VitalsAux));
        if(session.Party is {} party)await party.UpdateHealth(session);
    }
}

async Task ClearExpiredPlayerDebuffs(WorldSession session,long now){
    var previous=session.Character;
    var expired=PlayerDebuff.All.Where(debuff=>debuff.Deadline(previous)>0&&debuff.Deadline(previous)<=now).ToArray();
    if(expired.Length==0)return;
    try{session.Character=characters.ExpireDebuffs(previous.Id,now);}
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        Console.Error.WriteLine($"Player debuff expiry save failed: {ex.Message}");return;
    }
    var packets=new List<byte[]>();
    byte state=session.Character.AwaitingRevival?(byte)2:session.CombatMode;
    foreach(var debuff in expired){
        packets.Add(GamePackets.TimedSpell(debuff.Timer(0)));
        packets.Add(GamePackets.PaddedStatus(session.Character,0x40,state));
        packets.Add(GamePackets.Message($"{debuff.Spell.Name} 끝"));
    }
    await WorldHub.NotifyPackets(session,packets);
}

async Task<bool> UpdateTimedItems(WorldSession session,long now){
    var previous=session.Character;
    if(!(previous.Items??[]).Any(item=>CharacterStore.TimedItemNeedsUpdate(item,now))&&
        !(previous.BankItems??[]).Any(item=>CharacterStore.TimedItemNeedsUpdate(item,now))&&
        !(previous.LeonItems??[]).Any(item=>CharacterStore.TimedItemNeedsUpdate(item,now)))return true;
    ItemPlacement[] bag,bank,leon;
    try{(session.Character,bag,bank,leon)=characters.UpdateTimedItems(previous.Id,now);}
    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        Console.Error.WriteLine($"Timed item save failed: {ex.Message}");return false;
    }
    if(bag.Length>0){
        long weight=EquipmentItems.CarriedWeightTotal(previous.Items??[]);
        foreach(var expired in bag){
            weight-=EquipmentItems.InventoryWeight(expired);
            await WorldHub.Notify(session,GamePackets.ConsumableRemove(expired.InventorySlot,EquipmentItems.WeightWord(weight)));
        }
    }
    foreach(var expired in bank)await WorldHub.Notify(session,GamePackets.BankRemove(expired.InventorySlot));
    if(session.Leon!=null&&leon.Length>0){
        ushort weight=MercenaryHiring.InventoryWeight(session.Character.LeonItems??[]);
        foreach(var expired in leon)await WorldHub.Notify(session,MercenaryHiring.InventoryRemove(expired.InventorySlot,weight));
    }
    return true;
}

async Task RemoveExpiredDrop(FieldObject drop,long now){
    try{
        if(drop.DropOwnerId!=0){
            var updated=characters.ExpireGroundItem(drop.DropOwnerId,drop.Id,now);
            if(hub.Attached(drop.DropOwnerId) is {} owner)owner.Character=updated;
        }
    }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
        Console.Error.WriteLine($"Timed drop save failed: {ex.Message}");return;
    }
    world.RemoveDrop(drop.Id);
    await hub.BroadcastVisible(drop,GamePackets.Remove(drop.Id));
}

async Task ShowDowned(WorldSession session,bool appearanceSent=false){
    await InterruptGuildCapture(session,"길드마스터가 쓰러졌습니다.");
    long duration=session.Character.RevivalDueAt>0
        ?Math.Clamp(session.Character.RevivalDueAt-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),0,WorldSettings.RevivalDelayMs)
        :WorldSettings.RevivalDelayMs;
    long now=Environment.TickCount64;
    session.RevivalAt=now+duration;
    session.RevivalUnconsciousSent=false;
    long elapsed=WorldSettings.RevivalDelayMs-duration;
    session.NextStunEffectAt=now+WorldSettings.RevivalPulseMs-elapsed%WorldSettings.RevivalPulseMs;
    long stunSeconds=Math.Clamp((duration-2*WorldSettings.RevivalPulseMs+999)/1000,0,WorldSettings.RevivalStunSeconds);
    var packets=new List<byte[]>();
    if(duration>0){
        packets.Add([8,0,session.CombatMode,0]);
        if(stunSeconds>0)packets.Add(GamePackets.RevivalStun((ushort)stunSeconds));
    }
    if(!appearanceSent)packets.Add(GamePackets.Human(session.Character));
    packets.Add(GamePackets.RevivalScreen());
    packets.Add(GamePackets.RevivalVitals(session.Character,0x20,currentHp:1,padded:false));
    if(duration>0)packets.Add(GamePackets.Message("몸을 움직일 수 없습니다."));
    await WorldHub.NotifyPackets(session,packets);
}

async Task ShowUnconscious(WorldSession session){
    if(session.RevivalUnconsciousSent)return;
    session.RevivalUnconsciousSent=true;
    await WorldHub.NotifyPackets(session,[
        GamePackets.RevivalStun(0),
        GamePackets.Message("의식을 잃었습니다."),
        GamePackets.RevivalVitals(session.Character,0x20,
            padded:session.Character.Map!=2222||session.Character.ElBlessingUntil==0),
    ]);
}

byte[] FriendsPacket(Character owner,bool padded=false)=>GamePackets.FriendList((owner.Friends??[])
    .Select(characters.Find).OfType<Character>().Select(friend=>(friend.Name,hub.FindOnline(friend.Id)!=null)).ToArray(),padded);

async Task UpdateFriendPresence(uint id){
    foreach(var observer in hub.Online)
        if(observer.Character.Friends?.Contains(id)==true)await WorldHub.Notify(observer,FriendsPacket(observer.Character));
}

async Task UpdateVisibleNpcs(LocalNpc[] previous){
    static (int,int,byte,ushort,byte,byte,string) Appearance(LocalNpc n)=>(n.X,n.Y,n.Floor,n.Sprite,n.Flags,n.Type,n.Name);
    foreach(int map in previous.Select(n=>n.Map).Concat(world.Npcs.Select(n=>n.Map)).Distinct()){
        foreach(var session in hub.InMap(map)){
            var before=previous.Where(n=>n.Map==map&&(WorldHub.CanSee(session.Character,n)||
                session.RefreshedObjects.ContainsKey(n.Id))).ToDictionary(n=>n.Id);
            var after=world.InMap(map).Where(n=>WorldHub.CanSee(session.Character,n)||
                session.RefreshedObjects.ContainsKey(n.Id)&&WorldHub.CanRefreshMerchant(session.Character,n,includeDistant:true)).ToDictionary(n=>n.Id);
            foreach(var npc in before.Values){
                if(!after.TryGetValue(npc.Id,out var current))session.RefreshedObjects.TryRemove(npc.Id,out _);
                if(current==null||Appearance(current)!=Appearance(npc))
                    await WorldHub.Notify(session,GamePackets.Remove(npc.Id));
            }
            var added=after.Values.Where(n=>!before.TryGetValue(n.Id,out var old)||Appearance(old)!=Appearance(n)).ToArray();
            if(added.Length>0)await WorldHub.Notify(session,GamePackets.Objects(added));
        }
    }
}

async Task Listen(int port,string role) {
    var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
    Console.WriteLine($"{role} listening 127.0.0.1:{port}");
    try { while(!stop.IsCancellationRequested) {var c=await listener.AcceptTcpClientAsync(stop.Token);_=ServeLocal(c,role);} }
    catch(OperationCanceledException){}finally{listener.Stop();}
}
async Task ServeLocal(TcpClient client,string role){
    using(client){
        client.NoDelay=true;
        await Serve(client.GetStream(),role,shutdown:()=>client.Client.Shutdown(SocketShutdown.Send));
    }
}
async Task Serve(Stream stream,string role,SecureGateway.Connection? secure=null,Action? shutdown=null) {
    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,secure?.Revoked??CancellationToken.None)) {
        var unauthenticatedTimeout=TimeSpan.FromMinutes(5);
        timeout.CancelAfter(unauthenticatedTimeout);var ct=timeout.Token;
        var crypto=new LegacyCrypto();byte seq=0;
#if ELANCIA_RELEASE_BUNDLE
        string account=secure?.Account??"";
#else
        string account=secure?.Account??"test";
#endif
        int loginEndpoint=secure?.LoginPort??7501+offset;
        int worldEndpoint=secure?.WorldPort??worldPort;
        string id=Guid.NewGuid().ToString("N")[..8];
        bool authenticated=false;
        bool transferPending=false;
        bool creating=false;
        string? pendingName=null;
        Character? character=null;
        WorldSession? presence=null;
        WorldHandoff? outgoingHandoff=null;
        (uint Id,int X,int Y,long At)? lastBankMerchantRefresh=null;
        byte? pendingRasSlot=null;
        LocalNpc? activeShop=null;
        (uint Merchant,ushort Order,ShopOffer[] Offers)? activeShopService=null;
        uint? activeNpcDialog=null;
        string? activePursuitPath=null;
        GuildFoundingDialog? guildFounding=null;
        uint? guildFlagOffer=null;
        HairSalonDialog? hairSalon=null;
        NeruaDialog? neruaDialog=null;
        RubyJewelry? rubyDialog=null;
        OldSmithPage? oldSmithPage=null;
        ErenoaPage? erenoaPage=null;
        ushort? torionPage=null;
        TorionStage torionOfferedStage=TorionStage.None;
        RanicheStage? ranicheOffer=null;
        bool cookingChoices=false;
        CookingRecipe activeRecipe=CookingRecipe.Egg;
        MetiniPage? metiniPage=null;
        long nextSpellCast=0;
        bool castingSpell=false;
        int? conversationStage=null;
        CalasMusicLesson? calasLesson=null;
        const string rolanciaConfirmation="3901006F/3A0100DC00310104/3A0100DC01B300";
        const string rolanciaAccept=rolanciaConfirmation+"/3A0100DC01B40101";
        BankLoginStage? bankLoginStage=null;
        uint? activeBankNpc=null;
        ShrinePage? shrinePage=null;
        ShrineGrade? pendingShrineGrade=null;
        ShrineJob? shrineJob=null;
        byte? shrinePreviousFateOrder=null;
        byte shrineFateAptitude=0;
        byte guardianPage=0,guardianCertificateSlot=0;
        FameExchangeDialog? fameDialog=null;
        bool selfLookRequested=false;
        long nextCraftOpen=0;
        long nextSenseUse=0;
        long nextMeditationUse=0;
        (GrepaCombatSkill Skill,long At)? lastGrepaCombatUse=null;
        long nextConversationUse=0;
        var inventoryPositions=new System.Collections.Concurrent.ConcurrentDictionary<byte,(ushort X,ushort Y)>();
        using var sendLock=new SemaphoreSlim(1,1);
        int statusDimension=0;
        Console.WriteLine($"{role}/{id} connected");
        byte[] EncodeFrame(byte[] plain){
            var body=crypto.Encode(plain,seq,true);
            if(LegacyCrypto.ServerEncrypted(plain[0]))seq++;
            return Framing.Wrap(body);
        }
        void MarkPacketSent(byte[] packet){
            presence?.ObserveSentStatus(packet);
            if(presence is {} current&&packet.Length>=2&&packet[0]==8&&(packet[1]&0x40)!=0)
                current.TrainingHealthStatusPending=false;
            if(packet[0]==0x0F){
                int nameEnd=5+packet[4];
                var position=new PacketReader(packet.AsSpan(nameEnd+1+packet[nameEnd]+4));
                inventoryPositions[packet[1]]=(position.U16(),position.U16());
            }
            else if(packet[0]==0x10&&packet[2] is not (9 or 0x0C))inventoryPositions.TryRemove(packet[1],out _);
        }
        async Task Send(byte[] plain){
            await sendLock.WaitAsync(ct);
            try {
                plain=GamePackets.StatusForDimension(plain,statusDimension);
                await stream.WriteAsync(EncodeFrame(plain),ct);
                MarkPacketSent(plain);
                Log("S",plain);
            }finally{sendLock.Release();}
        }
        async Task SendSnapshot(List<byte[]> packets){
            await sendLock.WaitAsync(ct);
            try{
                using var batch=new MemoryStream();
                IReadOnlyList<byte[]> outgoing=statusDimension==0?packets:
                    packets.Select(packet=>GamePackets.StatusForDimension(packet,statusDimension)).ToArray();
                foreach(var packet in outgoing)batch.Write(EncodeFrame(packet));
                await stream.WriteAsync(batch.GetBuffer().AsMemory(0,(int)batch.Length),ct);
                foreach(var packet in outgoing)MarkPacketSent(packet);
                WriteLog(string.Join(Environment.NewLine,outgoing.Select(p=>FormatLog("S",p)))+Environment.NewLine);
            }finally{sendLock.Release();}
        }
        string FormatLog(string dir,byte[] p) {
            bool secret=(dir=="C"&&(p[0] is 2 or 3 or 4 or 0x0E or 0x10 or 0x19 or 0x26 or 0x42 or 0x65||
                p[0]==0x3A&&p.Length>10&&p[10]==2||p[0]==0x54&&p.Length>7&&p[1]==3&&p[6]==1||
                p[0]==0x6A&&p.Length>=4&&p[1]==3&&p[2] is 0 or 12||
                WorldBank.IsPasswordRequest(p))) ||
                (dir=="S"&&(p[0] is 3 or 0x0A or 0x0D or 0x57||p[0]==0x30&&(guildFounding!=null||fameDialog!=null)));
            var record=new{time=DateTimeOffset.UtcNow,role,id,dir,opcode=p[0],length=p.Length,hex=secret?"[redacted]":Convert.ToHexString(p.AsSpan(0,Math.Min(p.Length,256)))};
            return JsonSerializer.Serialize(record);
        }
        void Log(string dir,byte[] p)=>WriteLog(FormatLog(dir,p)+Environment.NewLine);
        static void WriteLog(string lines){
            Console.Write(lines);
            lock(typeof(Framing))File.AppendAllText("logs/server.jsonl",lines);
        }
        byte[] NewTransferTicket(uint? characterId,WorldHandoff? handoff=null){
            byte[] ticket=System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var now=DateTimeOffset.UtcNow;
            foreach(var entry in tickets)if(entry.Value.Expires<now)tickets.TryRemove(entry.Key,out _);
            tickets[Convert.ToHexString(ticket)]=(account,characterId,handoff?.Expires??now.AddMinutes(1),handoff);
            return ticket;
        }
        async Task LeaveWorld(){
            if(presence==null)return;
            await InterruptGuildCapture(presence,"",notify:false);
            if(presence.Trade is {} trade)await trade.Close("교환 상대의 연결이 종료됐습니다.");
            if(presence.Party is {} party)await party.Leave(presence);
            uint departingId=presence.Character.Id;
            await hub.Leave(presence);
            presence=null;
            await UpdateFriendPresence(departingId);
        }
        async Task SupersedeWorld(){
            // The native world screen handles a closed connection itself.
            outgoingHandoff?.Completion.TrySetCanceled();
            try{await LeaveWorld();}
            finally{
                try{if(shutdown!=null)shutdown();else stream.Dispose();}
                catch(Exception ex) when(ex is SocketException or IOException or ObjectDisposedException){}
                finally{timeout.Cancel();}
            }
        }
        async Task FinishRevival(){
            var pending=presence!.Character;
            await ShowUnconscious(presence);
            Character revived;
            try{
                int home=WorldSettings.HomeTown(pending.HomeTownMap??startLocation.Map);
                if(!CanEnterRegion(home))home=home/10000*10000+2;
                revived=characters.Revive(pending.Id,RevivalArrival(WorldSettings.RevivalShrine(home),pending.Id));
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
                Console.Error.WriteLine($"Revival persistence: {ex.Message}");return;
            }
            try{
                var recovery=GamePackets.RevivalExit(pending);
                await SendSnapshot(recovery);
                await hub.BroadcastVisible(pending,recovery[0],pending.Id);
                presence.CombatMode=1;
                await EnterMap(revived,revivalArrival:true);
                if(presence?.Party is {} party)await party.UpdateHealth(presence);
            }catch(Exception ex) when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){
                timeout.Cancel();
            }
        }
        void AppendMercenaries(List<byte[]> packets,bool initialLogin){
            if(character==null||presence==null)return;
            if(character.LeonHired&&!presence.LeonDismissed&&presence.PendingLeonArrival==null){
                var leon=presence.Leon??presence.SpawnLeon();
                if(initialLogin)packets.Add(MercenaryHiring.BindLeon(leon.Id));
                packets.Add(initialLogin?MercenaryHiring.InitialControls():MercenaryHiring.ShowControls());
                packets.Add(GamePackets.Human(leon));
            }
            foreach(var other in hub.InMap(character.Map)){
                if(other!=presence&&other.Leon is {} leon&&WorldHub.CanSee(character,leon))
                    packets.Add(GamePackets.Human(leon));
            }
        }
        (string Name,ushort? Track,byte Flags) MapEnvironment(int map)=>map<0?("Personal Room 1",null,5):
            world.ObservedMap(map) is {} observed?(observed.Name,observed.Track,observed.Flags):map switch
            {
                2 or 10002 or 20002 => ($"[{(char)('A'+map/10000)}]Town Of Rolancia",24,1),
                210 or 10210 or 20210 => ($"[{(char)('A'+map/10000)}]Rolancia-Inn",25,1),
                5 => ("[A]Rolancia-South Plain",30,5),
                215 => ("[A]Rolancia-Jewelry Shop",26,1),
                1002 or 11002 or 21002 => ($"[{(char)('A'+map/10000)}]Town Of Epiloria",33,1),
                1214 or 11214 or 21214 => ($"[{(char)('A'+map/10000)}]Epiloria-Pub",null,1),
                2002 or 12002 or 22002 => ($"[{(char)('A'+map/10000)}]Town Of Sernnica",1001,1),
                3002 or 13002 or 23002 => ($"[{(char)('A'+map/10000)}]Town Of Cronosis",null,1),
                4002 or 14002 or 24002 => ($"[{(char)('A'+map/10000)}]Town Of Popresne",1010,1),
                24219 => ("[C]Popresne-Magic Shop",1010,1),
                var shrine when WorldSettings.ShrineName(shrine) is string shrineName =>
                    ($"[{(char)('A'+shrine/10000)}]{shrineName}",(shrine%10000) switch {229=>23,2229=>1004,_=>null},1),
                _ => ($"로컬 맵 {map}",null,0),
            };
        async Task Snapshot(bool mapTransition=false,byte[]? inventoryDelta=null,bool initialLogin=false,bool southGate=false,bool revivalArrival=false,
            bool recordedArrival=false,WorldHandoff? handoff=null,bool oranFeather=false,bool keepMusic=false,
            DoorArrivalProfile? door=null,byte[]? doorOptionStatus=null,bool trainingRoomDoor=false){
            if(character==null||presence==null)return;
            lastBankMerchantRefresh=null;
            var map=world.GetMap(character.Map);
            var packets=new List<byte[]>();
            int clientMap=WorldSettings.ClientMap(character.Map);
            bool personalRoom=character.Map<0;
            bool roomOwner=personalRoom&&character.Id<=int.MaxValue&&character.Map==WorldSettings.PersonalRoomInstance(character.Id);
            if(!mapTransition&&handoff==null)
                foreach(var oid in presence.RefreshedObjects.Keys)packets.Add(GamePackets.Remove(oid));
            presence.RefreshedObjects.Clear();
            if(personalRoom&&mapTransition){
                // A key enters directly. The sign path has no item delta or sound.
                bool usedKey=inventoryDelta!=null;
                if(inventoryDelta!=null){
                    packets.Add([..GamePackets.SoundEffect(70),0]);
                    packets.Add(inventoryDelta);
                }
                presence.ExpectMapCheck(character.Map);
                packets.Add(GamePackets.Map(clientMap,map.Width,map.Height,"Personal Room 1",5,portalPadding:true));
                packets.Add(GamePackets.MapCheck(1));
                packets.Add(GamePackets.Position(character.X,character.Y,character.Floor));
                packets.Add(GamePackets.Weather(0,0));
                if(usedKey)packets.Add(GamePackets.BackgroundMusic(30,portalPadding:true));
                packets.Add(GamePackets.Human(character));
                var peers=hub.InMap(character.Map).Where(peer=>peer.Character.Id!=character.Id&&WorldHub.CanSee(character,peer.Character));
                packets.AddRange(peers.SelectMany(peer=>peer.PeerAppearance()));
                var objects=world.FieldObjects(character.Map).Where(o=>WorldHub.CanSee(character,o)).ToArray();
                if(objects.Length>0)packets.Add(GamePackets.Objects([],objects));
                var controls=GamePackets.PersonalRoomControls(roomOwner);
                packets.Add(usedKey?[..controls,0]:controls);
                AppendMercenaries(packets,initialLogin);
                await SendSnapshot(packets);
                return;
            }
            bool portalArrival=recordedArrival||door!=null;
            bool dimensionHandoff=handoff!=null;
            bool equippedLogin=initialLogin&&(character.Items??[]).Any(item=>item.InventorySlot==0);
            byte entryState=character.AwaitingRevival?(byte)2:presence.CombatMode;
            int entryDimension=handoff?.FromPersonalRoom!=true&&character.Map is >=0 and <30000?character.Map/10000:0;
            if(initialLogin)statusDimension=presence.StatusDimension=entryDimension;
            bool restoreEntryVitals=false;
            if(inventoryDelta!=null&&!portalArrival)packets.Add(inventoryDelta);
            if(!mapTransition&&!dimensionHandoff)
                foreach(var oid in presence.FarSpawns)packets.Add(GamePackets.Remove(oid));
            if(initialLogin){
                foreach(var item in (character.BankItems??[]).OrderBy(item=>item.InventorySlot))
                    packets.Add(GamePackets.BankAdd(item));
                packets.Add(GamePackets.SpellList(character));
                packets.Add(GamePackets.AbilityList(character));
                restoreEntryVitals=GamePackets.AddInitialInventory(packets,character,entryState,entryDimension);
            }else if(!mapTransition){
                int equippedWeight=EquipmentItems.EquippedWeight(character);
                long carriedWeight=0;
                foreach(var placement in character.Items??[]){
                    if(placement.InventorySlot==0){
                        packets.Add(GamePackets.EquipmentAdd(EquipmentItems.Get(placement.Key),equippedWeight));
                    }else{
                        carriedWeight=Math.Min(ushort.MaxValue,carriedWeight+EquipmentItems.InventoryWeight(placement));
                        packets.Add(GamePackets.InventoryAdd(placement,(ushort)carriedWeight));
                    }
                }
                if(character.Gold!=0&&character.WalletSlot!=0)packets.Add(GamePackets.Wallet(character,(ushort)carriedWeight));
            }
            presence.FarSpawns.Clear();
            var (name,track,flags)=MapEnvironment(character.Map);
            if(door!=null)track=door.Track;
            else if(keepMusic)track=null;
            if(!mapTransition){
                if(flags!=0){
                    var clock=world.Clock.Read(WorldTime());
                    packets.Add(GamePackets.WorldDay(clock.Month,clock.Day));
                    packets.Add(GamePackets.WorldHour(clock.Hour,clock.Minute));
                }
                // A world-to-world handoff already has a character panel in the client.
                if(initialLogin&&!dimensionHandoff)packets.Add(GamePackets.SelfLook(character,initialLogin:true));
                packets.Add(GamePackets.Self(character));
            }
            bool paddedArrival=portalArrival||dimensionHandoff;
            bool padMap=door?.MapPadding??(initialLogin||revivalArrival||recordedArrival&&!oranFeather&&
                character.Map%10000!=202&&!WorldSettings.IsArcherTrainingRoom(character.Map));
            if(door?.RefreshBuffStatus==true){
                long buffNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if(character.SpeedPotionUntil>buffNow&&character.HealthPotionUntil>buffNow)
                    packets.Add(GamePackets.DoorBuffStatus(character,entryState));
            }
            if(doorOptionStatus!=null)packets.Add(doorOptionStatus);
            packets.Add(GamePackets.Map(clientMap,map.Width,map.Height,name,flags,padMap||personalRoom));
            if(!mapTransition&&track.HasValue)packets.Add(GamePackets.BackgroundMusic(track.Value,portalPadding:initialLogin));
            presence.ExpectMapCheck(character.Map,entry:initialLogin,roomExit:handoff?.FromPersonalRoom==true);
            packets.Add(GamePackets.MapCheck(1));
            if(initialLogin)packets.Add(GamePackets.JobList(character,portalPadding:true));
            else if(!mapTransition)packets.Add(GamePackets.Status(character,vitalsExtra:presence.VitalsAux));
            // Captured in-world transitions send S15, S14, then S04 before other state.
            packets.Add(GamePackets.Position(character.X,character.Y,character.Floor));
            if(mapTransition&&!southGate&&!portalArrival&&!revivalArrival){
                if(track.HasValue)packets.Add(GamePackets.BackgroundMusic(track.Value));
                packets.Add(GamePackets.Status(character,vitalsExtra:presence.VitalsAux));
            }
            var nearby=hub.InMap(character.Map).Where(other=>other.Character.Id!=character.Id&&WorldHub.CanSee(character,other.Character));
            bool peersBeforeObjects=initialLogin||paddedArrival||revivalArrival;
            if(peersBeforeObjects)packets.AddRange(nearby.SelectMany(other=>other.PeerAppearance()));
            else packets.Add(GamePackets.Human(character));
            var npcs=world.InMap(character.Map).Where(n=>WorldHub.CanSee(character,n)).ToArray();
            var field=world.FieldObjects(character.Map).Where(o=>WorldHub.CanSee(character,o)).ToArray();
            if(door?.OmitEmptyObjects!=true||npcs.Length!=0||field.Length!=0){
                var objects=GamePackets.Objects(npcs,field);
                packets.Add(trainingRoomDoor&&character.Map%10000==298?[..objects,0]:objects);
            }
            if(flags!=0)packets.Add(GamePackets.Weather(0,0));
            if((southGate||portalArrival||revivalArrival)&&track.HasValue)packets.Add(GamePackets.BackgroundMusic(track.Value,portalPadding:true));
            if(equippedLogin){
                packets.Add(GamePackets.EntryArrivalStatus(character,entryState,entryDimension));
                // The initial status starts the auxiliary gauge at zero.
                if(restoreEntryVitals)packets.Add([..GamePackets.CurrentVitals(character,entryState,vitalsExtra:0),0]);
            }
            else if(initialLogin)packets.Add(GamePackets.InitialStatus(character,entryState,entryDimension));
            if(peersBeforeObjects){
                var self=GamePackets.Human(character);
                packets.Add(trainingRoomDoor?[..self,0]:self);
                if(paddedArrival&&inventoryDelta!=null)packets.Add(inventoryDelta);
                if(revivalArrival)packets.Add(GamePackets.RevivalVitals(character,1));
            }
            if(initialLogin||mapTransition){
                long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var active=PlayerDebuff.All.Where(debuff=>debuff.Deadline(character)>now).ToArray();
                foreach(var debuff in active)
                    packets.Add(GamePackets.TimedSpell(debuff.Timer(debuff.Remaining(character,now)),padded:false));
                var restored=character with {SlowUntil=0,CurseUntil=0};
                foreach(var debuff in active){
                    restored=debuff.WithDeadline(restored,debuff.Deadline(character));
                    packets.Add(debuff==PlayerDebuff.Slow
                        ?GamePackets.PaddedStatus(restored,0x40,entryState)
                        :GamePackets.Status(restored,0x40,entryState));
                    packets.Add(GamePackets.Message(debuff.Spell.Name));
                }
            }
            if(!mapTransition)packets.Add(GamePackets.PartyAllowed(character.PartyAllowed));
            if(!peersBeforeObjects)packets.AddRange(nearby.SelectMany(other=>other.PeerAppearance()));
            if(personalRoom)packets.Add(GamePackets.PersonalRoomControls(roomOwner));
            AppendMercenaries(packets,initialLogin);
            await SendSnapshot(packets);
            if(!initialLogin&&!mapTransition&&presence.Party is {} currentParty)await currentParty.SendRoster(presence);
        }
        async Task ChangeEquipment(byte slot,bool wear,(ushort X,ushort Y)? destination=null,bool direct=false){
            if(character==null||presence==null||slot==0||character.Items is not { Length: > 0 } items)return;
            bool knewFishing=character.FishingLearned;
            var next=items.ToArray();
            int index=Array.FindIndex(next,p=>wear ? p.InventorySlot==slot
                : p.InventorySlot==0&&EquipmentItems.Get(p.Key).EquipmentSlot==slot);
            if(index<0)return;

            var selected=next[index];
            var item=EquipmentItems.Get(selected.Key);
            if(item.EquipmentSlot==0)return;
            if(wear){
                if(EquipmentItems.CurrentDurability(selected,EquipmentItems.MaximumDurability(item))==0){
                    await Send(item.EquipmentSlot==1?GamePackets.WeaponBrokenNotice():GamePackets.Message("장비가 망가졌습니다."));return;
                }
                if(item.EquipmentSlot==3&&EquipmentItems.EquippedWeapon(character) is {} weapon&&
                    EquipmentItems.UsesBothHands(EquipmentItems.Get(weapon.Key))){
                    await Send(GamePackets.Message("양손 무기를 착용한 상태에서는 방패를 착용할 수 없습니다."));
                    return;
                }
                if(EquipmentItems.UsesBothHands(item)&&next.Any(p=>p.InventorySlot==0&&EquipmentItems.Get(p.Key).EquipmentSlot==3)){
                    await Send(GamePackets.Message("방패를 벗은 뒤 양손 무기를 착용해 주세요."));
                    return;
                }
                int occupied=Array.FindIndex(next,p=>p.InventorySlot==0&&EquipmentItems.Get(p.Key).EquipmentSlot==item.EquipmentSlot);
                next[index]=selected with {InventorySlot=0};
                var previous=character;
                ItemPlacement? initialReturn=null;
                Character? cleared=null;
                if(occupied>=0){
                    byte bagSlot=previous.FreeInventorySlots(next).First();
                    var (oldX,oldY)=inventoryPositions.GetValueOrDefault(bagSlot);
                    initialReturn=next[occupied] with {InventorySlot=bagSlot,InventoryWord1=oldX,InventoryWord2=oldY};
                    ushort x=(ushort)Random.Shared.Next(25,225),y=(ushort)Random.Shared.Next(15,86);
                    if(x==oldX&&y==oldY)x=(ushort)(x==224?25:x+1);
                    next[occupied]=initialReturn with {InventoryWord1=x,InventoryWord2=y};
                    cleared=CharacterStore.ApplyEquipment(previous,[..next.Where((_,position)=>position!=index)]);
                }
                try{character=characters.SetItems(character.Id,next);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Equipment wear save: {ex.Message}");
                    await Send(GamePackets.Message("장비 착용을 저장하지 못했습니다."));
                    return;
                }
                presence.Character=character;
                int equippedWeight=EquipmentItems.EquippedWeight(character);
                ushort carriedWeight=EquipmentItems.CarriedWeight(character);
                var equipped=GamePackets.EquipmentAdd(item,equippedWeight);
                var appearance=GamePackets.Human(character);
                byte state=presence.CombatMode;
                uint? wearEffectFlags=character.SpeedPotionUntil>0?presence.StatusEffectFlags:null;
                byte[] EquipmentDelta(Character from,Character to,bool padded=true){
                    var baseStatus=GamePackets.Status(to,0x40,state);
                    bool baseChanged=!baseStatus.AsSpan().SequenceEqual(GamePackets.Status(from,0x40,state));
                    bool vitalsChanged=!GamePackets.CurrentVitals(to).AsSpan().SequenceEqual(GamePackets.CurrentVitals(from));
                    byte mask=vitalsChanged?(byte)0x60:baseChanged?(byte)0x40:(byte)0;
                    if(to.SpeedPotionUntil>0||to.HealthPotionUntil>0)
                        return GamePackets.PotionBuffStatus(to,mask,padded:padded,state:state,vitalsExtra:presence.VitalsAux,
                            effectFlags:wearEffectFlags);
                    return mask switch {0=>[8,0,state,0],0x40=>[..baseStatus,0],
                        _=>[..GamePackets.Status(to,mask,state,vitalsExtra:presence.VitalsAux),0]};
                }
                var equipmentLearning=new List<byte[]>();
                var equippedAbility=CombatAbilities.For(character);
                if(!CombatAbilities.Has(previous,equippedAbility)&&CombatAbilities.Has(character,equippedAbility)){
                    var ability=equippedAbility;
                    if(ability==CombatAbilities.Greatsword&&!direct)
                        equipmentLearning.AddRange(GamePackets.GreatswordLearned(character));
                    else{
                        equipmentLearning.Add(GamePackets.AbilityMastery(character,ability));
                        equipmentLearning.Add(GamePackets.AbilityProgress(character,ability));
                    }
                }
                bool learnedFishing=!knewFishing&&character.FishingLearned;
                if(learnedFishing&&!direct)equipmentLearning.AddRange(GamePackets.FishingLearned(character));
                if(initialReturn!=null&&cleared!=null){
                    var placed=GamePackets.InventoryAdd(next[occupied],carriedWeight);
                    byte[] inventoryFinish=initialReturn.InventorySlot==slot?[..placed,0]:
                        GamePackets.ConsumableRemove(slot,carriedWeight,mode:0x0C,padded:true);
                    var prelude=GamePackets.EquipmentRemovalPrelude(previous,cleared,EquipmentDelta(previous,cleared));
                    await SendSnapshot([..equipmentLearning,..prelude,[..GamePackets.EquipmentRemove(item.EquipmentSlot,EquipmentItems.EquippedWeight(cleared)),0],
                        GamePackets.Human(cleared),[..GamePackets.InventoryAdd(initialReturn,carriedWeight),0],
                        [..placed,0],[..equipped,0],[..appearance,0],equipped,[..appearance,0],inventoryFinish,EquipmentDelta(cleared,character)]);
                    await hub.BroadcastVisible(character,appearance,except:character.Id);
                }else if(direct){
                    byte[] removal=Fishing.HasRod(character)?GamePackets.ConsumableRemove(slot,carriedWeight,mode:0x0C,padded:true)
                        :GamePackets.InventoryRemove(slot,carriedWeight);
                    await SendSnapshot([..equipmentLearning,removal,equipped]);
                    await hub.BroadcastVisible(character,appearance);
                }else{
                    // A candle worn in normal mode during speed sends the compact effect refresh.
                    bool padded=state!=1||character.SpeedPotionUntil==0||item is not {Sprite:0x8014,Effect:null};
                    var delta=EquipmentDelta(previous,character,padded);
                    byte[] refreshed=(delta[1]&0x40)!=0?[..appearance,0]:appearance;
                    byte[] added=[..equipped,0];
                    // C1C sends both equipment and self-appearance updates before clearing the bag slot.
                    await SendSnapshot([..equipmentLearning,added,refreshed,added,refreshed,
                        GamePackets.ConsumableRemove(slot,carriedWeight,mode:0x0C,padded:true),delta]);
                    await hub.BroadcastVisible(character,appearance,except:character.Id);
                }
                if(learnedFishing&&direct)
                    await SendSnapshot([GamePackets.FishingMastery(character),GamePackets.FishingProgress(character)]);
                if(occupied<0&&direct)await Send(GamePackets.Status(character,vitalsExtra:presence.VitalsAux));
            }else{
                byte free=character.FreeInventorySlots(next).FirstOrDefault();
                if(free==0){await Send(GamePackets.Message("소지품 창에 빈 칸이 없습니다."));return;}
                var previous=character;
                var (initialX,initialY)=inventoryPositions.GetValueOrDefault(free);
                var initial=selected with {InventorySlot=free,InventoryWord1=initialX,InventoryWord2=initialY};
                next[index]=initial with {InventoryWord1=destination?.X??initial.InventoryWord1,
                    InventoryWord2=destination?.Y??initial.InventoryWord2};
                try{character=characters.SetItems(character.Id,next);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Equipment removal save: {ex.Message}");
                    await Send(GamePackets.Message("장비 해제를 저장하지 못했습니다."));
                    return;
                }
                presence.Character=character;
                bool candle=item is {Sprite:0x8014,Name:"양초",Effect:null};
                var packets=GamePackets.EquipmentStow(previous,character,slot,initial,next[index],presence.CombatMode,presence.VitalsAux,
                    effectFlags:previous.SpeedPotionUntil>0?presence.StatusEffectFlags:null,candle:candle);
                await SendSnapshot(packets);
                await hub.BroadcastVisible(character,GamePackets.Human(character),except:character.Id);
                return;
            }
        }
        async Task MoveInventoryItem(byte slot,ushort x,ushort y,bool bundle=false){
            if(character==null||presence==null||slot==0)return;
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(slot==character.WalletSlot&&character.Gold!=0){
                try{character=characters.MoveWallet(character.Id,x,y);presence.Character=character;}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"{role}/{id} wallet position save failed: {ex.Message}");
                }
                await Send(GamePackets.Wallet(character,carriedWeight));
                return;
            }
            if(character.Items is not {} items)return;
            int index=Array.FindIndex(items,item=>item.InventorySlot==slot);
            if(index<0)return;
            var current=items[index];
            var moved=current with {InventoryWord1=x,InventoryWord2=y};
            if(moved!=current){
                var placements=items.ToArray();
                placements[index]=moved;
                if(!SaveInventoryLayout(placements)){
                    await Send(bundle?GamePackets.InventoryAdd(current,carriedWeight):GamePackets.InventoryDrag(current,carriedWeight));
                    return;
                }
            }
            if(!bundle){await Send(GamePackets.InventoryDrag(moved,carriedWeight));return;}
            var final=GamePackets.InventoryAdd(moved,carriedWeight);
            if(moved!=current)
                await SendSnapshot([[..GamePackets.InventoryAdd(current,carriedWeight),0],[..final,0],final]);
            else await Send(final);
        }
        bool SaveInventoryLayout(ItemPlacement[] placements){
            try{character=characters.SetItems(character!.Id,placements);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} inventory layout save failed: {ex.Message}");
                return false;
            }
            presence!.Character=character;
            return true;
        }
        async Task MoveInventoryBundle(byte sourceSlot,byte destinationSlot,ushort x,ushort y,uint quantity){
            if(character==null||presence==null||sourceSlot==0)return;
            if(sourceSlot==character.WalletSlot&&character.Gold!=0){
                if(destinationSlot==0&&quantity==character.Gold)await MoveInventoryItem(sourceSlot,x,y);
                else await Send(GamePackets.Wallet(character,EquipmentItems.CarriedWeight(character)));
                return;
            }
            if(character.Items is not {} items)return;
            var source=items.SingleOrDefault(item=>item.InventorySlot==sourceSlot);
            if(source==null)return;
            if(quantity==0||quantity>source.Quantity||sourceSlot==destinationSlot){
                await Send(GamePackets.Message("옮길 수량과 위치를 다시 확인해 주세요."));
                return;
            }
            bool relocateWhole=destinationSlot==0&&quantity==source.Quantity;
            var item=EquipmentItems.Get(source.Key);
            bool stackable=item.EquipmentSlot==0&&item.InventoryFlag1!=0;
            if(!relocateWhole&&!stackable){
                await Send(GamePackets.Message(destinationSlot==0?"이 소지품은 나눌 수 없습니다.":"이 소지품은 합칠 수 없습니다."));
                return;
            }
            var next=items.ToList();
            ItemPlacement target;
            if(destinationSlot==0){
                byte free=character.FreeInventorySlots(items).FirstOrDefault();
                if(relocateWhole&&(free==0||free>sourceSlot||source.InventoryWord1==x&&source.InventoryWord2==y)){
                    await MoveInventoryItem(sourceSlot,x,y,bundle:true);
                    return;
                }
                if(free==0){await Send(GamePackets.Message("소지품 창에 빈 칸이 없습니다."));return;}
                target=source with {InventorySlot=free,Quantity=quantity,InventoryWord1=x,InventoryWord2=y};
                next.Add(target);
            }else{
                int index=next.FindIndex(item=>item.InventorySlot==destinationSlot);
                if(index<0||next[index].Key!=source.Key||next[index].ExpiresAt!=source.ExpiresAt
                    ||next[index].Quantity>uint.MaxValue-quantity){
                    await Send(GamePackets.Message("이 소지품은 합칠 수 없습니다."));
                    return;
                }
                target=next[index] with {Quantity=next[index].Quantity+quantity};
                next[index]=target;
            }
            var remaining=source with {Quantity=source.Quantity-quantity};
            if(remaining.Quantity==0)next.Remove(source);
            else next[next.IndexOf(source)]=remaining;
            if(!SaveInventoryLayout(next.ToArray())){
                await Send(relocateWhole?GamePackets.InventoryAdd(source,EquipmentItems.CarriedWeight(character)):
                    GamePackets.Message("소지품 배치를 저장하지 못했습니다."));
                return;
            }
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(relocateWhole){
                inventoryPositions.TryGetValue(target.InventorySlot,out var oldPosition);
                var initial=target with {InventoryWord1=oldPosition.X,InventoryWord2=oldPosition.Y};
                await SendSnapshot([[..GamePackets.InventoryAdd(initial,carriedWeight),0],
                    [..GamePackets.InventoryRemove(sourceSlot,carriedWeight),0],[..GamePackets.InventoryAdd(target,carriedWeight),0]]);
                return;
            }
            await SendSnapshot([remaining.Quantity==0?GamePackets.InventoryRemove(sourceSlot,carriedWeight):GamePackets.InventoryAdd(remaining,carriedWeight),
                GamePackets.InventoryAdd(target,carriedWeight)]);
        }
        async Task HandleBank(byte[] packet){
            if(character==null||presence==null||activeBankNpc is not uint bankerId)return;
            var banker=world.InMap(character.Map).FirstOrDefault(n=>n.Id==bankerId);
            if(banker==null||banker.Floor!=character.Floor||Math.Max(Math.Abs(banker.X-character.X),Math.Abs(banker.Y-character.Y))>20){
                activeBankNpc=null;
                return;
            }
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 보관함을 이용해 주세요."));return;}
            var action=(StorageAction)packet[2];
            byte sourceSlot;
            if(action==StorageAction.Quote){
                if(packet.Length!=8)return;
                var quote=new PacketReader(packet.AsSpan(3));
                sourceSlot=quote.U8();uint quantity=quote.U32();
                if(sourceSlot!=0&&quantity!=0&&character.Items?.Any(item=>item.InventorySlot==sourceSlot&&item.Quantity>=quantity)==true)
                    await Send(GamePackets.BankQuote(sourceSlot,quantity));
                else await Send(GamePackets.Message("보관할 소지품과 수량을 확인해 주세요."));
                return;
            }
            if(packet.Length!=13||action>StorageAction.Rearrange)return;
            var moveRequest=new PacketReader(packet.AsSpan(3));
            sourceSlot=moveRequest.U8();byte destinationSlot=moveRequest.U8();
            ushort x=moveRequest.U16(),y=moveRequest.U16();
            if(WorldBank.Move(character,action,sourceSlot,destinationSlot,x,y,moveRequest.U32()) is not {} move){
                await Send(GamePackets.Message("옮길 아이템과 수량, 빈 칸을 확인해 주세요."));
                return;
            }
            try{character=characters.SaveBankMove(character.Id,move);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} bank save failed: {ex.Message}");
                await Send(GamePackets.Message("보관함 변경을 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(action==StorageAction.Deposit){
                await Send(move.Remaining is {} remaining?GamePackets.InventoryAdd(remaining,carriedWeight):GamePackets.InventoryRemove(sourceSlot,carriedWeight));
            }else if(move.Destination.InventorySlot!=sourceSlot||action==StorageAction.Withdraw){
                await Send(move.Remaining is {} remaining?GamePackets.BankAdd(remaining):GamePackets.BankRemove(sourceSlot));
            }
            await Send(action==StorageAction.Withdraw?GamePackets.InventoryAdd(move.Destination,carriedWeight):GamePackets.BankAdd(move.Destination));
        }
        async Task HandleLeonInventory(byte[] packet){
            if(character is not {LeonHired:true,AwaitingRevival:false} owner||
                presence is not {Ready:true,Leon:{} leon}||owner.Map!=leon.Map||packet.Length!=13)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 용병 가방을 이용해 주세요."));return;}
            var action=(StorageAction)packet[2];
            var request=new PacketReader(packet.AsSpan(3));
            byte source=request.U8(),destination=request.U8();
            ushort x=request.U16(),y=request.U16();
            var before=owner.LeonItems??[];
            if(InventoryStorage.Move(owner,before,action,source,destination,x,y,request.U32()) is not {} move)return;
            try{character=characters.SaveLeonMove(owner.Id,move);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} mercenary inventory save failed: {ex.Message}");
                await Send(GamePackets.Message("용병 가방의 변경을 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            ushort bagWeight=EquipmentItems.CarriedWeight(character),leonWeight=MercenaryHiring.InventoryWeight(move.Storage);
            var target=move.Destination;
            if(action==StorageAction.Deposit){
                var previous=before.FirstOrDefault(item=>item.InventorySlot==target.InventorySlot);
                var initial=target with {InventoryWord1=previous?.InventoryWord1??0,InventoryWord2=previous?.InventoryWord2??0};
                var placed=MercenaryHiring.InventoryAdd(target,leonWeight);
                var bagUpdate=move.Remaining is {} remaining?GamePackets.InventoryAdd(remaining,bagWeight):
                    [..GamePackets.InventoryRemove(source,bagWeight),0];
                // Retail sends two mercenary rows before the bag update and repeats the final mercenary row.
                await SendSnapshot([MercenaryHiring.InventoryAdd(initial,leonWeight),placed,bagUpdate,placed]);
                return;
            }
            var sourceUpdate=move.Remaining is {} sourceItem?MercenaryHiring.InventoryAdd(sourceItem,leonWeight):
                MercenaryHiring.InventoryRemove(source,leonWeight);
            if(action==StorageAction.Withdraw){
                await SendSnapshot([sourceUpdate,GamePackets.InventoryAdd(target,bagWeight)]);
            }else{
                var placed=MercenaryHiring.InventoryAdd(target,leonWeight);
                await SendSnapshot(target.InventorySlot==source?[placed]:[sourceUpdate,placed]);
            }
        }
        async Task HandleBankLogin(LocalNpc banker,ushort menu,byte[] packet){
            if(activeNpcDialog!=banker.Id||packet[1]!=1||character==null||presence==null)return;
            if(menu==WorldBank.Close&&packet.Length==8){
                bankLoginStage=null;
                activeBankNpc=null;
                return;
            }
            if(menu==WorldBank.Storage&&packet.Length==8&&bankLoginStage==BankLoginStage.OpenStorage){
                bankLoginStage=null;
                activeBankNpc=banker.Id;
                await Send(GamePackets.BankContents(banker.Id,character.BankItems??[]));
                return;
            }
            bool creating=bankLoginStage==BankLoginStage.NewPassword&&menu==WorldBank.NewPassword;
            if(!creating&&(bankLoginStage!=BankLoginStage.Password||menu!=WorldBank.Login))return;
            if(!WorldBank.TryPassword(packet,out byte[] password)){
                await Send(WorldBank.Dialog(banker,creating?WorldBank.NewPasswordRetry:WorldBank.LoginPrompt,
                    "은행 비밀번호는 숫자 1~8자리입니다."));
                return;
            }
            try{
                if(creating){
                    try{character=presence.Character=characters.SetBankPassword(character.Id,WorldBank.Protect(password));}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                        await Send(WorldBank.Dialog(banker,WorldBank.NewPasswordRetry,"비밀번호를 저장하지 못했습니다. 다시 입력해 주세요."));
                        return;
                    }
                    bankLoginStage=BankLoginStage.Password;
                    await Send(WorldBank.Dialog(banker,WorldBank.LoginPrompt,"설정한 비밀번호를 입력해 주세요."));
                }else if(character.BankPassword is {} saved&&WorldBank.Verify(saved,password)){
                    bankLoginStage=BankLoginStage.OpenStorage;
                    await Send(WorldBank.Dialog(banker,WorldBank.Welcome,"어서오세요. 일랜시아의 은행입니다!"));
                }else{
                    await Send(WorldBank.Dialog(banker,WorldBank.LoginPrompt,"비밀번호가 틀렸습니다."));
                }
            }finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(password);}
        }
        void ClearNpcDialogs(){
            activeShop=null;
            activeShopService=null;
            activeNpcDialog=null;
            activePursuitPath=null;
            guildFounding=null;
            guildFlagOffer=null;
            hairSalon=null;
            neruaDialog=null;
            rubyDialog=null;
            oldSmithPage=null;
            erenoaPage=null;
            torionPage=null;
            ranicheOffer=null;
            cookingChoices=false;
            metiniPage=null;
            conversationStage=null;
            calasLesson=null;
            bankLoginStage=null;
            activeBankNpc=null;
            shrinePage=null;
            pendingShrineGrade=null;
            shrineJob=null;
            shrinePreviousFateOrder=null;
            shrineFateAptitude=0;
            fameDialog=null;
            guardianPage=0;
            guardianCertificateSlot=0;
        }
        async Task EnterMap(Character updated,byte[]? inventoryDelta=null,bool southGate=false,bool revivalArrival=false,
            bool recordedArrival=false,bool oranFeather=false,DoorArrivalProfile? door=null,bool rasFeather=false){
            var previous=character!;
            pendingRasSlot=null;
            castingSpell=false;
            ClearNpcDialogs();
            await CloseCraft();
            if(presence!.Trade is {} trade)await trade.Close("맵이 바뀌어 교환을 취소했습니다.");
            if(previous.Map!=updated.Map||previous.X!=updated.X||previous.Y!=updated.Y||previous.Floor!=updated.Floor)
                await InterruptGuildCapture(presence,"깃발 앞을 벗어났습니다.");
            presence!.Ready=false;
            byte leonDirection=presence.Leon?.Direction??presence.PendingLeonArrival?.Direction??0;
            await hub.RemoveLeon(presence);
            presence.RequestedRoomSign=null;
            if(previous.Map<0&&updated.Map>=0)await Send(GamePackets.ClosePersonalRoomControls());
            character=updated;
            presence.Character=updated;
            if(rasFeather&&updated.LeonHired&&!presence.LeonDismissed)
                presence.PendingLeonArrival=(Environment.TickCount64+MercenaryHiring.RasArrivalDelayMs,leonDirection);
            byte sourceMode=presence.CombatMode;
            byte[]? doorOptionStatus=null;
            if(updated.StatusEffectBit0==true&&updated.SpeedPotionUntil==0&&door?.OptionStatuses is {Length:>0} samples){
                var variants=samples.DistinctBy(sample=>(sample.Mask,sample.Flags)).Take(2).ToArray();
                var matching=variants.Length==1?variants:samples.Where(sample=>sample.Direction==updated.Direction)
                    .DistinctBy(sample=>(sample.Mask,sample.Flags)).Take(2).ToArray();
                if(matching.Length==1)
                    doorOptionStatus=GamePackets.DoorOptionStatus(updated,sourceMode,presence.VitalsAux,matching[0]);
            }
            bool resetCombat=doorOptionStatus==null&&sourceMode!=1&&previous.Map!=updated.Map&&
                (WorldSettings.AllowsDuel(previous.Map)||WorldSettings.AllowsDuel(updated.Map));
            if(resetCombat)presence.CombatMode=1;
            bool archerTrainingDoor=WorldData.IsArcherTrainingDoor(previous.Map,updated.Map);
            bool trainingRoomDoor=recordedArrival&&archerTrainingDoor&&WorldSettings.IsArcherTrainingRoom(previous.Map)&&
                WorldSettings.IsArcherTrainingRoom(updated.Map);
            await Snapshot(mapTransition:true,inventoryDelta:inventoryDelta,southGate:southGate,revivalArrival:revivalArrival,
                recordedArrival:recordedArrival,oranFeather:oranFeather,
                keepMusic:archerTrainingDoor,door:door,doorOptionStatus:doorOptionStatus,trainingRoomDoor:trainingRoomDoor);
            if(resetCombat)await Send(GamePackets.CombatMode(updated.Id,1));
            await hub.RemovePeerAppearance(previous);
            presence.Ready=true;
            await hub.BroadcastAppearance(presence);
        }
        int PersonalRoomMenuDestination(Character occupant)=>occupant.PersonalRoomExitMap is null or 2
            ?10002:occupant.PersonalRoomExitMap.Value;
        async Task EnterPersonalRoom(uint owner,ItemPlacement? key=null){
            if(character is not {Map:>=0,AwaitingRevival:false} visitor||presence?.Trade!=null||owner>int.MaxValue)return;
            if(!world.GetMap(WorldSettings.PersonalRoomMap).CanStand(5,5,2))return;
            StartLocation arrival;
            try{arrival=RevivalArrival(new(WorldSettings.PersonalRoomInstance(owner),5,5,2),visitor.Id);}
            catch(InvalidDataException){return;}
            int exit=visitor.Map<30000?visitor.Map/10000*10000+2:2;
            Character updated;
            try{
                var inventory=key==null?null:ConsumeOne(visitor.Items!,key);
                updated=characters.EnterPersonalRoom(visitor.Id,arrival,exit,inventory);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Personal room entry failed ({ex.GetType().Name}): {ex.Message}");
                return;
            }
            byte[]? inventoryDelta=key==null?null:ConsumedInventory(updated,key,mode:9);
            await EnterMap(updated,inventoryDelta);
        }
        async Task LeavePersonalRoom(int destination,bool fromMenu=true){
            if(character is not {Map:<0,AwaitingRevival:false} occupant||
                fromMenu&&destination!=PersonalRoomMenuDestination(occupant))return;
            var desired=destination switch {
                10002=>new StartLocation(10002,159,236,1),
                _=>StartLocation.Rolancia with {Map=destination}
            };
            StartLocation arrival;
            try{arrival=RevivalArrival(desired,occupant.Id);}
            catch(InvalidDataException){return;}
            if(SavePosition(occupant,arrival.X,arrival.Y,occupant.Direction,arrival.Map,arrival.Floor) is not {} moved)return;
            if(fromMenu&&(occupant.PersonalRoomExitMap??2)!=destination)
                await BeginWorldHandoff(moved,roomMenuExit:true);
            else
                await EnterMap(moved,recordedArrival:true);
        }
        async Task MovePersonalRoom(uint? owner){
            character=presence?.Character;
            try{
                if(owner is uint ownerId)await EnterPersonalRoom(ownerId);
                else if(character is {Map:<0} visitor)await LeavePersonalRoom(visitor.PersonalRoomExitMap??2,fromMenu:false);
            }catch(Exception ex) when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){
                timeout.Cancel();
            }
        }
        async Task HandleRoomGuestCommand(byte[] packet,Character owner){
            if(owner.Id>int.MaxValue||owner.Map!=WorldSettings.PersonalRoomInstance(owner.Id)||
                packet.Length<5||packet[3]!=packet.Length-4||packet[3]>24)return;
            string name;
            try{name=new PacketReader(packet.AsSpan(3)).Str8();}
            catch(DecoderFallbackException){return;}
            if(name.Length>12||hub.FindOnline(name) is not {} guest||guest.Character.Id==owner.Id||
                guest.MovePersonalRoom is not {} move)return;
            if(packet[2]==12){
                if(guest.Character.Map==owner.Map)await move(null);
                return;
            }
            if(guest.Character.Map>=0&&guest.RequestedRoomSign is uint signId&&
                world.InspectRoomSign(signId,Environment.TickCount64) is {Open:true} sign&&
                sign.Owner==owner.Id&&WorldHub.CanRefreshRoomSign(guest.Character,sign.Sign))
                await move(owner.Id);
        }
        async Task UseItem(byte slot){
            if(character==null||presence==null||slot==0||character.Items is not { } items)return;
            var selected=items.SingleOrDefault(p=>p.InventorySlot==slot);
            if(selected==null||selected.Quantity==0)return;
            if(selected.Key is MercenaryHiring.LeonDeed or MercenaryHiring.SummonScroll){
                bool hire=selected.Key==MercenaryHiring.LeonDeed;
                if(character.AwaitingRevival||presence.Trade!=null||!presence.Ready||character.LeonHired==hire||
                    presence.Leon!=null||presence.PendingLeonArrival!=null)return;
                Character? summoned;
                try{summoned=characters.ConsumeLeonSummonItem(character.Id,slot,hire);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Mercenary summon save failed: {ex.Message}");
                    await Send(GamePackets.Message(hire?"용병 고용을 저장하지 못했습니다.":"용병 소환을 저장하지 못했습니다."));
                    return;
                }
                if(summoned==null)return;
                character=presence.Character=summoned;
                presence.LeonDismissed=false;
                var leon=presence.SpawnLeon();
                var appearance=GamePackets.Human(leon);
                await SendSnapshot([MercenaryHiring.BindLeon(leon.Id),appearance,appearance,
                    ConsumedInventory(summoned,selected,mode:6),MercenaryHiring.InitialControls()]);
                await hub.BroadcastVisible(leon,appearance,summoned.Id);
                return;
            }
            if(selected.Key is GuardianQuest.Certificate or GuardianQuest.Badge){
                if(character.AwaitingRevival||presence.Trade!=null)return;
                ClearNpcDialogs();
                if(selected.Key==GuardianQuest.Certificate){
                    if(character.Guardian?.GuardianId is uint guardianId){
                        await Send(GamePackets.Message($"수호천사: {characters.Find(guardianId)?.Name??"알 수 없음"}"));
                        return;
                    }
                    guardianCertificateSlot=slot;
                    guardianPage=1;
                    await Send(GamePackets.NpcPursuitPage(GuardianQuest.CertificateSpeaker,"수호천사를 임명하시겠습니까?",
                        ["수호천사 임명하기","닫기"],page:1,script:GuardianQuest.Script));
                }else await ShowGuardianBadge();
                return;
            }
            if(selected.Key==CookingRecipe.KitKey){
                if(character.AwaitingRevival||presence.CraftStation!=null)return;
                if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 요리해 주세요."));return;}
                var kitchen=world.InMap(character.Map).FirstOrDefault(n=>CookingRecipe.IsKitchen(n)&&NearCraftStation(character,n));
                if(kitchen==null){await Send(GamePackets.Message("로랜시아 식료품점의 카딜라 가까이에서 요리키트를 사용해 주세요."));return;}
                ClearNpcDialogs();
                activeNpcDialog=kitchen.Id;
                cookingChoices=true;
                await Send(GamePackets.NpcPursuitPage(kitchen,"어떤 요리를 만들겠어요?",
                    CookingRecipe.All.Select(r=>r.Name).ToArray(),script:CookingRecipe.Script));
                return;
            }
            if(LocalSpell.All.FirstOrDefault(spell=>spell.Teaches(selected.Key)) is {} spell){
                if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 마법을 배워 주세요."));return;}
                SpellResult result;
                try{(character,result)=characters.LearnSpell(character.Id,slot,spell);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await Send(GamePackets.Message($"{spell.Name} 습득을 저장하지 못했습니다."));
                    return;
                }
                if(result==SpellResult.AlreadyLearned){await Send(GamePackets.Message($"이미 {spell.Name} 마법을 배웠습니다."));return;}
                if(result!=SpellResult.Applied)return;
                presence.Character=character;
                var remaining=character.Items?.SingleOrDefault(item=>item.InventorySlot==slot);
                ushort carriedWeight=EquipmentItems.CarriedWeight(character);
                await SendSnapshot([remaining==null?GamePackets.InventoryRemove(slot,carriedWeight):GamePackets.InventoryAdd(remaining,carriedWeight),
                    GamePackets.LearnSpell(spell,character)]);
                return;
            }
            if(selected.Key==Fishing.BaitKey){
                // C1C consumes one worm directly (1:73980 and 1:74006).
                // The separate C13 cast is what advances fishing mastery.
                if(presence.Trade!=null||character.AwaitingRevival||Fishing.Remaining(selected)==0)return;
                var (remaining,baitUpdate)=SpendFishingBait(items,selected,padded:false);
                try{character=presence.Character=characters.SetItems(character.Id,remaining);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await Send(GamePackets.Message("미끼 사용을 저장하지 못했습니다."));
                    return;
                }
                await SendSnapshot([[8,0,1,0],baitUpdate]);
                return;
            }
            if(selected.Key=="ras-feather"){
                pendingRasSlot=slot;
                await Send(GamePackets.OpenTeleportMenu());
                return;
            }
            var item=EquipmentItems.Get(selected.Key);
            if(item is {Sprite:0x8EF0,Name:"개인방열쇠"}){
                await EnterPersonalRoom(character.Id,selected);
                return;
            }
            if(PersonalRoomSign.FromItem(item) is {} signKind){
                if(character.Map<0||character.AwaitingRevival||presence.Trade!=null)return;
                if(character.Map%10000==4216){
                    await Send(GamePackets.Message("이곳에 설치할 수 없습니다"));
                    return;
                }
                var (dx,dy)=LocalMap.Delta(character.Direction);
                int x=character.X+dx,y=character.Y+dy;
                if(!world.GetMap(character.Map).CanStand(x,y,character.Floor)||
                    world.StepDestination(character.Map,x,y,character.Floor)!=null||
                    world.IsOccupied(character.Map,x,y,character.Floor)||
                    world.HasRoomSign(character.Map,x,y,character.Floor)||
                    hub.IsOccupied(character.Map,x,y,character.Floor,character.Id))return;
                try{character=presence.Character=characters.SetItems(character.Id,ConsumeOne(items,selected));}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Room sign inventory save failed ({ex.GetType().Name}): {ex.Message}");
                    return;
                }
                var sign=world.PlaceRoomSign(character,x,y,signKind,Environment.TickCount64);
                await hub.BroadcastVisible(sign,[..GamePackets.Objects([],[sign]),0]);
                await Send(ConsumedInventory(character,selected,padded:signKind.PaddedConsumption));
                return;
            }
            if(selected.Key==ElBlessing.WrappedItemKey){
                if(character.AwaitingRevival||presence.Trade!=null)return;
                ItemPlacement? unwrapped;
                try{(character,unwrapped)=characters.UnwrapElBlessing(character.Id,slot,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Blessing package save failed: {ex.Message}");
                    return;
                }
                presence.Character=character;
                if(unwrapped!=null){
                    byte[] consumed=unwrapped.InventorySlot==slot
                        ?GamePackets.ConsumableRemove(slot,EquipmentItems.CarriedWeight(character))
                        :ConsumedInventory(character,selected);
                    await SendSnapshot([consumed,..GamePackets.PositionedInventoryAdd(character,unwrapped)]);
                }
                return;
            }
            if(ElBlessing.IsItem(item)){
                if(character.AwaitingRevival)return;
                long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if(selected.IsExpired(now))return;
                if(character.ElBlessingUntil>now){
                    await SendSnapshot([[8,0,1],GamePackets.Message("이미 사용중입니다")]);
                    return;
                }
                try{character=presence.Character=characters.StartElBlessing(character.Id,now);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Blessing use save failed: {ex.Message}");
                    await Send(GamePackets.Message("엘의축복 사용을 저장하지 못했습니다."));
                    return;
                }
                byte[] effect=GamePackets.ObjectEffect(character.Id,ElBlessing.Effect);
                await SendSnapshot([[8,0,1,0],GamePackets.TimedSpell(ElBlessing.Timer(ElBlessing.DurationSeconds)),effect]);
                await hub.BroadcastVisible(character,effect,except:character.Id);
                return;
            }
            if(PotionBuffs.For(item) is {} buff){
                long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await ClearExpiredPotionBuffs(presence,now);
                character=presence.Character;
                if(PotionBuffs.Until(character,buff)>0){
                    await Send(GamePackets.Message("이미 포션 효과가 적용 중입니다."));return;
                }
                try{character=presence.Character=characters.StartPotionBuff(character.Id,buff,ConsumeOne(items,selected),now);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"Potion use save failed: {ex.Message}");
                    await Send(GamePackets.Message("포션 사용을 저장하지 못했습니다."));
                    return;
                }
                bool speed=buff==PotionBuff.Speed;
                byte mode=presence.CombatMode;
                bool paddedSpeed=speed&&mode!=2;
                uint? effectFlags=speed||character.SpeedPotionUntil>now?presence.StatusEffectFlags:null;
                byte[] remaining=ConsumedInventory(character,selected,padded:paddedSpeed);
                var timer=GamePackets.TimedSpell(PotionBuffs.Timer(buff,PotionBuffs.ShownSeconds));
                if(speed){
                    byte[] status=paddedSpeed?[8,0,mode,0]:[8,0,mode];
                    await SendSnapshot([status,timer,GamePackets.PotionBuffStatus(character,padded:false,state:mode,effectFlags:effectFlags),remaining]);
                }else{
                    await SendSnapshot([GamePackets.PotionBuffStatus(character,state:mode,effectFlags:effectFlags),timer,
                        GamePackets.PotionBuffStatus(character,0x40,state:mode,effectFlags:effectFlags),remaining]);
                }
                if(!speed&&presence.Party is {} party)await party.UpdateHealth(presence);
                return;
            }
            if(item.Sprite==0x8F07&&item.Name=="단검용독(Lv1)"){
                if(CombatAbilities.For(character).Slot!=4){
                    await Send(GamePackets.Message("단검을 장착한 뒤 사용할 수 있습니다."));
                    return;
                }
                if(character.DaggerPoisonActive()){
                    await Send(GamePackets.Message("아직 단검용독 효과가 남아 있습니다."));
                    return;
                }
                await ClearExpiredDaggerPoison(presence);
                var timer=EquipmentItems.DaggerPoisonTimer;
                long until=DateTimeOffset.UtcNow.AddSeconds(timer.Duration).ToUnixTimeMilliseconds();
                character=characters.SetItems(character.Id,ConsumeOne(items,selected),poisonUntil:until);
                presence.Character=character;
                presence.PoisonShown=true;
                byte mode=presence.CombatMode;
                // The two maps have distinct recorded self responses. Peer padding is independent.
                bool popresneShop=character.Map==4216&&presence.StatusDimension==0;
                byte[] status=character.Map==10002&&presence.StatusDimension==1
                    ?GamePackets.PaddedStatus(character,0x40,mode)
                    :popresneShop?[8,0,mode]:GamePackets.EmptySatietyStatus(mode);
                byte[] effect=GamePackets.DaggerPoisonEffect(character.Id),motion=GamePackets.DaggerPoisonMotion(character.Id);
                byte[] remaining=ConsumedInventory(character,selected);
                if(selected.Quantity>1&&!popresneShop)remaining=[..remaining,0];
                await SendSnapshot([status,GamePackets.TimedSpell(timer),GamePackets.Message("단검에 독을 발랐습니다."),
                    popresneShop?[..effect,0]:effect,popresneShop?[..motion,0]:motion,remaining]);
                await hub.BroadcastVisible(character,effect,except:character.Id);
                await hub.BroadcastVisible(character,motion,except:character.Id);
                return;
            }
            var training=item.Sprite switch {
                // Caps come from v611 NPCMuttering/수련장. The first two durability values fit the recorded breaks.
                0x8D1F when item.Name=="수련인형(500G)" => (Sprite:0x400B,Durability:3000,Limit:1000,UsesDamage:true),
                0x8D20 when item.Name=="수련인형(2500G)" => (Sprite:0x400C,Durability:4000,Limit:1500,UsesDamage:true),
                0x8D21 when item.Name=="수련인형(10000G)" => (Sprite:0x400D,Durability:200,Limit:2000,UsesDamage:false),
                _ => (Sprite:0,Durability:0,Limit:0,UsesDamage:false)
            };
            bool fightingEgg=item is {Sprite:0x8BD7,Name:"싸움닭(알)"};
            if((training.Sprite!=0||fightingEgg)&&world.BlocksFieldPlacement(character.Map)){
                await Send(GamePackets.Message("사용 불가 지역!!"));
                return;
            }
            if(fightingEgg){
                var (dx,dy)=LocalMap.Delta(character.Direction);
                int x=character.X+dx,y=character.Y+dy;
                if(!world.GetMap(character.Map).CanStand(x,y,character.Floor)||
                    world.StepDestination(character.Map,x,y,character.Floor)!=null||
                    world.InMap(character.Map).Any(n=>n.X==x&&n.Y==y&&n.Floor==character.Floor)||
                    hub.IsOccupied(character.Map,x,y,character.Floor,character.Id)){
                    await Send(GamePackets.Message("앞에 싸움닭을 놓을 수 없습니다."));
                    return;
                }
                character=presence.Character=characters.SetItems(character.Id,ConsumeOne(items,selected));
                var rooster=world.PlaceFightingRooster(character,x,y);
                await hub.BroadcastVisible(rooster.Appearance,[..GamePackets.Objects([],[rooster.Appearance]),0]);
                await Send(ConsumedInventory(character,selected,padded:true));
                _=StartFightingRoosterWalk(rooster,stop.Token);
                return;
            }
            if(training.Sprite!=0){
                var (dx,dy)=LocalMap.Delta(character.Direction);
                int x=character.X+dx,y=character.Y+dy;
                if(!world.GetMap(character.Map).CanStand(x,y,character.Floor)
                    ||world.StepDestination(character.Map,x,y,character.Floor)!=null
                    ||world.IsOccupied(character.Map,x,y,character.Floor,ignoreTrainingDolls:true)
                    ||hub.IsOccupied(character.Map,x,y,character.Floor,character.Id)){
                    await Send(GamePackets.Message("앞에 수련인형을 설치할 수 없습니다."));
                    return;
                }
                character=characters.SetItems(character.Id,ConsumeOne(items,selected));
                presence.Character=character;
                var doll=world.PlaceTrainingDoll(character,x,y,(ushort)training.Sprite,training.Durability,training.Limit,training.UsesDamage);
                await hub.BroadcastVisible(doll,GamePackets.Objects([],[doll with {Type=2}]));
                await hub.BroadcastVisible(doll,GamePackets.Turn(doll.Id,doll.Type));
                await Send(ConsumedInventory(character,selected));
                return;
            }
            if(item.RestoreHp>0||item.RestoreMp>0||item.RestoreSatiety>0){
                bool potion=EquipmentItems.IsRecoveryPotion(item);
                uint potionDivisor=character.Satiety==0&&potion?2u:1u;
                uint healed=Math.Min(item.RestoreHp/potionDivisor,character.DamageTaken);
                uint restoredMana=Math.Min(item.RestoreMp/potionDivisor,character.ManaSpent);
                bool food=item.RestoreSatiety>0;
                bool bread=item is {Sprite:0x8C78,Name:"식빵"};
                bool apple=item is {Sprite:0x8BC8,Name:"사과"};
                bool healingSix=item is {Sprite:0x8BC6,Name:"힐링포션(6ml)"};
                bool compactPotion=potion&&item.Sprite is 0x8BC3 or 0x8D24;
                if(!potion&&!food&&healed==0&&restoredMana==0)return;
                uint satiety=Math.Min(WorldSettings.MaxSatiety,character.Satiety+item.RestoreSatiety);
                bool changed=healed>0||restoredMana>0||satiety!=character.Satiety;
                var next=ConsumeOne(items,selected);
                uint previousDamage=character.DamageTaken;
                uint damage=previousDamage-healed,mana=character.ManaSpent-restoredMana;
                if(potion){
                    uint cost=EquipmentItems.PotionCrossCost(item);
                    if(item.RestoreHp>0){
                        uint maxMana=character.MaxMp(EquipmentItems.EquippedEffects(character));
                        mana+=Math.Min(cost,maxMana-Math.Min(mana,maxMana));
                    }else{
                        uint currentHp=character.CurrentHp();
                        if(currentHp>1)damage+=Math.Min(cost,currentHp-1);
                    }
                }
                character=characters.SetHealth(character.Id,damage,next,satiety,mana);
                presence.Character=character;
                byte mask=changed||potion?(byte)0x60:(byte)0x40;
                byte state=presence.CombatMode;
                uint vitalsAux=presence.VitalsAux;
                bool lastChestnut=!changed&&selected.Quantity==1&&item is {Sprite:0x8BCC,Name:"밤"};
                byte[] status=food?(changed||bread?GamePackets.PaddedStatus(character,mask,state,vitalsAux):apple||lastChestnut?[8,0,state]:[8,0,state,0])
                    :compactPotion?GamePackets.PaddedStatus(character,0x20,state,vitalsAux)
                    :GamePackets.Status(character,mask,state,vitalsExtra:vitalsAux);
                byte[] remaining=ConsumedInventory(character,selected,mode:food?(byte)2:(byte)6,padded:food&&!lastChestnut||healingSix||compactPotion);
                if(food){
                    if(selected.Quantity>1)remaining=[..remaining,0];
                    var notice=GamePackets.Message(bread?"식빵을 먹었습니다.":$"{item.Name}을(를) 먹었습니다.");
                    await SendSnapshot(apple?[status,notice,remaining,GamePackets.SoundEffect(35,padded:false)]:[status,notice,remaining]);
                }else if(healingSix||compactPotion){
                    await SendSnapshot([status,remaining,GamePackets.PotionUseSound()]);
                }else{
                    await SendSnapshot([status,remaining]);
                }
                if(damage!=previousDamage&&presence.Party is {} party)await party.UpdateHealth(presence);
                return;
            }
            if(selected.Key=="oran-feather")await UseFeather(selected,WorldSettings.OranDestination(character.OranTownMap,character.Map));
            else await ChangeEquipment(slot,true);
        }
        async Task ShowGuardianBadge(){
            var (pending,points,cost)=characters.GuardianBalance(character!.Id,DateTimeOffset.UtcNow);
            guardianPage=3;
            await Send(GamePackets.NpcPursuitPage(GuardianQuest.BadgeSpeaker,
                $"받을 포인트 {pending:N0}|사용 가능한 포인트 {points:N0}|교환 비용 {cost:N0}포인트",
                ["포인트 받기","이동속도","힘","카운트","닫기"],page:3,script:GuardianQuest.Script));
        }
        async Task HandleCalendarQuest(LocalNpc npc,CalendarQuest quest,bool claim){
            if(presence is not {Ready:true,Trade:null}||character is not {AwaitingRevival:false} previous)return;
            try{
                var (updated,result,reward)=characters.RecordCalendarQuest(previous.Id,quest,claim,DateTimeOffset.UtcNow);
                character=presence.Character=updated;
                var progress=quest.Progress(updated);
                var packets=quest.NeedsWater&&result==CalendarQuestResult.Visited?
                    MaterialDeltas(previous,updated,CalendarQuest.IsWater).ToList():new List<byte[]>();
                if(reward!=null)packets.Add(GamePackets.InventoryAdd(reward,EquipmentItems.CarriedWeight(updated)));
                string message=result switch {
                    CalendarQuestResult.Visited=>$"오늘 방문을 기록했네. ({progress.Visits}/{CalendarQuest.RequiredVisits})",
                    CalendarQuestResult.Rewarded=>$"{quest.RingName}을 받게.",
                    CalendarQuestResult.AlreadyVisited=>"오늘은 이미 방문했네. 다음 날 다시 오게.",
                    CalendarQuestResult.MissingWater=>"깨끗한물 한 개를 가져오게.",
                    CalendarQuestResult.InventoryFull=>"소지품에 빈 칸을 만들어 주게.",
                    CalendarQuestResult.Completed=>"이미 보상을 받았네.",
                    _=>progress.Visits>=CalendarQuest.RequiredVisits?"보상 메뉴에서 보상을 받게.":
                        $"방문 기록이 {progress.Visits}/{CalendarQuest.RequiredVisits}일이네."
                };
                packets.Add(GamePackets.NpcPursuitPage(npc,message));
                await SendSnapshot(packets);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Calendar quest save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"진행을 저장하지 못했습니다."));
            }
        }
        async Task AdvanceGuardian(byte[] packet){
            if(guardianPage==0||presence is not {Ready:true,Trade:null}||character is not {AwaitingRevival:false} current||
                packet[1]!=1||packet[8]!=0||packet[9]!=guardianPage+1)return;
            byte page=guardianPage;
            guardianPage=0;
            try{
                if(page==1&&packet.Length==12&&packet[10]==1&&packet[11]==1){
                    guardianPage=2;
                    await Send(GamePackets.NpcPursuitInput(GuardianQuest.CertificateSpeaker,"수호천사로 임명할 캐릭터의 이름을 입력해 주세요.",
                        2,"캐릭터 이름",24,GuardianQuest.Script));
                }else if(page==2&&packet[10]==2&&packet.Length>=12&&packet[11]<=24&&packet.Length==12+packet[11]){
                    string name;
                    try{name=new PacketReader(packet.AsSpan(11)).Str8();}
                    catch(DecoderFallbackException){return;}
                    var (trainee,guardian,badge,pencil,result)=characters.AppointGuardian(current.Id,guardianCertificateSlot,name);
                    if(result!=GuardianResult.Applied){
                        await Send(GamePackets.Message(result==GuardianResult.InventoryFull?"소지품에 빈 칸이 필요합니다.":
                            "이 캐릭터를 수호천사로 임명할 수 없습니다."));
                        return;
                    }
                    character=presence.Character=trainee;
                    await SendSnapshot([..MaterialDeltas(current,trainee,i=>i.Key==GuardianQuest.Certificate),
                        GamePackets.InventoryAdd(pencil!,EquipmentItems.CarriedWeight(trainee)),GamePackets.Message($"{guardian!.Name} 님을 수호천사로 임명했습니다.")]);
                    if(hub.FindOnline(guardian.Id) is {} online){
                        online.Character=guardian;
                        if(badge!=null)await WorldHub.Notify(online,GamePackets.InventoryAdd(badge,EquipmentItems.CarriedWeight(guardian)));
                    }
                }else if(page==3&&packet.Length==12&&packet[10]==1&&
                    (current.Items??[]).Any(i=>i.Key==GuardianQuest.Badge&&i.InventorySlot>0)){
                    if(packet[11]==1){
                        var (updated,claimed)=characters.ClaimGuardianPoints(current.Id);
                        character=presence.Character=updated;
                        await Send(GamePackets.Message($"{claimed:N0}포인트를 받았습니다."));
                        await ShowGuardianBadge();
                    }else if(packet[11] is >=2 and <=4){
                        var reward=(GuardianReward)packet[11];
                        var (updated,result)=characters.RedeemGuardianReward(current.Id,reward,DateTimeOffset.UtcNow);
                        character=presence.Character=updated;
                        if(result==GuardianResult.Applied){
                            if(reward==GuardianReward.Speed)await Send(GamePackets.PotionBuffStatus(updated,0x60));
                            if(reward==GuardianReward.Strength)await Send(GamePackets.Status(updated,0x40));
                            await Send(GamePackets.TimedSpell(GuardianQuest.Timer(reward,GuardianQuest.Duration)));
                        }else await Send(GamePackets.Message(result==GuardianResult.AlreadyActive?
                            "이미 효과가 적용 중입니다.":"포인트가 부족합니다."));
                        await ShowGuardianBadge();
                    }
                }
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Guardian quest save failed: {ex.Message}");
                await Send(GamePackets.Message("수호천사 진행을 저장하지 못했습니다."));
            }
        }
        static byte[] ConsumedInventory(Character current,ItemPlacement selected,byte mode=6,bool padded=false){
            ushort weight=EquipmentItems.CarriedWeight(current);
            return selected.Quantity==1?GamePackets.ConsumableRemove(selected.InventorySlot,weight,mode,padded):
                GamePackets.InventoryAdd(selected with {Quantity=selected.Quantity-1},weight);
        }
        static ItemPlacement[] ConsumeOne(ItemPlacement[] items,ItemPlacement selected)=>
            selected.Quantity==1 ? items.Where(p=>p!=selected).ToArray()
                : items.Select(p=>p==selected ? p with {Quantity=p.Quantity-1} : p).ToArray();
        async Task BeginWorldHandoff(Character updated,byte[]? inventoryDelta=null,bool roomMenuExit=false){
            await CloseCraft();
            var moving=presence!;
            if(moving.Trade is {} trade)await trade.Close("맵이 바뀌어 교환을 취소했습니다.");
            await InterruptGuildCapture(moving,"깃발 앞을 벗어났습니다.");
            var previous=moving.Character;
            uint? closingLeonId=moving.Leon?.Id;
            if(closingLeonId==null&&moving.PendingLeonArrival!=null)closingLeonId=MercenaryHiring.LeonId(previous.Id);
            moving.Ready=false;
            await hub.RemoveLeon(moving);
            moving.RequestedRoomSign=null;
            moving.Character=updated;
            await hub.RemovePeerAppearance(previous);
            transferPending=true;
            var lifetime=TimeSpan.FromSeconds(10);
            outgoingHandoff=new WorldHandoff(moving,DateTimeOffset.UtcNow+lifetime,previous.Map<0);
            timeout.CancelAfter(lifetime);
            var packets=new List<byte[]>();
            if(previous.Map<0)packets.Add(GamePackets.ClosePersonalRoomControls());
            if(roomMenuExit||closingLeonId.HasValue)
                packets.Add(MercenaryHiring.CloseControls(closingLeonId.GetValueOrDefault()));
            packets.Add(GamePackets.BlockClientForHandoff());
            packets.Add(GamePackets.Transfer(worldEndpoint,NewTransferTicket(updated.Id,outgoingHandoff)));
            if(inventoryDelta!=null)packets.Add(inventoryDelta);
            await SendSnapshot(packets);
        }
        async Task UseFeather(ItemPlacement selected,StartLocation destination){
            if(character==null||character.Items is not {} items)return;
            if(await BlockRegionTravel(destination.Map))return;
            var item=EquipmentItems.Get(selected.Key);
            var map=world.GetMap(destination.Map);
            uint characterId=character.Id;
            bool Available(StartLocation arrival)=>map.CanWalk(arrival.X,arrival.Y,arrival.Floor)&&
                !world.IsOccupied(arrival.Map,arrival.X,arrival.Y,arrival.Floor)&&
                !hub.IsOccupied(arrival.Map,arrival.X,arrival.Y,arrival.Floor,characterId);
            bool ras=selected.Key=="ras-feather";
            var landing=ras?WorldSettings.PickRasArrival(destination,character.Map,Available):Available(destination)?destination:null;
            if(landing==null){
                await Send(GamePackets.Message($"{item.Name} 도착 위치를 사용할 수 없습니다."));
                return;
            }
            destination=landing;
            if(ras){
                // The menu can open from a higher slot; retail debits the first available stack.
                selected=items.Where(i=>i.Key=="ras-feather"&&i.InventorySlot>0&&i.Quantity>0)
                    .MinBy(i=>i.InventorySlot)??selected;
            }
            Character updated;
            try{updated=characters.SetItems(character.Id,ConsumeOne(items,selected),destination);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} feather travel save failed: {ex.Message}");
                await Send(GamePackets.Message("이동 위치를 저장하지 못했습니다."));
                return;
            }
            ushort carriedWeight=EquipmentItems.CarriedWeight(updated);
            var inventoryDelta=selected.Quantity==1
                ? (ras?GamePackets.RasFeatherRemove(selected.InventorySlot,carriedWeight):GamePackets.InventoryRemove(selected.InventorySlot,carriedWeight)):
                GamePackets.InventoryAdd(selected with {Quantity=selected.Quantity-1},carriedWeight);
            int origin=character.Map<0?character.PersonalRoomExitMap??2:character.Map;
            if(ras&&origin/10000!=destination.Map/10000){
                if(selected.Quantity>1)inventoryDelta=[..inventoryDelta,0];
                await BeginWorldHandoff(updated,inventoryDelta);
                return;
            }
            await EnterMap(updated,inventoryDelta,recordedArrival:true,oranFeather:!ras,rasFeather:ras);
        }
        async Task<bool> BlockRegionTravel(int map,byte[]? correction=null){
            if(CanEnterRegion(map))return false;
            if(correction!=null)await Send(correction);
            await Send(GamePackets.Message(WorldSettings.UnreleasedRegionMessage));
            return true;
        }
        static bool CanTalkTo(Character current,LocalNpc npc,int range,bool allowMerchantAcrossFloor=false)=>HairSalonDialog.IsBarber(npc)||
            allowMerchantAcrossFloor&&npc.Category==2&&npc.Floor!=current.Floor
            ?WorldHub.CanSee(current,npc)
            :npc.Floor==current.Floor&&Math.Max(Math.Abs(npc.X-current.X),Math.Abs(npc.Y-current.Y))<=range;
        LocalNpc? GuildFlagUnder(Character current)=>WorldSettings.IsGuildHouse(current.Map)
            ?world.InMap(current.Map).FirstOrDefault(n=>GuildHouseCapture.IsFlag(n)&&GuildHouseCapture.AtFlag(current,n)):null;
        async Task RefreshWorld(){
            if(character is not {} current||presence is not {} session)return;
            var flag=GuildFlagUnder(current);
            if(flag==null){
                await Snapshot();
            }else{
                var map=world.GetMap(current.Map);
                var observed=world.ObservedMap(current.Map);
                var packets=new List<byte[]>();
                foreach(var oid in session.RefreshedObjects.Keys.Concat(session.FarSpawns).Distinct())
                    packets.Add(GamePackets.Remove(oid));
                session.RefreshedObjects.Clear();
                session.FarSpawns.Clear();
                session.ExpectMapCheck(current.Map);
                packets.Add(GamePackets.Map(WorldSettings.ClientMap(current.Map),map.Width,map.Height,
                    observed?.Name??$"로컬 맵 {current.Map}",observed?.Flags??0));
                packets.Add(GamePackets.MapCheck(1));
                packets.Add(GamePackets.Position(current.X,current.Y,current.Floor));
                var peers=hub.InMap(current.Map).Where(peer=>peer!=session&&WorldHub.CanSee(current,peer.Character));
                packets.AddRange(peers.SelectMany(peer=>peer.PeerAppearance()));
                var npcs=world.InMap(current.Map).Where(npc=>WorldHub.CanSee(current,npc)).ToArray();
                var objects=world.FieldObjects(current.Map).Where(item=>WorldHub.CanSee(current,item)).ToArray();
                packets.Add(GamePackets.Objects(npcs,objects));
                packets.Add(GamePackets.Human(current));
                AppendMercenaries(packets,initialLogin:false);
                await SendSnapshot(packets);
                await OpenNpc(flag,false);
            }
        }
        string? GuildCaptureBlock(LocalNpc flag,string? owner){
            if(!GuildHouseCapture.AtFlag(character!,flag))return "깃발 바로 북쪽 한 칸에 서서 신청해 주세요.";
            if(!GuildHouseCapture.IsGuildMaster(character!))return GuildHouseCapture.MasterRequired;
            if(string.Equals(owner,character!.GuildName,StringComparison.OrdinalIgnoreCase))return "이미 우리 길드가 점령하고 있습니다.";
            if(guildCaptures.ContainsKey(flag.Map))return "다른 길드가 점령을 진행 중입니다.";
            if(!GuildHouseCapture.IsOpen(WorldTime(),guildHoldSeconds))
                return "한국 시간 오전 9시부터 자정까지 점령할 수 있습니다. 수요일은 신청을 받지 않습니다.";
            if(presence!.Trade!=null)return "교환을 끝낸 뒤 점령을 신청해 주세요.";
            return character.Gold<GuildHouseCapture.Fee?"점령 신청에 1,000갈리드가 필요합니다.":null;
        }
        async Task OpenGuildFlag(LocalNpc flag){
            guildFlagOffer=null;
            if(GuildHouseCapture.AtFlag(character!,flag)&&!GuildHouseCapture.IsGuildMaster(character!)){
                await Send(GamePackets.Pursuit(flag.Id,GuildHouseCapture.MasterRequired,null,back:false,next:true));
                return;
            }
            string? owner=characters.GuildHouseOwner(flag.Map);
            string status=owner==null?"현재 점령 중인 길드가 없습니다.":$"현재 소유 길드 [{owner}]";
            if(guildCaptures.TryGetValue(flag.Map,out var attempt)&&attempt.Challenger==presence){
                long remaining=Math.Max(0,(attempt.Due-Environment.TickCount64+999)/1000);
                await Send(GamePackets.NpcPursuitPage(flag,$"{status}|점령까지 {remaining/60}분 {remaining%60}초 남았습니다."));
                return;
            }
            string? blocked=GuildCaptureBlock(flag,owner);
            if(blocked!=null){await Send(GamePackets.NpcPursuitPage(flag,$"{status}|{blocked}"));return;}
            guildFlagOffer=flag.Id;
            await Send(GamePackets.NpcPursuitPage(flag,
                $"{status}|1,000갈리드를 내고 {GuildHouseCapture.Duration(guildHoldSeconds)}간 이 자리를 지키시겠습니까? 성공하면 500갈리드를 돌려드립니다.",
                ["예","아니오"]));
        }
        async Task StartGuildCapture(LocalNpc flag){
            var current=character!;
            var blocked=GuildCaptureBlock(flag,characters.GuildHouseOwner(flag.Map));
            if(blocked!=null){await Send(GamePackets.NpcPursuitPage(flag,blocked));return;}
            Character paid;
            try{paid=characters.PayGuildHouseFee(current.Id,flag.Map);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
                Console.Error.WriteLine($"Guild house fee save failed for map {flag.Map}: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(flag,"점령 신청을 저장하지 못했습니다."));
                return;
            }
            if(paid.Gold==current.Gold){await Send(GamePackets.NpcPursuitPage(flag,"점령 신청 조건을 다시 확인해 주세요."));return;}
            character=presence!.Character=paid;
            guildCaptures.Add(flag.Map,new(presence,new(flag.Map,paid.X,paid.Y,paid.Floor),
                Environment.TickCount64+guildHoldSeconds*1000L));
            await SendWalletUpdate(paid,current.Gold);
            await Send(GamePackets.Gold(paid.Gold));
            await Send(GamePackets.NpcPursuitPage(flag,"점령을 시작했습니다. 완료될 때까지 이 자리를 지켜 주세요."));
            await hub.Broadcast(flag.Map,GamePackets.Message($"[{paid.GuildName}] 길드가 길드집 점령을 시작했습니다."));
        }
        async Task BuyLeonDeed(LocalNpc npc,byte[] receipt){
            if(character is not {AwaitingRevival:false} current||presence==null)return;
            if(presence.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 고용서를 받아 주세요."));
                return;
            }
            Character buyer;
            ItemPlacement? deed;
            try{(buyer,deed)=characters.BuyLeonDeed(current.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Hiring deed save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"고용서 구매를 저장하지 못했습니다."));
                return;
            }
            if(deed==null){
                await Send(GamePackets.NpcPursuitPage(npc,buyer.Gold<MercenaryHiring.Price
                    ?"고용서를 받으려면 10000 갈리드가 필요합니다.":"소지품 창에 빈 칸이 없습니다."));
                return;
            }
            character=presence.Character=buyer;
            activePursuitPath=MercenaryHiring.Confirmed;
            ushort weight=EquipmentItems.CarriedWeight(buyer);
            var (x,y)=inventoryPositions.GetValueOrDefault(deed.InventorySlot);
            var initial=GamePackets.InventoryAdd(deed with {InventoryWord1=x,InventoryWord2=y},weight);
            var positioned=GamePackets.InventoryAdd(deed,weight);
            await SendWalletUpdate(buyer,current.Gold,weight);
            await SendSnapshot([GamePackets.PaddedStatus(buyer,0x10,presence.CombatMode),initial,[..positioned,0],
                GamePackets.SoundEffect(70),receipt]);
        }
        async Task OpenNpc(LocalNpc npc,bool infoRequest){
            if(activeNpcDialog==npc.Id&&MercenaryHiring.IsGuard(npc)&&MercenaryHiring.IsReceiptPage(activePursuitPath)&&
                npc.OpenDialog is {} guardMenu){
                await Send(GamePackets.NpcDialog(npc,guardMenu));
                return;
            }
            ClearNpcDialogs();
            activeNpcDialog=npc.Id;
            if(RanicheQuest.IsRex(npc)&&character is {AwaitingRevival:false} royalVisitor){
                if(royalVisitor.RanicheProgress>=RanicheStage.ShowStaff&&CanTalkTo(royalVisitor,npc,8))await ShowRaniche(npc);
                else await Send(GamePackets.NpcPursuitPage(npc,"무슨 일인가? 할 말이 있으면 내 앞에서 이야기하게."));
                return;
            }
            if(ErenoaQuest.IsNpc(npc)){
                await ShowErenoa(npc,ErenoaPage.Menu);
                return;
            }
            if(WorldBank.IsBanker(npc)){
                bool creating=character?.BankPassword==null;
                bankLoginStage=creating?BankLoginStage.NewPassword:BankLoginStage.Password;
                await Send(WorldBank.Dialog(npc,creating?WorldBank.NewPassword:WorldBank.Login));
                return;
            }
            if(GuildHouseCapture.IsFlag(npc)){
                await OpenGuildFlag(npc);
                return;
            }
            var opening=npc.OpenDialog??(HairSalonDialog.IsBarber(npc)?HairSalonDialog.Open(npc):
                KondQuest.IsOblo(npc)?KondQuest.ObloMenu(npc):null);
            if(opening is {} observed&&
                (npc.Shop==null||observed.Mode!=13||observed.Flags==npc.ShopFlags)){
                await Send(GamePackets.NpcDialog(npc,observed));
            }else if(npc.Shop!=null){
                await Send(GamePackets.ShopMenu(npc));
            }else{
                await Send(infoRequest&&npc.DialogReplies==null&&npc.PursuitReplies==null?GamePackets.NpcInfo(npc):GamePackets.NpcDialog(npc));
            }
        }
        async Task AdvanceNeruaDialog(LocalNpc npc,NeruaDialog dialog,byte[] request){
            var action=dialog.Advance(request);
            if(action==NeruaAction.Ignore)return;
            if(action!=NeruaAction.Next)neruaDialog=null;
            if(action==NeruaAction.Close)return;
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(action==NeruaAction.Next){await Send(dialog.Packet(npc));return;}
            if(action==NeruaAction.Farewell){
                await Send(GamePackets.NpcPursuitPage(npc,"이야기를 들어 줘서 고마워. 다음에 또 봐."));
                return;
            }
            NeruaReward reward;
            ItemPlacement? candy;
            try{(character,reward,candy)=characters.GrantNeruaCandy(character!.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                await Send(GamePackets.NpcPursuitPage(npc,"사탕을 건네지 못했어. 다시 이야기하자."));
                return;
            }
            presence!.Character=character;
            if(candy!=null)await Send(GamePackets.InventoryAdd(candy,EquipmentItems.CarriedWeight(character)));
            await Send(GamePackets.NpcPursuitPage(npc,reward switch {
                NeruaReward.Granted=>"내 이야기를 재미있게 들어 줘서 고마워. 이 사탕을 받아 줘!",
                NeruaReward.InventoryFull=>"사탕을 줄게. 소지품에 빈자리를 만든 뒤 다시 와 줘.",
                _=>"내가 준 사탕을 아직 가지고 있구나. 다음에 또 이야기하자."
            }));
        }
        async Task BeginCalasLesson(LocalNpc npc){
            bool qualified=CombatAbilities.Mastery(character!,CombatAbilities.Harp)>=CalasMusicLesson.MinimumMastery;
            calasLesson=qualified?new(npc.Id):null;
            await Send(GamePackets.NpcPursuitPage(npc,qualified?
                "현금의 기초를 익히셨군요. 도부터 시까지 잘 듣고 음과 화음을 맞혀 보세요.":
                "현금 능력이 5.00 이상이면 음악 시험을 볼 수 있습니다.",next:qualified));
        }
        async Task OfferSense(LocalNpc teacher){
            if(character!.SenseMastery!=null){
                activePursuitPath=null;
                await Send(GamePackets.NpcPursuitPage(teacher,"이미 SENSE 기술을 배웠네."));
                return;
            }
            if(!SenseTraining.Eligible(character)){
                activePursuitPath=SenseTraining.CheckPath;
                await Send(GamePackets.NpcPursuit(teacher,SenseTraining.CheckPath)??
                    GamePackets.NpcPursuitPage(teacher,"모험가가 아니면 순수 지능이 10 이상이어야 하네."));
                return;
            }
            activePursuitPath=SenseTraining.OfferPath;
            await Send(GamePackets.NpcPursuitPage(teacher,"SENSE 기술을 2,500갈리드에 배우겠는가?",
                ["예","아니오"],page:SenseTraining.OfferPage,script:SenseTraining.Script));
        }
        async Task EnrollSense(LocalNpc teacher){
            if(presence!.Trade!=null){await Send(GamePackets.NpcPursuitPage(teacher,"교환을 끝낸 뒤 다시 와 주게."));return;}
            uint previousGold=character!.Gold;
            SenseEnrollment result;
            try{(character,result)=characters.LearnSense(character.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Sense enrollment save failed: {ex.GetType().Name}");
                await Send(GamePackets.NpcPursuitPage(teacher,"기술을 저장하지 못했습니다."));
                return;
            }
            if(result!=SenseEnrollment.Learned){
                await Send(GamePackets.NpcPursuitPage(teacher,result switch{
                    SenseEnrollment.AlreadyLearned=>"이미 SENSE 기술을 배웠네.",
                    SenseEnrollment.Ineligible=>"SENSE를 배울 자격이 부족하네.",
                    _=>"수업료 2,500갈리드가 필요하네."
                }));
                return;
            }
            presence.Character=character;
            await SendWalletUpdate(character,previousGold);
            await SendSnapshot([GamePackets.Gold(character.Gold),GamePackets.SenseAbility(0),GamePackets.SenseProgress(),
                GamePackets.NpcPursuitPage(teacher,"SENSE 기술을 배웠네. Action Ability 창에서 왼쪽 보석을 눌러 몬스터를 선택하게.")]);
        }
        async Task UseSense(Character player,uint targetId){
            if(player.SenseMastery is not ushort mastery||player.AwaitingRevival||presence==null)return;
            long now=Environment.TickCount64;
            if(now<nextSenseUse)return;
            var monster=world.MonsterById(targetId);
            if(monster==null||monster.IsTreasureChest||!WorldHub.InMonsterReach(monster.Appearance,player,SenseTraining.Range(mastery))||
                !world.GetMap(player.Map).ClearAttackPath(player.X,player.Y,player.Floor,monster.Appearance.X,monster.Appearance.Y)){
                await Send(GamePackets.SenseResult("가까운 몬스터를 선택해 주세요."));
                return;
            }
            nextSenseUse=now+SenseTraining.UseIntervalMs;
            string description=SenseTraining.Describe(monster,mastery);
            try{character=presence.Character=characters.TrainSense(player.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Sense training save failed: {ex.GetType().Name}");
                await Send(GamePackets.SenseResult(description));
                return;
            }
            var packets=new List<byte[]>{GamePackets.SenseResult(description)};
            if(character.SenseMastery is ushort trained&&trained!=mastery){
                packets.Add(GamePackets.SenseAbility(trained));
                packets.Add(GamePackets.SenseProgress());
            }
            packets.AddRange(JobGrowthPackets(player,character));
            await SendSnapshot(packets);
        }
        async Task EnrollMeditation(LocalNpc teacher){
            var before=character!;
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(teacher,"교환을 마친 다음 다시 오게나."));
                return;
            }
            MeditationEnrollment result;
            try{(character,result)=characters.LearnMeditation(before.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Meditation enrollment save failed: {ex.GetType().Name}");
                await Send(GamePackets.NpcPursuitPage(teacher,"명상 기술을 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            if(result==MeditationEnrollment.LowInt&&GamePackets.NpcPursuit(teacher,MeditationTraining.LowIntPath) is {} refusal){
                await Send(refusal);
                return;
            }
            if(result==MeditationEnrollment.Learned){
                await SendWalletUpdate(character,before.Gold);
                await SendSnapshot([GamePackets.Gold(character.Gold),GamePackets.MeditationAbility(0),GamePackets.MeditationProgress(0)]);
            }
            string text=result switch{
                MeditationEnrollment.Learned=>"명상 기술을 배웠네. Action Ability 창에서 사용하면 마력을 회복할 수 있네.",
                MeditationEnrollment.AlreadyLearned=>"이미 명상 기술을 배웠네.",
                MeditationEnrollment.LowInt=>"순수 지능이 15가 되면 다시 오게나.",
                MeditationEnrollment.WrongJob=>"악사 직업을 가진 뒤 다시 오게나.",
                _=>"명상을 배우려면 200,000갈리드가 필요하네."
            };
            await Send(GamePackets.NpcPursuitPage(teacher,text,page:MeditationTraining.OfferPage,script:MeditationTraining.Script));
        }
        async Task UseMeditation(Character player){
            if(player.MeditationCounters is not uint before||player.AwaitingRevival||presence==null)return;
            long now=Environment.TickCount64;
            if(now<nextMeditationUse)return;
            nextMeditationUse=now+MeditationTraining.UseIntervalMs;
            try{character=presence.Character=characters.Meditate(player.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Meditation save failed: {ex.GetType().Name}");
                return;
            }
            var current=character;
            uint after=current.MeditationCounters??before;
            var packets=new List<byte[]>();
            if(player.ManaSpent!=current.ManaSpent)packets.Add(GamePackets.CurrentVitals(current));
            if(before!=after){
                if(MeditationTraining.Mastery(after)>MeditationTraining.Mastery(before))packets.Add(GamePackets.MeditationAbility(after));
                packets.Add(GamePackets.MeditationProgress(after));
            }
            packets.AddRange(JobGrowthPackets(player,current));
            if(CombatAbilities.Experience(current)!=CombatAbilities.Experience(player)){
                if(CombatAbilities.Level(CombatAbilities.Experience(current))!=CombatAbilities.Level(CombatAbilities.Experience(player))&&
                    player.JobStats==current.JobStats)packets.Add(GamePackets.Status(current,mask:0x40));
                if(selfLookRequested)packets.Add(GamePackets.SelfLook(current));
            }
            if(packets.Count>0)await SendSnapshot(packets);
        }
        async Task BeginConversationExam(LocalNpc teacher){
            activePursuitPath=null;
            var learner=character!;
            string? notice=learner.ConversationCounters.HasValue?"이미 대화 기술을 배웠네.":
                !ConversationTraining.Eligible(learner)?"모험가 적성이 첫째이고 모험가 잡포인트가 20 이상이면 다시 오게나.":
                learner.ConversationRetryAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()?"아직 다시 시험을 볼 수 없네. 오답을 낸 뒤 한 시간이 지나야 한다네.":null;
            conversationStage=notice==null?0:null;
            await Send(notice==null?ConversationTraining.ExamPage(teacher,0):
                GamePackets.NpcPursuitPage(teacher,notice,script:MeditationTraining.Script));
        }
        async Task AdvanceConversationExam(LocalNpc teacher,int stage,byte[] packet){
            if(packet.Length<11||packet[1]!=1||new PacketReader(packet.AsSpan(6)).U16()!=MeditationTraining.Script)return;
            var page=new PacketReader(packet.AsSpan(8)).U16();
            byte action=packet[10];
            if(action==0&&packet.Length==11&&page==ConversationTraining.Page(stage)){
                conversationStage=null;
                return;
            }
            if(page!=ConversationTraining.Page(stage)+1)return;
            bool correct;
            if(ConversationTraining.IsInput(stage)){
                if(action!=2||packet.Length<12||packet[11]>ConversationTraining.InputLimit||packet.Length!=12+packet[11])return;
                try{correct=ConversationTraining.Correct(stage,new PacketReader(packet.AsSpan(11)).Str8());}
                catch(DecoderFallbackException){return;}
            }else if(stage is ConversationTraining.Consent or ConversationTraining.GoodsQuestion){
                if(action!=1||packet.Length!=12)return;
                if(stage==ConversationTraining.Consent){
                    if(packet[11]==2)conversationStage=null;
                    if(packet[11]!=1)return;
                    correct=true;
                }else{
                    if(packet[11] is <1 or >8)return;
                    correct=packet[11]==8;
                }
            }else{
                if(action!=0||packet.Length!=11)return;
                correct=true;
            }
            if(correct&&stage!=ConversationTraining.FinalQuestion){
                conversationStage=stage+1;
                await Send(ConversationTraining.ExamPage(teacher,stage+1));
                return;
            }
            conversationStage=null;
            var before=character!;
            try{character=presence!.Character=characters.FinishConversationExam(before.Id,correct,DateTimeOffset.UtcNow);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Conversation exam save failed: {ex.GetType().Name}");
                await Send(GamePackets.NpcPursuitPage(teacher,"시험 결과를 저장하지 못했네. 다시 말을 걸어 주게."));
                return;
            }
            if(correct&&character.ConversationCounters==0&&!before.ConversationCounters.HasValue){
                await SendSnapshot([GamePackets.ConversationAbility(0),GamePackets.ConversationProgress(0),
                    GamePackets.NpcPursuitPage(teacher,"대화 기술을 익혔네.",script:MeditationTraining.Script)]);
            }else{
                string notice=correct?"대화 기술을 배울 조건을 다시 확인해 주게.":"답이 틀렸네. 한 시간 뒤 다시 오게나.";
                await Send(GamePackets.NpcPursuitPage(teacher,notice,script:MeditationTraining.Script));
            }
        }
        async Task UseConversation(Character speaker){
            if(presence==null||speaker.AwaitingRevival||speaker.ConversationCounters is not uint before)return;
            long now=Environment.TickCount64;
            if(now<nextConversationUse)return;
            nextConversationUse=now+ConversationTraining.UseIntervalMs;
            var listeners=hub.InMap(speaker.Map).Where(peer=>peer.Character.Id!=speaker.Id&&!peer.Character.AwaitingRevival&&
                WorldHub.CanSee(speaker,peer.Character)&&WorldHub.CanSee(peer.Character,speaker)).ToArray();
            if(listeners.Length==0){
                await Send(GamePackets.Message("주위에 이야기를 들을 사람이 있어야 합니다."));
                return;
            }
            Character[] recovered;
            try{(character,recovered)=characters.SpeakToNearby(speaker.Id,listeners.Select(peer=>peer.Character.Id).ToArray());}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Conversation skill save failed: {ex.GetType().Name}");
                return;
            }
            presence.Character=character;
            foreach(var current in recovered){
                var listener=listeners.Single(peer=>peer.Character.Id==current.Id);
                bool healed=listener.Character.DamageTaken!=current.DamageTaken;
                listener.Character=current;
                await WorldHub.Notify(listener,GamePackets.CurrentVitals(current));
                if(healed&&listener.Party is {} party)await party.UpdateHealth(listener);
            }
            await hub.BroadcastVisible(character,GamePackets.Say(character,0,"말 한마디가 긴 여행의 피로를 덜어 주는 법이지."));
            uint after=character.ConversationCounters??before;
            if(after==before)return;
            byte rank=ShrineGrade.Conversation.Rank(character);
            if(ConversationTraining.Mastery(after,rank)>ConversationTraining.Mastery(before,rank))
                await Send(GamePackets.ConversationAbility(after,rank));
            await SendSnapshot([GamePackets.ConversationProgress(after,rank),..JobGrowthPackets(speaker,character)]);
            if(CombatAbilities.Experience(character)!=CombatAbilities.Experience(speaker)){
                if(CombatAbilities.Level(CombatAbilities.Experience(character))!=CombatAbilities.Level(CombatAbilities.Experience(speaker))&&
                    speaker.JobStats==character.JobStats)await Send(GamePackets.Status(character,mask:0x40));
                if(selfLookRequested)await Send(GamePackets.SelfLook(character));
            }
        }
        async Task OfferGrepaSkill(LocalNpc teacher,GrepaCombatSkill skill,string path){
            if(skill.Refusal(character!) is {} refusal){
                activePursuitPath=null;
                var observed=GamePackets.NpcPursuit(teacher,path);
                bool captured=(skill==GrepaCombatSkill.BodyBlow&&character!.Job==skill.Job&&
                    CombatAbilities.Mastery(character,skill.RequiredAbility)<7500)||
                    (skill==GrepaCombatSkill.RemoveArmor&&character!.Job!=skill.Job);
                await Send(captured&&observed!=null?observed:
                    GamePackets.NpcPursuitPage(teacher,refusal,page:skill.OfferPage,script:MeditationTraining.Script));
                return;
            }
            activePursuitPath=skill.OfferPath;
            await Send(GamePackets.NpcPursuitPage(teacher,$"{skill.Name} 기술을 {skill.Price:N0}갈리드에 배우겠는가?",
                ["예","아니오"],page:skill.OfferPage,script:MeditationTraining.Script));
        }
        async Task EnrollGrepaSkill(LocalNpc teacher,GrepaCombatSkill skill){
            var before=character!;
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(teacher,"교환을 마친 다음 다시 오게나."));
                return;
            }
            string? refusal;
            try{(character,refusal)=characters.LearnGrepaSkill(before.Id,skill);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Grepa skill enrollment save failed: {ex.GetType().Name}");
                await Send(GamePackets.NpcPursuitPage(teacher,"기술을 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            if(refusal==null){
                await SendWalletUpdate(character,before.Gold);
                await SendSnapshot([GamePackets.Gold(character.Gold),GamePackets.GrepaSkillAbility(skill,0),GamePackets.GrepaSkillProgress(skill,0)]);
            }
            await Send(GamePackets.NpcPursuitPage(teacher,refusal??$"{skill.Name} 기술을 배웠네.",
                page:skill.OfferPage,script:MeditationTraining.Script));
        }
        async Task SendBodyBlowArrival(WorldSession target,Character caster){
            var moved=target.Character;
            var map=world.GetMap(moved.Map);
            var (name,_,flags)=MapEnvironment(moved.Map);
            var peers=hub.InMap(moved.Map).ToArray();
            target.RefreshedObjects.Clear();
            target.FarSpawns.Clear();
            target.ExpectMapCheck(moved.Map,displacement:true);
            var packets=new List<byte[]>{
                GamePackets.Map(WorldSettings.ClientMap(moved.Map),map.Width,map.Height,name,flags,portalPadding:true),
                GamePackets.MapCheck(1),GamePackets.Position(moved.X,moved.Y,moved.Floor)
            };
            packets.AddRange(peers.Where(peer=>peer!=target&&WorldHub.CanSee(moved,peer.Character))
                .Select(peer=>GamePackets.Human(peer.Character)));
            packets.Add(GamePackets.Objects(world.InMap(moved.Map).Where(npc=>WorldHub.CanSee(moved,npc)).ToArray(),
                world.FieldObjects(moved.Map).Where(item=>WorldHub.CanSee(moved,item)).ToArray()));
            packets.Add(GamePackets.Human(moved));
            packets.AddRange(peers.Select(peer=>peer.Leon).OfType<Character>().Where(leon=>WorldHub.CanSee(moved,leon))
                .Select(leon=>GamePackets.Human(leon)));
            ushort motion=Random.Shared.Next(82) switch {<39=>15,<69=>20,_=>30};
            packets.Add(GamePackets.ObjectEffect(moved.Id,7));
            packets.Add(GamePackets.BodyBlowMotion(caster.Id,motion));
            await WorldHub.NotifyPackets(target,packets);
        }
        async Task UseGrepaCombatSkill(Character caster,GrepaCombatSkill skill){
            if(presence==null||caster.AwaitingRevival||presence.Trade!=null||skill.Counters(caster) is not uint points)return;
            long now=Environment.TickCount64;
            if(lastGrepaCombatUse is {} last&&now-last.At<skill.IntervalAfter(last.Skill))return;
            var (dx,dy)=LocalMap.Delta(caster.Direction);
            int x=caster.X+dx,y=caster.Y+dy;
            var target=hub.InMap(caster.Map).FirstOrDefault(peer=>peer.Character.Id!=caster.Id&&
                peer.Character.X==x&&peer.Character.Y==y&&peer.Character.Floor==caster.Floor&&!peer.Character.AwaitingRevival);
            if(target?.Trade!=null||!world.GetMap(caster.Map).ClearAttackPath(caster.X,caster.Y,caster.Floor,x,y))return;
            lastGrepaCombatUse=(skill,now);
            int targetLimit=10000;
            try{
                if(skill==GrepaCombatSkill.BodyBlow){
                    if(!world.TryKnockbackTile(caster.Map,x,y,caster.Floor,caster.Direction,out var next)||
                        hub.IsOccupied(caster.Map,next.X,next.Y,next.Floor,0))return;
                    if(target!=null){
                        var previous=target.Character;
                        if(SavePosition(previous,next.X,next.Y,previous.Direction,floor:next.Floor) is not {} moved)return;
                        target.Character=moved;
                        await InterruptGuildCapture(target,"깃발 앞을 벗어났습니다.");
                        await SendBodyBlowArrival(target,caster);
                        await hub.BroadcastPeerStep(target,previous,2,direction:caster.Direction);
                        if(target.CraftStation is {} station&&!NearCraftStation(moved,station)){
                            target.CraftStation=null;
                            await WorldHub.Notify(target,GamePackets.CraftClose());
                        }
                    }else if(world.MonsterAt(caster.Map,x,y,caster.Floor) is {IsTreasureChest:false} monster){
                        targetLimit=monster.TrainingLimit;
                        var previous=monster.Appearance;
                        monster.Move(next.X,next.Y,caster.Direction);
                        await hub.MoveField(previous,monster.Appearance);
                    }else return;
                }else{
                    if(target==null||!WorldSettings.AllowsDuel(caster.Map)||
                        !await RemoveGrepaEquipment(target,GrepaCombatSkill.Mastery(points)))return;
                }
                character=presence.Character=characters.TrainGrepaSkill(caster.Id,skill,targetLimit);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Grepa combat skill save failed: {ex.GetType().Name}");
                return;
            }
            uint trained=skill.Counters(character)??points;
            if(trained==points)return;
            if(GrepaCombatSkill.Mastery(trained)>GrepaCombatSkill.Mastery(points))
                await Send(GamePackets.GrepaSkillAbility(skill,trained));
            await SendSnapshot([GamePackets.GrepaSkillProgress(skill,trained),..JobGrowthPackets(caster,character)]);
        }
        List<byte[]> MovedObjectPackets(WorldSession moved,Character previous){
            var packets=new List<byte[]>();
            var current=moved.Character;
            var npcs=world.InMap(current.Map);
            var objects=world.FieldObjects(current.Map);
            foreach(var entering in npcs.Where(n=>WorldHub.CanSee(current,n)&&!WorldHub.CanSee(previous,n))){
                moved.RefreshedObjects.TryRemove(entering.Id,out _);
                packets.Add(GamePackets.Objects([entering]));
            }
            foreach(var entering in objects.Where(o=>WorldHub.CanSee(current,o)&&!WorldHub.CanSee(previous,o))){
                moved.FarSpawns.Remove(entering.Id);
                packets.Add(GamePackets.Objects([],[entering]));
            }
            foreach(var leaving in npcs.Where(n=>!WorldHub.CanSee(current,n)&&
                (WorldHub.CanSee(previous,n)||!WorldHub.CanRefreshMerchant(current,n,includeDistant:true)&&
                    moved.RefreshedObjects.TryRemove(n.Id,out _))))
                packets.Add(GamePackets.Remove(leaving.Id));
            foreach(var leaving in objects.Where(o=>!WorldHub.CanSee(current,o)&&
                (WorldHub.CanSee(previous,o)||moved.FarSpawns.Contains(o.Id)&&!WorldHub.CanRefreshFieldObject(current,o)))){
                moved.FarSpawns.Remove(leaving.Id);
                packets.Add(GamePackets.Remove(leaving.Id));
            }
            return packets;
        }
        static ItemPlacement ForcedBagPlacement(ItemPlacement equipped,byte slot)=>equipped with {
            InventorySlot=slot,InventoryWord1=equipped.InventoryWord1??(ushort)(25+(slot-1)%10*20),
            InventoryWord2=equipped.InventoryWord2??(ushort)(15+(slot-1)/10*16)};
        async Task<bool> RemoveGrepaEquipment(WorldSession target,ushort mastery){
            var previous=target.Character;
            if(previous.Items is not {Length:>0} items)return false;
            using var free=previous.FreeInventorySlots(items).GetEnumerator();
            var placements=items.ToArray();
            var removed=new List<(byte Slot,int Index)>();
            foreach(byte slot in GrepaCombatSkill.RemovableSlots(mastery)){
                int index=Array.FindIndex(placements,item=>item.InventorySlot==0&&EquipmentItems.Get(item.Key).EquipmentSlot==slot);
                if(index<0)continue;
                if(!free.MoveNext())break;
                placements[index]=ForcedBagPlacement(placements[index],free.Current);
                removed.Add((slot,index));
            }
            if(removed.Count==0)return false;
            var updated=target.Character=characters.SetItems(previous.Id,placements);
            var packets=new List<byte[]>();
            var intermediate=items.ToArray();
            for(int i=0;i<removed.Count;i++){
                var (slot,index)=removed[i];
                var placed=placements[index];
                var initial=items[index] with {InventorySlot=placed.InventorySlot,InventoryWord1=items[index].InventoryWord1??0,
                    InventoryWord2=items[index].InventoryWord2??0};
                intermediate[index]=placed;
                var after=i==removed.Count-1?updated:CharacterStore.ApplyEquipment(previous,intermediate.ToArray());
                packets.AddRange(GamePackets.EquipmentStow(previous,after,slot,initial,placed,target.CombatMode,target.VitalsAux,padInventory:true));
                previous=after;
            }
            await WorldHub.NotifyPackets(target,packets);
            await hub.BroadcastVisible(updated,GamePackets.Human(updated),except:updated.Id);
            if(target.Party is {} party)await party.UpdateHealth(target);
            return true;
        }
        async Task PlayCalasScale(LocalNpc npc,CalasMusicLesson lesson){
            try{
                var sounds=CalasMusicLesson.NoteSounds;
                for(int index=1;index<=sounds.Length;index++){
                    await Task.Delay(CalasMusicLesson.NoteIntervalMs,ct);
                    await worldUpdateLock.WaitAsync(ct);
                    try{
                        if(calasLesson!=lesson||presence==null||!CanTalkTo(presence.Character,npc,20)||presence.Character.AwaitingRevival)return;
                        if(index<sounds.Length){await Send(GamePackets.SoundEffect(sounds[index]));continue;}
                        lesson.Stage=CalasStage.Note;
                        await SendSnapshot([GamePackets.SoundEffect(sounds[lesson.Note]),
                            GamePackets.NpcPursuitPage(npc,"지금 들린 음은 무엇입니까?",CalasMusicLesson.Notes,page:2)]);
                    }finally{worldUpdateLock.Release();}
                }
            }catch(OperationCanceledException) when(ct.IsCancellationRequested){}
            catch(Exception ex) when(ex is IOException or SocketException or ObjectDisposedException){
                Console.WriteLine($"{role}/{id} Calas sound playback stopped: {ex.GetType().Name}");
            }
        }
        async Task AdvanceCalasLesson(LocalNpc npc,CalasMusicLesson lesson,byte[] request){
            byte expectedPage=lesson.Stage switch {CalasStage.Intro=>2,CalasStage.Note=>3,CalasStage.Chord=>4,_=>0};
            if(expectedPage==0||request[1]!=1||request[6]!=0||request[7]!=0||request[8]!=0)return;
            if(request.Length==11&&request[10]==0&&request[9]==expectedPage-1){calasLesson=null;return;}
            if(request[9]!=expectedPage)return;
            if(lesson.Stage==CalasStage.Intro&&request.Length==11&&request[10]==0){
                lesson.Stage=CalasStage.Scale;
                await Send(GamePackets.SoundEffect(CalasMusicLesson.NoteSounds[0]));
                _=PlayCalasScale(npc,lesson);
                return;
            }
            if(request.Length!=12||request[10]!=1||lesson.Stage is not (CalasStage.Note or CalasStage.Chord))return;
            bool note=lesson.Stage==CalasStage.Note;
            int optionCount=note?CalasMusicLesson.Notes.Length:CalasMusicLesson.Chords.Length;
            int choice=request[11]-1;
            if(choice<0||choice>=optionCount)return;
            bool correct=choice==(note?lesson.Note:lesson.Chord);
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(note&&correct){
                lesson.Stage=CalasStage.Chord;
                await SendSnapshot([GamePackets.SoundEffect(CalasMusicLesson.ChordSounds[lesson.Chord]),
                    GamePackets.NpcPursuitPage(npc,"맞았습니다. 이번에는 들리는 화음을 골라 주세요.",CalasMusicLesson.Chords,page:3)]);
            }else{
                calasLesson=null;
                await Send(GamePackets.NpcPursuitPage(npc,correct?"잘 맞혔습니다. 기본 음악 시험을 마쳤습니다.":
                    $"다른 {(note?"음":"화음")}을 골랐습니다. 다시 도전하려면 말을 걸어 주세요."));
            }
        }
        async Task AdvanceHairSalon(HairSalonDialog salon,byte[] request,uint npcId){
            if(character==null||presence==null||request[1]!=1||request.Length is not (11 or 12))return;
            var npc=world.InMap(character.Map).FirstOrDefault(n=>n.Id==npcId);
            if(npc==null||!HairSalonDialog.IsBarber(npc)||!CanTalkTo(character,npc,20)){hairSalon=null;return;}
            var reader=new PacketReader(request.AsSpan(6));
            if(reader.U16()!=HairSalonDialog.Script)return;
            ushort page=reader.U16();
            byte action=reader.U8();
            if((request.Length==11&&action!=0)||(request.Length==12&&action!=1))return;
            byte choice=action==1?reader.U8():(byte)0;
            var previous=character;
            HairServiceResult Purchase(HairPart part,byte value,uint price){
                if(previous.DisplayModel()[(int)part]==value)return HairServiceResult.Already;
                if(previous.Gold<price)return HairServiceResult.InsufficientGold;
                try{
                    var updated=characters.ChangeHair(previous.Id,part,value,price);
                    if(updated==previous)return HairServiceResult.SaveFailed;
                    character=presence.Character=updated;
                    return HairServiceResult.Changed;
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"{role}/{id} hair salon save failed: {ex.Message}");
                    return HairServiceResult.SaveFailed;
                }
            }
            if(!salon.Advance(page,action,choice,Purchase))return;
            if(salon.Closed){hairSalon=null;return;}
            var current=character!;
            var reply=salon.Packet();
            if(current==previous){await Send(reply);return;}
            var appearance=GamePackets.Human(current);
            var replies=new List<byte[]>();
            if(current.WalletSlot!=0)replies.Add(current.Gold==0?GamePackets.InventoryRemove(current.WalletSlot,EquipmentItems.CarriedWeight(current)):
                GamePackets.Wallet(current));
            byte[] status=salon.Dyed?[..GamePackets.Gold(current.Gold,state:1),0]:GamePackets.Status(current,mask:0x50);
            replies.AddRange([status,appearance,reply]);
            await SendSnapshot(replies);
            await hub.BroadcastVisible(current,appearance,except:current.Id);
        }
        async Task RecoverAtNpc(LocalNpc npc,byte[] reply){
            var previous=character!;
            try{character=characters.SetHealth(previous.Id,0,satiety:WorldSettings.MaxSatiety,manaSpent:0);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} NPC recovery save failed: {ex.Message}");
                await Send(GamePackets.Message("회복 상태를 저장하지 못했습니다."));
                return;
            }
            presence!.Character=character;
            uint auxiliary=presence.VitalsAux;
            bool splitRecovery=npc.Map==10210&&previous.DamageTaken>0&&previous.ManaSpent==0&&previous.Satiety<character.Satiety;
            var flash=GamePackets.ObjectEffect(character.Id,14);
            var motion=GamePackets.InnRecoveryMotion(npc.Id,character.Id);
            var finish=GamePackets.ObjectEffect(character.Id,0x800B);
            // Alpha Therese and the recorded split recovery append a zero to both S29 effects.
            byte[][] effects=splitRecovery||npc.Map==211?[[..flash,0],motion,[..finish,0]]:[flash,motion,finish];
            if(splitRecovery){
                // Beta Holly 1:683–693 heals HP before filling FP, with an effect group for each step.
                var baseStatus=GamePackets.Status(character,0x40,state:1);
                await SendSnapshot([
                    GamePackets.PaddedStatus(character with {Satiety=previous.Satiety},0x60,1,auxiliary),
                    baseStatus,[..baseStatus,0],..effects,
                    GamePackets.PaddedStatus(character,0x60,1,auxiliary),..effects,reply]);
                foreach(var effect in effects)await hub.BroadcastVisible(character,effect,except:character.Id);
            }else await SendSnapshot([GamePackets.InnRecoveryVitals(character,auxiliary),..effects,reply]);
            foreach(var effect in effects)await hub.BroadcastVisible(character,effect,except:character.Id);
            if(previous.DamageTaken!=0&&presence.Party is {} party)await party.UpdateHealth(presence);
        }
        async Task<bool> RegisterRolancia(LocalNpc npc){
            var previous=character!;
            try{character=characters.RegisterRolancia(previous.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} citizenship save failed: {ex.Message}");
                await Send(GamePackets.Message("시민 등록을 저장하지 못했습니다."));
                return false;
            }
            if(character==previous){
                await Send(GamePackets.Message(character.OranTownMap==2?"이미 로랜시아 시민입니다.":"시민 등록에 필요한 갈리드가 부족합니다."));
                return false;
            }
            presence!.Character=character;
            await SendWalletUpdate(character,previous.Gold);
            await Send(GamePackets.CitizenshipStatus(character,includeBase:npc.Map/10000==1));
            await hub.BroadcastVisible(character,GamePackets.Human(character));
            await Send(GamePackets.CitizenshipSound());
            return true;
        }
        async Task<bool> AcceptGatheringQuest(LocalNpc npc,GatheringQuest quest){
            if(quest==GatheringQuest.Pini&&world.Clock.Read(WorldTime()).Hour is <11 or >=19){
                activePursuitPath=null;
                await Send(GamePackets.NpcPursuit(npc,GatheringQuest.PiniAcceptPath)??
                    GamePackets.NpcPursuitPage(npc,"오전 11시에서 오후 7시 사이에 오셔서 도와주실래요?"));
                return false;
            }
            try{character=presence!.Character=characters.AcceptGatheringQuest(character!.Id,quest,DateTimeOffset.UtcNow);return true;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} gathering quest acceptance save failed: {ex.Message}");
                activePursuitPath=null;
                await Send(GamePackets.NpcPursuitPage(npc,"의뢰를 저장하지 못했습니다."));
                return false;
            }
        }
        async Task ShowGatheringQuest(LocalNpc npc,GatheringQuest quest){
            if(character!.GatheringDeadline(quest)<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()){
                activePursuitPath=GatheringQuest.OfferPath;
                await Send(GamePackets.NpcPursuitPage(npc,
                    $"{quest.Region}에서 {quest.MaterialName} {quest.Required}개를 {quest.Hours}시간 안에 구해 주시겠어요? 사례비는 {quest.Reward:N0}갈리드예요.",
                    ["예","아니오"]));
                return;
            }
            ulong count=quest.Count(character);
            bool ready=count>=quest.Required;
            if(ready)activePursuitPath=GatheringQuest.TurnInPath;
            string text=ready?$"{quest.MaterialName} {quest.Required}개를 건네주시겠어요?":
                $"{quest.MaterialName}을 모아 와 주세요. 현재 {count}/{quest.Required}개예요.";
            await Send(GamePackets.NpcPursuitPage(npc,text,ready?["예","아니오"]:null));
        }
        async Task ShowKondReferral(LocalNpc npc){
            try{character=presence!.Character=characters.ReferKondQuest(character!.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Kond referral save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"이야기를 저장하지 못했습니다. 다시 말을 걸어 주세요."));
                return;
            }
            string text=character.KondStage==KondQuestStage.Completed
                ?"컨드의 낚싯대라면 에필로리아 병원의 미스토도 알아볼 걸세."
                :"컨드를 잘 알던 사람이 있지. 로랜시아 아처길드의 수련장에게 가 보게.";
            await Send(GamePackets.NpcPursuitPage(npc,text));
        }
        async Task ShowKondQuest(LocalNpc npc){
            string text;
            switch(character!.KondStage){
                case KondQuestStage.None:
                    text="컨드의 이야기는 로랜시아 퍼브의 오블로에게 먼저 들어 보게.";
                    break;
                case KondQuestStage.Completed:
                    text="받아 간 컨드의 낚싯대를 미스토에게 보여 주게.";
                    break;
                default:
                    if(!KondQuest.MeetsMastery(character)){
                        text="낚시, 발굴, 지하탐색 중 한 가지를 20.00 이상 수련하고 다시 오게.";
                    }else if(character.KondStage==KondQuestStage.Referred){
                        text="아처길드를 지킬 비늘방패가 필요하네. 가져오면 컨드가 맡긴 낚싯대를 주겠네. 부탁을 들어주겠나?";
                        activePursuitPath=KondQuest.OfferPath;
                    }else if(NpcMaterials.Count(character,KondQuest.IsShield)==0){
                        text="비늘방패를 구해 오게. 로랜시아 갑옷상점의 세크티가 만들어 줄 수 있다네.";
                    }else{
                        text="비늘방패를 가져왔군. 컨드의 낚싯대와 바꾸겠나?";
                        activePursuitPath=KondQuest.TurnInPath;
                    }
                    break;
            }
            ushort page=activePursuitPath==KondQuest.TurnInPath?KondQuest.TurnInPage:KondQuest.OfferPage;
            await Send(GamePackets.NpcPursuitPage(npc,text,activePursuitPath==null?null:["예","아니오"],page:page));
        }
        async Task AdvanceKondQuest(LocalNpc npc,string path){
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 와 주게."));
                return;
            }
            var before=character!;
            try{
                if(path==KondQuest.OfferPath){
                    character=presence.Character=characters.AcceptKondQuest(before.Id);
                    await ShowKondQuest(npc);
                    return;
                }
                var (updated,result,rod)=characters.CompleteKondQuest(before.Id);
                character=presence.Character=updated;
                if(rod==null){
                    string text=result switch{
                        KondRewardResult.InventoryFull=>"소지품 창에 빈 칸이 필요하네.",
                        KondRewardResult.MissingShield=>"소지품 창에 비늘방패가 있어야 하네.",
                        _=>"먼저 비늘방패 의뢰를 받아 주게."
                    };
                    await Send(GamePackets.NpcPursuitPage(npc,text));
                    return;
                }
                await SendSnapshot([..MaterialDeltas(before,updated,KondQuest.IsShield),
                    ..GamePackets.PositionedInventoryAdd(updated,rod),
                    GamePackets.NpcPursuitPage(npc,"약속대로 컨드의 낚싯대를 주겠네. 미스토에게 보여 주게.")]);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Kond quest save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"의뢰 진행을 저장하지 못했습니다."));
            }
        }
        async Task ShowRaddHunt(LocalNpc npc){
            if(RaddHunt.Active(character!,DateTimeOffset.UtcNow) is not {} progress){
                activePursuitPath=RaddHunt.OfferPath;
                await Send(GamePackets.NpcPursuitPage(npc,"마을 아이들이 안전하게 놀 수 있도록 몬스터를 퇴치해 주시겠어요?",
                    ["예","아니오"]));
                return;
            }
            var hunt=RaddHunt.For(progress.Kind)!;
            bool ready=progress.Kills>=RaddHunt.Required;
            if(ready)activePursuitPath=RaddHunt.TurnInPath;
            await Send(GamePackets.NpcPursuitPage(npc,ready?$"{hunt.Monster} {RaddHunt.Required}마리를 퇴치하셨군요. 사례비를 받으시겠어요?"
                :$"{hunt.Monster} 퇴치 진행 {progress.Kills}/{RaddHunt.Required}마리",ready?["예","아니오"]:null));
        }
        async Task AdvanceRaddHunt(LocalNpc npc,string path,byte choice){
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(path==RaddHunt.OfferPath&&choice==1){
                activePursuitPath=RaddHunt.SelectPath;
                await Send(GamePackets.NpcPursuitPage(npc,"어떤 몬스터를 퇴치해 주실 수 있나요?",["초급몬스터","중급몬스터"]));
                return;
            }
            var previous=character!;
            try{
                if(path==RaddHunt.SelectPath&&choice is 1 or 2){
                    var tier=choice==1?RaddHunt.Beginner:RaddHunt.Intermediate;
                    var hunt=tier[Random.Shared.Next(tier.Length)];
                    character=presence!.Character=characters.AcceptRaddHunt(previous.Id,hunt,DateTimeOffset.UtcNow);
                    if(character.RaddHunt?.Kind!=hunt.Kind){await ShowRaddHunt(npc);return;}
                    string place=hunt.Map==5?"로랜시아 남쪽 들판":"로랜시아 지하 하수도";
                    await Send(GamePackets.NpcPursuitPage(npc,$"{place}에서 {hunt.Monster} {RaddHunt.Required}마리를 2시간 안에 퇴치해 주세요. 사례비는 {hunt.Reward:N0}갈리드입니다."));
                }else if(path==RaddHunt.TurnInPath&&choice==1){
                    var (updated,result)=characters.CompleteRaddHunt(previous.Id,DateTimeOffset.UtcNow);
                    character=presence!.Character=updated;
                    if(result==RaddReward.Paid){
                        await SendWalletUpdate(updated,previous.Gold);
                        await Send(GamePackets.Gold(updated.Gold,1));
                    }
                    string text=result switch{
                        RaddReward.Paid=>$"수고하셨어요. 사례비 {updated.Gold-previous.Gold:N0}갈리드를 받으세요.",
                        RaddReward.Expired=>"의뢰 시간이 지났어요. 다시 의뢰를 받아 주세요.",
                        RaddReward.Incomplete=>"아직 목표 마릿수를 채우지 못했어요.",
                        _=>"보상 갈리드 소지 한도와 지갑을 확인해 주세요."
                    };
                    await Send(GamePackets.NpcPursuitPage(npc,text));
                }
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Radd hunt save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"의뢰 진행을 저장하지 못했습니다."));
            }
        }
        async Task FinishGatheringQuest(LocalNpc npc,GatheringQuest quest){
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 재료를 가져와 주세요."));
                return;
            }
            var before=character!;
            GatheringTurnIn result;
            try{(character,result)=characters.CompleteGatheringQuest(before.Id,quest,DateTimeOffset.UtcNow);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} gathering quest save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"의뢰 완료를 저장하지 못했습니다."));
                return;
            }
            if(result!=GatheringTurnIn.Completed){
                var text=result switch{
                    GatheringTurnIn.Expired=>"의뢰 기한이 지났어요. 다시 의뢰를 받아 주세요.",
                    GatheringTurnIn.MissingItems=>$"{quest.MaterialName} {quest.Required}개를 가져와 주세요.",
                    _=>WorldSettings.CanCreditGold(before.Gold,quest.Reward)
                        ?"보상 갈리드를 받을 지갑을 확인해 주세요.":WorldSettings.GoldLimitMessage
                };
                await Send(GamePackets.NpcPursuitPage(npc,text));
                return;
            }
            presence.Character=character;
            var packets=MaterialDeltas(before,character,quest.IsMaterial).ToList();
            packets.Add(GamePackets.Wallet(character));
            packets.Add(GamePackets.Gold(character.Gold,1));
            packets.Add(GamePackets.NpcPursuitPage(npc,$"{quest.MaterialName}을 구해 주셔서 고마워요! 보상으로 {quest.Reward:N0}갈리드를 드릴게요."));
            await SendSnapshot(packets);
        }
        static IEnumerable<byte[]> MaterialDeltas(Character before,Character after,Func<ItemPlacement,bool> matches){
            var remaining=(after.Items??[]).Where(p=>p.InventorySlot!=0&&matches(p)).ToDictionary(p=>(p.InventorySlot,p.Key));
            var previous=before.Items??[];
            long bagWeight=EquipmentItems.CarriedWeightTotal(previous);
            foreach(var material in previous.Where(p=>p.InventorySlot!=0&&matches(p))){
                remaining.TryGetValue((material.InventorySlot,material.Key),out var rest);
                if(rest?.Quantity==material.Quantity)continue;
                bagWeight-=EquipmentItems.InventoryWeight(material)-(rest==null?0:EquipmentItems.InventoryWeight(rest));
                ushort weight=EquipmentItems.WeightWord(bagWeight);
                yield return rest==null?GamePackets.InventoryRemove(material.InventorySlot,weight):GamePackets.InventoryAdd(rest,weight);
            }
        }
        async Task AdvanceOldSmith(LocalNpc npc,OldSmithPage page,byte[] packet){
            if(character==null||presence==null||packet[1]!=1||packet[6]!=0||packet[7]!=0)return;
            ushort requested=(ushort)((packet[8]<<8)|packet[9]);
            byte action=packet[10];
            if(action==0&&packet.Length==11&&requested==(ushort)page){oldSmithPage=null;return;}
            if(requested!=(ushort)page+1)return;
            var before=character;
            var next=page;
            ItemPlacement? reward=null;
            try{
                if(page==OldSmithPage.Flower&&action==2&&packet.Length>=12&&packet[11]<=12&&packet.Length==12+packet[11]){
                    string answer;
                    try{answer=new PacketReader(packet.AsSpan(11)).Str8().Trim();}
                    catch(DecoderFallbackException){return;}
                    if(answer!="꽃"){
                        await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                        await Send(OldSmithQuest.Packet(npc,page));
                        return;
                    }
                    character=characters.BeginOldSmithQuest(before.Id);
                    next=OldSmithQuest.WeaponPage(character.OldSmithProgress);
                }else if(action==1&&packet.Length==12&&packet[11] is 1 or 2){
                    byte choice=packet[11];
                    if(page==OldSmithPage.Menu)next=choice==1?OldSmithPage.Golem:OldSmithQuest.WeaponPage(before.OldSmithProgress);
                    else if(page is OldSmithPage.Water or OldSmithPage.Rings){
                        if(choice==2){oldSmithPage=null;return;}
                        if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 다시 와 주세요."));return;}
                        var expected=page==OldSmithPage.Water?OldSmithStage.Water:OldSmithStage.Rings;
                        OldSmithTurnIn result;
                        (character,result,reward)=characters.AdvanceOldSmithQuest(before.Id,expected);
                        if(result is OldSmithTurnIn.MissingMaterials or OldSmithTurnIn.InventoryFull){
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            await SendSnapshot([GamePackets.Message(result==OldSmithTurnIn.MissingMaterials
                                ?"필요한 재료가 부족합니다.":"소지품 창에 빈 칸이 필요합니다."),OldSmithQuest.Packet(npc,page)]);
                            return;
                        }
                        next=OldSmithQuest.WeaponPage(character.OldSmithProgress);
                    }else return;
                }else return;
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Old Smith quest save failed: {ex.GetType().Name}");
                await Send(GamePackets.Message("의뢰 진행을 저장하지 못했습니다."));
                return;
            }
            oldSmithPage=next;
            presence.Character=character;
            var replies=MaterialDeltas(before,character,OldSmithQuest.IsIngredient).ToList();
            if(reward!=null)replies.AddRange(GamePackets.PositionedInventoryAdd(character,reward));
            replies.Add(OldSmithQuest.Packet(npc,next));
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            await SendSnapshot(replies);
        }
        async Task ConfirmNpcCrafting(LocalNpc npc,NpcCraftingRecipe recipe){
            if(recipe.Materials.Any(material=>!material.IsAvailable(character!))){
                await Send(GamePackets.NpcPursuitPage(npc,$"필요한 재료는 {recipe.Ingredients}입니다."));
                return;
            }
            activePursuitPath=recipe.ProductKey;
            await Send(GamePackets.NpcPursuitPage(npc,$"{recipe.Ingredients}로 {EquipmentItems.Get(recipe.ProductKey).Name}을 만들어 드릴까요?",
                ["예","아니오"]));
        }
        async Task<bool> FinishNpcCrafting(LocalNpc npc,NpcCraftingRecipe recipe,byte[]? success=null){
            if(presence!.Trade!=null){await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 와 주세요."));return false;}
            var before=character!;
            NpcCraftingResult result;
            ItemPlacement? product;
            try{(character,result,product)=characters.CraftNpcItem(before.Id,recipe);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} NPC crafting save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"제작 결과를 저장하지 못했습니다."));
                return false;
            }
            if(product==null){
                await Send(GamePackets.NpcPursuitPage(npc,result==NpcCraftingResult.InventoryFull?
                    "소지품 창에 빈 칸이 필요해요.":$"{recipe.Ingredients}를 가져와 주세요."));
                return false;
            }
            presence.Character=character;
            await SendSnapshot([..MaterialDeltas(before,character,recipe.IsMaterial),
                ..GamePackets.PositionedInventoryAdd(character,product),
                success??GamePackets.NpcPursuitPage(npc,$"{EquipmentItems.Get(product.Key).Name}을 만들었어요.")]);
            return true;
        }
        async Task ShowErenoa(LocalNpc npc,ErenoaPage page){
            erenoaPage=page;
            await Send(ErenoaQuest.Packet(npc,page));
        }
        async Task AdvanceErenoa(LocalNpc npc,ErenoaPage page,byte[] request){
            if(request[1]!=1||request[6]!=0||request[7]!=0)return;
            int requested=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
            byte action=request[10],choice=request.Length==12?request[11]:(byte)0;
            if(action==0&&requested==(ushort)page){erenoaPage=null;return;}
            if(requested!=(ushort)page+1)return;
            if(page==ErenoaPage.Offer&&action==1&&choice==2){erenoaPage=null;return;}
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            switch(page,action,choice){
                case (ErenoaPage.Menu,1,1):await ShowErenoa(npc,ErenoaPage.Offer);break;
                case (ErenoaPage.Menu,1,2):erenoaPage=null;await ShowTorion(npc);break;
                case (ErenoaPage.Offer,1,1):await ShowErenoa(npc,ErenoaPage.Space);break;
                case (ErenoaPage.Space,0,0):
                    erenoaPage=null;
                    if(await FinishNpcCrafting(npc,ErenoaQuest.Exchange,ErenoaQuest.Packet(npc,ErenoaPage.Granted)))
                        erenoaPage=ErenoaPage.Granted;
                    break;
                case (ErenoaPage.Granted,0,0):await ShowErenoa(npc,ErenoaPage.Use);break;
                case (ErenoaPage.Use,0,0):await ShowErenoa(npc,ErenoaPage.Cost);break;
            }
        }
        async Task ShowTorion(LocalNpc npc){
            var current=character!;
            torionOfferedStage=current.TorionProgress;
            torionPage=TorionQuest.IsNpc(npc,torionOfferedStage)&&TorionQuest.CanAdvance(current)
                ?TorionQuest.Page(torionOfferedStage):null;
            await Send(TorionQuest.Offer(npc,current));
        }
        async Task AdvanceTorion(LocalNpc npc,ushort page,byte[] request){
            if(request[1]!=1||U16(request,6)!=TorionQuest.Script)return;
            if(request.Length==11&&U16(request,8)==page){torionPage=null;return;}
            if(request.Length!=12||U16(request,8)!=page+1)return;
            torionPage=null;
            byte choice=request[11];
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(page==TorionQuest.ObloMenuPage){
                if(choice==1)await ShowTorion(npc);
                if(choice==2)await ShowKondReferral(npc);
                if(choice==3)await ShowRaniche(npc);
                return;
            }
            if(choice!=1||!TorionQuest.IsNpc(npc,torionOfferedStage))return;
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 이야기해 주세요."));
                return;
            }
            TorionResult result;
            ItemPlacement? reward;
            try{(character,result,reward)=characters.AdvanceTorion(character!.Id,torionOfferedStage);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Torion quest save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"퀘스트를 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            if(result==TorionResult.InventoryFull){
                await Send(GamePackets.NpcPursuitPage(npc,"소지품에 빈 칸을 만들고 다시 와 주세요."));
                return;
            }
            if(result!=TorionResult.Advanced)return;
            var reply=TorionQuest.Advanced(npc,character.TorionProgress);
            if(reward==null)await Send(reply);
            else await SendSnapshot([..GamePackets.PositionedInventoryAdd(character,reward),reply]);

            static ushort U16(byte[] p,int offset)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(offset));
        }
        async Task ShowRaniche(LocalNpc npc){
            var current=character!;
            activeNpcDialog=npc.Id;
            ranicheOffer=RanicheQuest.CanSpeak(npc,current.RanicheProgress)&&RanicheQuest.CanAdvance(current)
                ?current.RanicheProgress:null;
            await Send(RanicheQuest.Offer(npc,current));
        }
        async Task AdvanceRaniche(LocalNpc npc,RanicheStage stage,byte[] request){
            ushort page=RanicheQuest.Page(stage);
            if(request[1]!=1||request[6]!=(RanicheQuest.Script>>8)||request[7]!=(RanicheQuest.Script&255)||
                request[8]!=0||request[9]!=page+(request.Length==12?1:0))return;
            ranicheOffer=null;
            if(request.Length!=12||request[11]!=1)return;
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(presence!.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 이야기해 주세요."));
                return;
            }
            var previous=character!;
            RanicheResult result;
            ItemPlacement? reward;
            try{(character,result,reward)=characters.AdvanceRaniche(previous.Id,stage);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} Raniche quest save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"퀘스트를 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            if(result!=RanicheResult.Advanced){
                await Send(result==RanicheResult.InventoryFull?GamePackets.NpcPursuitPage(npc,"소지품에 빈 칸을 만들어 주세요."):
                    RanicheQuest.Offer(npc,character));
                return;
            }
            var packets=RanicheQuest.Required(stage) is {} material?MaterialDeltas(previous,character,material.Matches).ToList():[];
            if(reward!=null)packets.Add(GamePackets.InventoryAdd(reward,EquipmentItems.CarriedWeight(character)));
            if(previous.Gold!=character.Gold){
                await SendWalletUpdate(character,previous.Gold);
                packets.Add(GamePackets.Gold(character.Gold,1));
            }
            packets.Add(RanicheQuest.Advanced(npc,character.RanicheProgress));
            await SendSnapshot(packets);
        }
        async Task ShowMetini(LocalNpc npc,MetiniPage page){
            activePursuitPath=null;
            metiniPage=page;
            await Send(MetiniQuest.Packet(npc,page,character!));
        }
        async Task AdvanceMetini(LocalNpc npc,MetiniPage page,byte[] request){
            if(request[1]!=1||System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6))!=MetiniQuest.Script)return;
            int requested=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
            byte action=request[10];
            if(action==0&&request.Length==11&&requested==(ushort)page){metiniPage=null;return;}
            if(requested!=(ushort)page+1)return;
            var before=character!;
            if(MetiniQuest.IsVerse(page)){
                if(action!=2||request.Length<12||request[11]>MetiniQuest.InputLimit||request.Length!=12+request[11])return;
                string answer;
                try{answer=new PacketReader(request.AsSpan(11)).Str8();}
                catch(DecoderFallbackException){return;}
                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                if(answer!=MetiniQuest.Chant[(ushort)page-(ushort)MetiniPage.Verse1]){
                    await SendSnapshot([GamePackets.Message("주문의 글자와 띄어쓰기를 다시 확인해 주세요."),MetiniQuest.Packet(npc,page,before)]);
                    return;
                }
                if(page!=MetiniPage.Verse5){await ShowMetini(npc,page+1);return;}
                if(presence!.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 보상을 받아 주세요."));return;}
                ItemPlacement? reward;
                MetiniResult result;
                try{(character,result,reward)=characters.CompleteMetini(before.Id);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await Send(GamePackets.Message("메티니의 보상을 저장하지 못했습니다."));return;
                }
                if(reward==null){
                    await Send(GamePackets.Message(result==MetiniResult.InventoryFull?
                        "소지품 창에 빈 칸이 필요해요.":"아직 메티니의 보상을 받을 수 없습니다."));
                    return;
                }
                presence.Character=character;
                metiniPage=MetiniPage.Completed;
                await SendSnapshot([..GamePackets.PositionedInventoryAdd(character,reward),MetiniQuest.Packet(npc,MetiniPage.Completed,character)]);
                return;
            }
            if(action==0&&request.Length==11&&page is MetiniPage.Explanation or MetiniPage.Recital or MetiniPage.Review){
                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                await ShowMetini(npc,page+1);
                return;
            }
            if(action!=1||request.Length!=12||page is not (MetiniPage.Payment or MetiniPage.Ready))return;
            if(request[11]!=1){metiniPage=null;return;}
            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
            if(page==MetiniPage.Ready){
                await ShowMetini(npc,before.MetiniProgress==MetiniStage.Paid&&before.MetiniRoosters>=MetiniQuest.Roosters
                    ?MetiniPage.Review:MetiniQuest.Resume(before));
                return;
            }
            if(presence!.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 재료를 건네주세요."));return;}
            MetiniResult payment;
            try{(character,payment)=characters.PayMetini(before.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                await Send(GamePackets.Message("메티니에게 건넨 재료를 저장하지 못했습니다."));return;
            }
            if(payment==MetiniResult.Applied){
                presence.Character=character;
                await SendSnapshot([..MaterialDeltas(before,character,MetiniQuest.IsElsaria)]);
                await SendWalletUpdate(character,before.Gold);
                await Send(GamePackets.Gold(character.Gold));
            }else if(payment!=MetiniResult.AlreadyPaid){
                await SendSnapshot([GamePackets.Message(payment switch{
                    MetiniResult.MissingGold=>"2,500갈리드가 필요해요.",
                    MetiniResult.MissingStones=>"엘사리아 20개가 필요해요.",
                    _=>"아직 마법을 배울 준비가 되지 않았어요."
                }),MetiniQuest.Packet(npc,MetiniPage.Payment,character)]);
                return;
            }
            await ShowMetini(npc,MetiniQuest.Resume(character));
        }
        async Task DressChicken(LocalNpc npc){
            if(presence!.Trade!=null){await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 와 주세요."));return;}
            var before=character!;
            ButcherResult result;
            ItemPlacement? product;
            try{(character,result,product)=characters.DressChicken(before.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} butcher save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"다듬기 결과를 저장하지 못했습니다."));
                return;
            }
            if(product==null){
                await Send(GamePackets.NpcPursuitPage(npc,result==ButcherResult.InventoryFull?
                    "소지품 창에 빈 칸이 필요합니다.":"다듬을 닭고기가 없습니다."));
                return;
            }
            presence.Character=character;
            await SendSnapshot([..MaterialDeltas(before,character,MutaiButcher.IsChicken),
                ..GamePackets.PositionedInventoryAdd(character,product),
                GamePackets.NpcPursuitPage(npc,$"[{MutaiButcher.ProductName}]를 얻다.!!")]);
        }
        async Task FinishJewelry(LocalNpc npc,JewelryRecipe recipe){
            if(presence!.Trade!=null){await Send(GamePackets.NpcPursuitPage(npc,"교환을 끝낸 뒤 다시 와 주세요."));return;}
            var before=character!;
            JewelryResult result;
            ItemPlacement? product;
            try{(character,result,product)=characters.CraftJewelry(before.Id,recipe);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} jewelry save failed: {ex.Message}");
                await Send(GamePackets.NpcPursuitPage(npc,"가공 결과를 저장하지 못했어요."));
                return;
            }
            if(product==null){
                await Send(GamePackets.NpcPursuitPage(npc,result switch {
                    JewelryResult.InsufficientGold=>$"가공비 {recipe.Fee}갈리드가 필요해요.",
                    JewelryResult.InventoryFull=>"소지품 창에 빈 칸이 필요해요.",
                    _=>$"{recipe.Requirements}를 가져와 주세요."
                }));
                return;
            }
            presence.Character=character;
            await SendSnapshot(MaterialDeltas(before,character,i=>recipe.IsRing(i)||recipe.IsStone(i)).ToList());
            long beforeProduct=EquipmentItems.CarriedWeightTotal(character.Items??[])-EquipmentItems.InventoryWeight(product);
            await SendWalletUpdate(character,before.Gold,EquipmentItems.WeightWord(beforeProduct));
            await SendSnapshot([GamePackets.Gold(character.Gold,1),
                ..GamePackets.PositionedInventoryAdd(character,product),
                GamePackets.NpcPursuitPage(npc,$"{EquipmentItems.Get(product.Key).Name}을 만들었어요.")]);
        }
        async Task<bool> LeaveGuild(LocalNpc npc){
            var previous=character!;
            try{character=characters.LeaveGuild(previous.Id);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} guild departure save failed: {ex.Message}");
                await Send(GuildFoundingDialog.Notice(npc,"길드 탈퇴를 저장하지 못했습니다."));
                return false;
            }
            if(character==previous){
                var reason=character.GuildName==null?"가입한 길드가 없습니다.":
                    character.GuildLeaderId==character.Id?"길드 마스터는 이 메뉴에서 탈퇴할 수 없습니다.":"길드헌납금 100000 갈리드가 부족합니다.";
                await Send(GuildFoundingDialog.Notice(npc,reason));
                return false;
            }
            presence!.Character=character;
            await SendWalletUpdate(character,previous.Gold);
            await Send([..GamePackets.Status(character,mask:0x50,state:1),0]);
            return true;
        }
        async Task TravelFromNpc(NpcPursuitTravel route,uint fee=0){
            var current=character!;
            var to=route.Destination;
            if(await BlockRegionTravel(to.Map))return;
            if(!world.GetMap(to.Map).CanStand(to.X,to.Y,to.Floor)||world.IsOccupied(to.Map,to.X,to.Y,to.Floor)||
                hub.IsOccupied(to.Map,to.X,to.Y,to.Floor,current.Id)){
                await Send(GamePackets.Message("이동할 위치를 사용할 수 없습니다."));
                return;
            }
            Character? updated;
            if(fee==0)updated=SavePosition(current,to.X,to.Y,current.Direction,to.Map,to.Floor);
            else{
                if(presence!.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 이동해 주세요."));return;}
                if(current.Gold<fee){await Send(GamePackets.Message($"이동에 {fee}갈리드가 필요합니다."));return;}
                try{updated=characters.PayNpcTravel(current.Id,to,fee);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"{role}/{id} paid NPC travel save failed: {ex.Message}");
                    updated=null;
                }
                if(updated?.Gold==current.Gold){await Send(GamePackets.Message($"이동에 {fee}갈리드가 필요합니다."));return;}
            }
            if(updated==null){await Send(GamePackets.Message("이동 위치를 저장하지 못했습니다."));return;}
            if(route.Sound is ushort sound)await Send(GamePackets.SoundEffect(sound));
            await EnterMap(updated,recordedArrival:true);
            if(fee!=0){
                await SendWalletUpdate(updated,current.Gold);
                await Send(GamePackets.Gold(updated.Gold));
            }
        }
        async Task ShowShrinePage(ShrinePage page,ShrineJob? job=null,byte aptitude=0,byte? previousOrder=null,
            ShrineGrade? skill=null,string? result=null){
            ClearNpcDialogs();
            shrinePage=page;
            pendingShrineGrade=skill;
            shrineJob=job;
            shrinePreviousFateOrder=previousOrder;
            shrineFateAptitude=aptitude;
            await Send(ShrineDialog.Packet(page,job,character,aptitude,previousOrder,skill,result));
        }
        async Task AdvanceFameDialog(FameExchangeDialog dialog,byte[] request){
            if(character==null||presence==null||!FameExchangeDialog.InRange(character)){fameDialog=null;return;}
            if(request[1]!=4||request[6]!=0||request[7]!=0||request[8]!=0)return;
            byte page=request[9],action=request[10],choice=0;
            string? input=null;
            switch(action){
                case 0 when request.Length==11:break;
                case 1 when request.Length==12:choice=request[11];break;
                case 2 when request.Length>=12&&request[11]<=FameExchangeDialog.InputLimit&&request.Length==12+request[11]:
                    try{input=new PacketReader(request.AsSpan(11)).Str8();}
                    catch(DecoderFallbackException){return;}
                    break;
                default:return;
            }
            var previous=character;
            bool walletLimit=false;
            bool Exchange(uint spent,uint credit){
                if(!WorldSettings.CanCreditGold(previous.Gold,credit)){walletLimit=true;return false;}
                Character updated;
                try{updated=characters.ExchangeFame(previous.Id,spent,credit);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"{role}/{id} fame exchange save failed: {ex.Message}");
                    return false;
                }
                if(updated==previous)return false;
                character=presence.Character=updated;
                return true;
            }
            if(!dialog.Advance(page,action,choice,input,previous,Exchange))return;
            if(dialog.Closed){fameDialog=null;return;}
            var current=character!;
            if(current==previous){
                if(walletLimit)await Send(GamePackets.Message(WorldSettings.GoldLimitMessage));
                await Send(dialog.Packet(current));
                return;
            }
            var priorWalletPosition=current with {WalletX=previous.WalletX,WalletY=previous.WalletY};
            await SendSnapshot([GamePackets.PaddedStatus(current,0x48),GamePackets.Wallet(priorWalletPosition),
                GamePackets.Wallet(current),GamePackets.Status(current,mask:0x10),GamePackets.SoundEffect(70),dialog.Packet(current)]);
        }
        async Task AdvanceShrinePage(ShrinePage page,byte action,byte choice,string? input=null){
            if(character==null||presence==null||character.AwaitingRevival||!ShrineDialog.InRange(character)){ClearNpcDialogs();return;}
            if((page,action,choice)==(ShrinePage.GradeKind,1,2)){
                await ShowShrinePage(ShrinePage.GradeResult,result:"현재 스펠 그레이드는 이용할 수 없습니다.");
                return;
            }
            if(page==ShrinePage.GradeInput&&action==2&&input!=null){
                var skill=ShrineGrade.Find(input.Trim());
                string? refusal=skill==null?"승급할 수 있는 어빌리티 이름을 확인해 주세요.":skill.Refusal(character);
                if(refusal!=null)await ShowShrinePage(ShrinePage.GradeResult,result:refusal);
                else await ShowShrinePage(ShrinePage.GradeConfirm,skill:skill);
                return;
            }
            if((page,action,choice)==(ShrinePage.GradeConfirm,1,1)&&pendingShrineGrade is {} selected){
                pendingShrineGrade=null;
                if(presence.Trade!=null){
                    await ShowShrinePage(ShrinePage.GradeResult,result:"교환을 끝낸 뒤 다시 시도해 주세요.");
                    return;
                }
                var previous=character;
                string? refusal;
                try{(character,refusal)=characters.RaiseGrade(previous.Id,selected);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await ShowShrinePage(ShrinePage.GradeResult,result:"그레이드를 저장하지 못했습니다.");
                    return;
                }
                presence.Character=character;
                if(refusal!=null){
                    await ShowShrinePage(ShrinePage.GradeResult,result:refusal);
                    return;
                }
                await SendSnapshot(MaterialDeltas(previous,character,ShrineGrade.IsGem).ToList());
                await SendWalletUpdate(character,previous.Gold);
                byte rank=selected.Rank(character);
                byte[][] refresh=selected.Combat is {} ability
                    ?[GamePackets.AbilityMastery(character,ability),GamePackets.AbilityProgress(character,ability)]
                    :selected==ShrineGrade.Angling
                        ?[GamePackets.FishingMastery(character),GamePackets.FishingProgress(character)]
                        :[GamePackets.ConversationAbility(0,rank),GamePackets.ConversationProgress(0,rank)];
                await SendSnapshot([[..GamePackets.Gold(character.Gold,1),0],..refresh]);
                await ShowShrinePage(ShrinePage.GradeResult,result:$"{selected.Name}의 그레이드가 {rank}이 되었습니다.");
                return;
            }
            if(page==ShrinePage.FateChoices&&action==1&&choice is >=1 and <=3){
                var previous=character;
                byte previousOrder=previous.AptitudeOrder();
                if(presence.Trade!=null){
                    ClearNpcDialogs();
                    await Send(GamePackets.Message("교환을 끝낸 뒤 성향포인트를 올려 주세요."));
                    return;
                }
                try{character=presence.Character=characters.AllocateFatePoint(previous.Id,choice);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    ClearNpcDialogs();
                    await Send(GamePackets.Message("성향포인트를 저장하지 못했습니다."));
                    return;
                }
                if(character==previous){await ShowShrinePage(ShrinePage.FateEmpty);return;}
                await ShowShrinePage(ShrinePage.FateAllocated,aptitude:(byte)(choice-1),
                    previousOrder:previousOrder!=character.AptitudeOrder()?previousOrder:null);
                return;
            }
            if(page==ShrinePage.FateAllocated&&action==0&&choice==0){
                await ShowShrinePage(ShrinePage.FateRemaining,aptitude:shrineFateAptitude,previousOrder:shrinePreviousFateOrder);
                return;
            }
            if(page==ShrinePage.FateRemaining&&action==0&&choice==0&&shrinePreviousFateOrder is {} order){
                await ShowShrinePage(ShrinePage.FateOrderChanged,aptitude:shrineFateAptitude,previousOrder:order);
                return;
            }
            if(page==ShrinePage.FateAllocated&&action!=0)return;
            bool leave=(page,action,choice) is (ShrinePage.LeaveJob,1,1);
            if(leave||(page,action,choice) is (ShrinePage.MartialCheck or ShrinePage.JobCheck,0,0)&&shrineJob!=null){
                var job=leave?null:shrineJob;
                if(presence.Trade!=null){
                    ClearNpcDialogs();
                    await Send(GamePackets.Message("교환을 끝낸 뒤 직업을 변경해 주세요."));
                    return;
                }
                uint previousGold=character.Gold;
                JobChangeResult result;
                try{(character,result)=characters.ChangeJob(character.Id,job);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    ClearNpcDialogs();
                    await Send(GamePackets.Message("직업 변경을 저장하지 못했습니다."));
                    return;
                }
                presence.Character=character;
                if(result==JobChangeResult.Changed){
                    if(previousGold!=character.Gold){
                        await SendWalletUpdate(character,previousGold);
                        await Send([..GamePackets.Gold(character.Gold,1),0]);
                    }
                    await Send(GamePackets.JobList(character,portalPadding:true));
                    if(!leave)await Send(GamePackets.SoundEffect(51));
                    if(selfLookRequested)await Send(GamePackets.SelfLook(character));
                }
                var reply=result switch {
                    JobChangeResult.Changed when leave=>ShrinePage.Commoner,
                    JobChangeResult.Changed when page==ShrinePage.MartialCheck=>ShrinePage.MartialSuccess,
                    JobChangeResult.Changed=>ShrinePage.JobSuccess,
                    JobChangeResult.SkillRequired=>ShrinePage.JobSkillRequired,
                    JobChangeResult.InsufficientGold=>ShrinePage.InsufficientGold,
                    _=>leave?ShrinePage.AlreadyCommoner:ShrinePage.AlreadyJob
                };
                await ShowShrinePage(reply,job);
                return;
            }
            if(ShrineDialog.Next(page,action,choice,character.Job!=null) is {} next){
                var job=(page,action,choice) is (ShrinePage.Jobs or ShrinePage.JobDescriptions,1,>=1 and <=10)
                    ?ShrineJob.All[choice-1]:shrineJob;
                await ShowShrinePage(next,job);
            }else ClearNpcDialogs();
        }
        Task SendShopCatalog(LocalNpc merchant,IEnumerable<string>? purchased=null){
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LocalNpc stock;
            try{stock=characters.RefreshShopStock(merchant,now,purchased);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} shop restock save failed: {ex.Message}");
                stock=characters.WithShopStock(merchant,now);
            }
            return Send(GamePackets.ShopInventory(stock));
        }
        async Task BuyItems(LocalNpc merchant,byte[] packet){
            if(character==null||presence==null)return;
            async Task Finish(string message,bool completed=false,IEnumerable<string>? purchased=null){
                await Send(GamePackets.ShopNotice(packet[8],completed,message));
                await SendShopCatalog(merchant,purchased);
            }
            if(presence.Trade!=null){await Finish("교환을 끝낸 뒤 상점을 이용해 주세요.");return;}
            (ShopEntry? Entry,byte Quantity)[] order;
            try{order=PacketReader.ShopOrder(packet,merchant);}
            catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException){
                await Finish("상점 선택을 다시 확인해 주세요.");return;
            }
            if(order.Length==0){await Finish("구매할 물건을 선택해 주세요.");return;}
            if(order.Any(line=>line.Entry==null||line.Quantity==0)){
                await Finish("상점 선택을 다시 확인해 주세요.");return;
            }
            static (ShopEntry Entry,int Quantity)[] Totals(IEnumerable<(ShopEntry? Entry,byte Quantity)> lines)=>
                lines.GroupBy(line=>line.Entry!).Select(group=>(Entry:group.Key,Quantity:group.Sum(line=>line.Quantity))).ToArray();
            var totals=Totals(order);
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var available=characters.WithShopStock(merchant,now).Shop!;
            if(totals.Any(line=>MerchantRestock.Capacity(merchant,line.Entry).HasValue&&
                line.Quantity>available.Single(entry=>MerchantRestock.StockKey(merchant,entry)==MerchantRestock.StockKey(merchant,line.Entry)).Flags)){
                await Finish("상점에 남은 수량이 부족합니다.");return;
            }
            if(totals.Sum(line=>(long)line.Entry.Price*line.Quantity)>character.Gold){
                await Finish("골드가 부족합니다.");return;
            }
            var items=character.Items??[];
            var next=items.ToList();
            var grants=new List<(ItemPlacement Item,uint Cost,bool NewSlot,long AddedWeight,(ushort X,ushort Y) PreviousPosition)>();
            var free=new Queue<byte>(character.FreeInventorySlots(items));
            uint cost=0;
            int completedLines=0;
            uint? refusedCost=null;
            foreach(var line in order){
                var entry=line.Entry!;
                uint lineCost=entry.Price*line.Quantity;
                var item=EquipmentItems.Get(entry.Key);
                bool stackable=item.EquipmentSlot==0&&item.InventoryFlag1==1;
                uint capacity=stackable?(entry.Key=="ras-feather"?20u:uint.MaxValue):1;
                long stackSpace=stackable?next.Where(p=>p.InventorySlot!=0&&p.Key==entry.Key&&p.Quantity<capacity)
                    .Sum(p=>(long)(capacity-p.Quantity)):0;
                long unstacked=Math.Max(0,line.Quantity-stackSpace);
                if((unstacked+capacity-1)/capacity>free.Count){refusedCost=lineCost;break;}
                cost+=lineCost;
                completedLines++;
                ushort? durability=item.EquipmentSlot==0?null:
                    merchant.Category==2?entry.Durability??EquipmentItems.FullDurability:EquipmentItems.FullDurability;
                for(uint remaining=line.Quantity;remaining>0;){
                    int stack=stackable?next.FindIndex(p=>p.InventorySlot!=0&&p.Key==entry.Key&&p.Quantity<capacity):-1;
                    ItemPlacement placement;
                    (ushort X,ushort Y) previousPosition=default;
                    uint quantity;
                    if(stack>=0){
                        quantity=Math.Min(remaining,capacity-next[stack].Quantity);
                        placement=next[stack] with {Quantity=next[stack].Quantity+quantity};
                        next[stack]=placement;
                    }else{
                        byte slot=free.Dequeue();
                        quantity=Math.Min(remaining,capacity);
                        previousPosition=inventoryPositions.GetValueOrDefault(slot);
                        ushort x=(ushort)Random.Shared.Next(25,226),y=(ushort)Random.Shared.Next(15,86);
                        if((x,y)==previousPosition)x=(ushort)(x==225?25:x+1);
                        placement=new(entry.Key,slot,quantity,x,y,durability){ExpiresAt=ElBlessing.NewItemDeadline(entry.Key,now)};
                        next.Add(placement);
                    }
                    grants.Add((placement,entry.Price*quantity,stack<0,(long)item.EquipmentWord2*quantity,previousPosition));
                    remaining-=quantity;
                }
            }
            uint balance=character.Gold;
            (ushort walletX,ushort walletY)=(character.WalletX,character.WalletY);
            (ushort X,ushort Y)? refundWalletPosition=null;
            uint refundCost=refusedCost.GetValueOrDefault();
            bool refundDue=refundCost>0&&refundCost<=balance-cost;
            if(refundDue&&character.WalletSlot!=0)refundWalletPosition=NewBagPosition(character.WalletX,character.WalletY);
            var completedTotals=refusedCost.HasValue?Totals(order.Take(completedLines)):totals;
            if(completedLines!=0||refundWalletPosition.HasValue){
                try{
                    var purchased=characters.PurchaseShopItems(character.Id,next.ToArray(),cost,merchant,completedTotals,now,refundWalletPosition);
                    if(purchased==null){await Finish("상점에 남은 수량이 부족합니다.");return;}
                    character=purchased;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    Console.Error.WriteLine($"{role}/{id} shop purchase save failed: {ex.Message}");
                    await Finish("상점 거래를 저장하지 못했습니다.");return;
                }
            }
            presence.Character=character;
            long bagWeight=EquipmentItems.CarriedWeightTotal(items);
            foreach(var grant in grants){
                uint previousGold=balance;
                balance-=grant.Cost;
                var priced=character with {Gold=balance,WalletX=walletX,WalletY=walletY};
                await SendWalletUpdate(priced,previousGold,EquipmentItems.WeightWord(bagWeight));
                bagWeight+=grant.AddedWeight;
                ushort carriedWeight=EquipmentItems.WeightWord(bagWeight);
                await SendSnapshot(GamePackets.PurchasedItem(priced,grant.Item,carriedWeight,grant.NewSlot,presence.CombatMode,now,grant.PreviousPosition));
            }
            // The debit and first refund wallet keep the old position; the second refund places it again.
            if(refundDue){
                var previousWallet=character with {WalletX=walletX,WalletY=walletY};
                var provisional=previousWallet with {Gold=character.Gold-refundCost};
                bool beta=presence.StatusDimension==1;
                int refundDimension=beta?1:0;
                ushort weight=EquipmentItems.WeightWord(bagWeight);
                var refund=new List<byte[]>();
                if(character.WalletSlot!=0)
                    refund.Add(provisional.Gold==0?GamePackets.InventoryRemove(character.WalletSlot,weight):GamePackets.Wallet(provisional,weight));
                refund.Add(GamePackets.ShopBalanceStatus(provisional,presence.CombatMode,refundDimension,padded:false));
                if(character.WalletSlot!=0)
                    refund.AddRange([GamePackets.Wallet(previousWallet,weight,padded:false),GamePackets.Wallet(character,weight,padded:!beta)]);
                refund.Add(GamePackets.ShopBalanceStatus(character,presence.CombatMode,refundDimension,padded:beta));
                await SendSnapshot(refund);
            }
            const string noSpace="소지품 창에 빈 칸이 없습니다.";
            if(completedLines==0){await Finish(noSpace);return;}
            string result;
            if(!refusedCost.HasValue&&completedTotals is [{Quantity:1} single]){
                var name=EquipmentItems.Get(single.Entry.Key).Name;
                result=FormattableString.Invariant($"{name}: {cost} 갈리드입니다.");
            }else{
                string purchasedItems=string.Join(", ",completedTotals.Select(line=>$"{EquipmentItems.Get(line.Entry.Key).Name} {line.Quantity}개"));
                result=$"{purchasedItems}니까.. {cost} 갈리드입니다.";
            }
            await Finish(refusedCost.HasValue?$"{result} {noSpace}":result,completed:true,purchased:completedTotals.Select(line=>line.Entry.Key));
        }
        static (ushort X,ushort Y) NewBagPosition(ushort previousX,ushort previousY){
            ushort x=(ushort)Random.Shared.Next(25,225),y=(ushort)Random.Shared.Next(15,85);
            if(x==previousX&&y==previousY)x=(ushort)(x==224?25:x+1);
            return (x,y);
        }
        async Task SendWalletUpdate(Character current,uint previousGold,ushort? carriedWeight=null){
            if(current.WalletSlot==0||current.Gold==previousGold)return;
            ushort weight=carriedWeight??EquipmentItems.CarriedWeight(current);
            await Send(current.Gold==0?GamePackets.InventoryRemove(current.WalletSlot,weight):GamePackets.Wallet(current,weight));
        }
        async Task CompleteCastleOffice(LocalNpc clerk,bool recharge,string nextPage){
            if(presence==null||character==null||character.AwaitingRevival)return;
            if(presence.Trade!=null){
                await Send(GamePackets.NpcPursuitPage(clerk,"교환을 끝낸 뒤 다시 와 주세요."));
                return;
            }
            string? refusal=null;
            try{
                if(recharge){
                    (character,bool applied)=characters.ChargeStartingBattlePoints(character.Id);
                    if(!applied)refusal=character.BattlePoints!=0?"이미 배틀포인트가 있습니다.":"갈리드가 부족합니다.";
                }else{
                    (character,JobChangeResult result)=characters.ChangeJob(character.Id,null,CastleOffice.ResignationFee);
                    if(result!=JobChangeResult.Changed)
                        refusal=result==JobChangeResult.AlreadySelected?"포기할 직업이 없습니다.":"갈리드가 부족합니다.";
                }
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                await Send(GamePackets.NpcPursuitPage(clerk,"처리 결과를 저장하지 못했습니다."));
                return;
            }
            presence.Character=character;
            if(refusal!=null){await Send(GamePackets.NpcPursuitPage(clerk,refusal));return;}
            var packets=new List<byte[]>();
            if(character.WalletSlot!=0){
                ushort weight=EquipmentItems.CarriedWeight(character);
                packets.Add(character.Gold==0?GamePackets.InventoryRemove(character.WalletSlot,weight):GamePackets.Wallet(character,weight));
            }
            packets.Add(GamePackets.ShopBalanceStatus(character,presence.CombatMode,0,padded:recharge));
            if(recharge){
                packets.Add(GamePackets.SoundEffect(70));
                packets.Add(GamePackets.NpcPursuit(clerk,nextPage)??GamePackets.NpcPursuitPage(clerk,"충전완료!!",next:true));
                activePursuitPath=nextPage;
            }else packets.Add(GamePackets.JobList(character,portalPadding:true));
            await SendSnapshot(packets);
        }
        async Task OpenShopService(LocalNpc merchant,ushort action){
            if(character==null||presence==null)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 상점을 이용해 주세요."));return;}
            bool repair=action==GamePackets.ShopRepairAction;
            bool butcher=MutaiButcher.IsNpc(merchant);
            var offers=new List<ShopOffer>();
            foreach(var placement in character.Items??[]){
                var item=EquipmentItems.Get(placement.Key);
                byte kind=placement.InventorySlot==0?(byte)1:(byte)0;
                ushort maximum=EquipmentItems.MaximumDurability(item);
                if(repair){
                    uint missing=(uint)(maximum-EquipmentItems.CurrentDurability(placement,maximum));
                    if(item.EquipmentSlot==0||missing==0||placement.Quantity!=1)continue;
                    uint price=(uint)(((ulong)world.RepairBasePrice(merchant.Map,placement.Key)*missing+maximum-1)/maximum);
                    offers.Add(new(kind,kind==1?item.EquipmentSlot:placement.InventorySlot,placement,price));
                }else if(kind==0&&placement.Quantity>0){
                    uint price;
                    if(butcher&&MutaiButcher.IsProduct(placement))price=MutaiButcher.ProductSellPrice;
                    else if(butcher&&MutaiButcher.IsChicken(placement))price=MutaiButcher.ChickenSellPrice;
                    else if(merchant.Shop?.FirstOrDefault(entry=>entry.Key==placement.Key&&entry.Price!=0) is {} stock){
                        uint value=item.EquipmentSlot==0?maximum:EquipmentItems.CurrentDurability(placement,maximum);
                        price=stock.SellPrice is uint quote&&quote>0
                            ?(uint)Math.Max(1UL,(ulong)quote*value/maximum)
                            :(uint)Math.Max(1UL,(ulong)stock.Price*value*45/(100UL*maximum));
                    }else continue;
                    offers.Add(new(kind,placement.InventorySlot,placement,price));
                }
            }
            var list=offers.OrderBy(o=>o.Kind).ThenBy(o=>o.Slot).ToArray();
            activeShop=null;
            activeShopService=(merchant.Id,repair?GamePackets.ShopRepairOrder:GamePackets.ShopSellOrder,list);
            await Send(repair?GamePackets.ShopRepairList(merchant,list):GamePackets.ShopSellList(merchant,list));
        }
        async Task CommitShopService(LocalNpc merchant,byte[] packet,ushort order,ShopOffer[] offers){
            if(character==null||presence==null)return;
            activeShopService=null;
            bool repair=order==GamePackets.ShopRepairOrder;
            Task Report(string message,bool completed=false)=>Send(repair?GamePackets.Message(message):GamePackets.ShopNotice(packet[8],completed,message));
            if(presence.Trade!=null){await Report("교환을 끝낸 뒤 상점을 이용해 주세요.");return;}
            (byte Kind,byte Slot,byte Quantity)[] selection;
            try{selection=PacketReader.ShopServiceOrder(packet,repair);}
            catch(InvalidDataException){await Report("상점 선택을 다시 확인해 주세요.");return;}
            if(selection.Length==0)return;
            var next=(character.Items??[]).ToList();
            var updates=new List<(ShopOffer Offer,ItemPlacement Updated,ulong Price)>();
            var usedSlots=new HashSet<(byte Kind,byte Slot)>();
            ulong total=0;
            foreach(var line in selection){
                var offer=offers.FirstOrDefault(o=>o.Kind==line.Kind&&o.Slot==line.Slot);
                int itemIndex=offer==null?-1:next.IndexOf(offer.Item);
                if(offer==null||itemIndex<0||!usedSlots.Add((line.Kind,line.Slot))||line.Quantity==0||line.Quantity>offer.Item.Quantity){
                    await Report("소지품이 바뀌었습니다. 목록을 다시 열어 주세요.");
                    return;
                }
                ulong price=(ulong)offer.Price*line.Quantity;
                total+=price;
                var updated=repair?EquipmentItems.RestoreDurability(offer.Item):
                    offer.Item with {Quantity=offer.Item.Quantity-line.Quantity};
                if(updated.Quantity==0)next.RemoveAt(itemIndex);else next[itemIndex]=updated;
                updates.Add((offer,updated,price));
            }
            if(repair&&total>character.Gold){await Report("수리할 갈리드가 부족합니다.");return;}
            if(!repair&&!WorldSettings.CanCreditGold(character.Gold,total)){await Report(WorldSettings.GoldLimitMessage);return;}
            var previous=character;
            uint balance=previous.Gold;
            long bagWeight=EquipmentItems.CarriedWeightTotal(previous.Items??[]);
            (ushort X,ushort Y)? walletPosition=!repair&&updates.Count==1&&updates[0].Updated.Quantity==0
                ?NewBagPosition(previous.WalletX,previous.WalletY):null;
            try{character=characters.SetItems(character.Id,next.ToArray(),gold:repair?balance-(uint)total:balance+(uint)total,
                walletPosition:walletPosition);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} shop service save failed: {ex.Message}");
                await Report("상점 거래를 저장하지 못했습니다.");
                return;
            }
            presence.Character=character;
            if(repair){
                var replies=new List<byte[]>();
                byte[]? appearance=null;
                ushort carriedWeight=EquipmentItems.WeightWord(bagWeight);
                int equippedWeight=EquipmentItems.EquippedWeight(character);
                for(int i=0;i<updates.Count;i++){
                    var (offer,updated,price)=updates[i];
                    balance-=(uint)price;
                    var priced=character with {Gold=balance};
                    if(priced.WalletSlot!=0)
                        replies.Add(balance==0?GamePackets.InventoryRemove(priced.WalletSlot,carriedWeight)
                            :GamePackets.Wallet(priced,carriedWeight,padded:false));
                    // The captured batch pads its opening status and repaired worn armor/accessories.
                    bool padded=i==0||offer.Kind==1&&offer.Slot!=1;
                    replies.Add(GamePackets.ShopBalanceStatus(priced,presence.CombatMode,presence.StatusDimension,padded));
                    if(offer.Kind==1){
                        replies.Add(GamePackets.ShopRepairedEquipment(EquipmentItems.Get(updated.Key),equippedWeight));
                        replies.Add(appearance??=GamePackets.Human(character));
                    }else{
                        var itemUpdate=GamePackets.InventoryAdd(updated,carriedWeight);
                        // The recorded bag repair of the Tis Battle Axe ends with one zero byte.
                        replies.Add(updated.Key==EquipmentItems.TisBattleAxeKey?[..itemUpdate,0]:itemUpdate);
                    }
                }
                replies.Add(GamePackets.ShopRepairReceipt(updates.Select(line=>(EquipmentItems.Get(line.Updated.Key).Name,line.Price)),total));
                if(selfLookRequested&&appearance!=null)replies.Add(GamePackets.SelfLook(character));
                await SendSnapshot(replies);
                if(appearance!=null)await hub.BroadcastVisible(character,appearance,except:character.Id);
                return;
            }
            foreach(var (offer,updated,_) in updates){
                bagWeight-=EquipmentItems.InventoryWeight(offer.Item)-EquipmentItems.InventoryWeight(updated);
                ushort carriedWeight=EquipmentItems.WeightWord(bagWeight);
                await Send(updated.Quantity==0?GamePackets.InventoryRemove(updated.InventorySlot,carriedWeight,reason:9)
                    :GamePackets.InventoryAdd(updated,carriedWeight));
            }
            // The first update keeps the old wallet location; the second places the credited wallet.
            if(walletPosition.HasValue)
                await SendWalletUpdate(character with {WalletX=previous.WalletX,WalletY=previous.WalletY},balance);
            await SendWalletUpdate(character,balance);
            await Send(GamePackets.Gold(character.Gold,presence.CombatMode));
            string receipt=string.Join("\n",updates.Select(update=>{
                string name=EquipmentItems.Get(update.Offer.Item.Key).Name;
                char last=name[^1];
                string particle=last is >= '가' and <= '힣'&&(last-'가')%28!=0?"을":"를";
                return $"{name}{particle} 하나에 {update.Offer.Price}갈리드씩 {update.Price}갈리드에 사드립니다.";
            }));
            await Report(receipt,completed:true);
            await OpenShopService(merchant,GamePackets.ShopSellAction);
        }
        static bool NearCraftStation(Character player,LocalNpc station)=>player.Map==station.Map&&player.Floor==station.Floor&&
            Math.Max(Math.Abs(station.X-player.X),Math.Abs(station.Y-player.Y))<=6;
        async Task OpenCraft(uint? requestedStation=null,CookingRecipe? recipe=null){
            if(character==null||presence==null||presence.CraftStation!=null)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 제작을 열어 주세요."));return;}
            var station=world.InMap(character.Map).FirstOrDefault(n=>(recipe!=null?CookingRecipe.IsKitchen(n):n.Crafting)&&
                (!requestedStation.HasValue||n.Id==requestedStation)&&NearCraftStation(character,n));
            if(station==null){await Send(GamePackets.Message("로컬 안내 NPC 가까이에서 제작을 열어 주세요."));return;}
            long now=Environment.TickCount64;
            // The installed v611 core ignores repeated craft windows within two seconds.
            if(now<nextCraftOpen){await Send(GamePackets.Message("잠시 뒤 제작 창을 다시 열어 주세요."));return;}
            nextCraftOpen=now+2100;
            presence.CraftStation=station;
            activeRecipe=recipe??CookingRecipe.Egg;
            // The native window looks up both object IDs while building its controls.
            await Send(GamePackets.CraftOpen(character.Id,station.Id));
        }
        async Task CloseCraft(){
            if(presence?.CraftStation==null)return;
            presence.CraftStation=null;
            await Send(GamePackets.CraftClose());
        }
        async Task HandleCraft(byte[] packet){
            if(character==null||presence==null||packet.Length<5||packet[1]!=GamePackets.CraftKind||packet[3]!=GamePackets.CraftCode)return;
            var reader=new PacketReader(packet.AsSpan(5));
            var station=presence.CraftStation;
            switch(packet[4]){
                case 0 when packet.Length==9&&packet[2]==0:
                    await OpenCraft(reader.U32());
                    break;
                case 1 when station!=null&&packet.Length==10&&packet[2]==0:
                    if(reader.U8()!=0||reader.U32()!=station.Id)break;
                    await Send(GamePackets.CraftRecipe(activeRecipe));
                    await Send(GamePackets.CraftResultPreview(activeRecipe));
                    break;
                case 7 when station!=null&&packet.Length==12&&packet[2]==0:
                    if(reader.U8()!=0||reader.U32()!=station.Id||reader.U8()!=0)break;
                    if(character.AwaitingRevival||!NearCraftStation(character,station)||presence.Trade!=null)break;
                    var recipe=activeRecipe;
                    var before=character;
                    bool useKit=CookingRecipe.IsKitchen(station);
                    CookingResult outcome;
                    ItemPlacement? result;
                    try{(character,outcome,result)=characters.Cook(character.Id,recipe,useKit);}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                        await Send(GamePackets.Message("요리 결과를 저장하지 못했습니다."));break;
                    }
                    if(result==null){
                        await Send(GamePackets.Message(outcome switch {
                            CookingResult.InventoryFull=>"소지품 창에 빈 칸이 없습니다.",
                            CookingResult.MissingItems=>$"{recipe.Ingredients}가 필요합니다.",
                            _=>recipe.Requirement(character,useKit)??"지금은 요리할 수 없습니다."
                        }));
                        break;
                    }
                    presence.Character=character;
                    await CloseCraft();
                    await SendSnapshot([..MaterialDeltas(before,character,recipe.IsMaterial),GamePackets.InventoryAdd(result,EquipmentItems.CarriedWeight(character)),
                        GamePackets.Message($"{recipe.Name}을 만들었습니다. 요리 {CookingRecipe.Mastery(character)/100m:0.00}"),
                        ..JobGrowthPackets(before,character)]);
                    break;
                case 8 when station!=null&&packet.Length==9&&packet[2]==0:
                    if(reader.U32()==station.Id)presence.CraftStation=null;
                    break;
            }
        }
        long nextFishingCast=0;
        static (ItemPlacement[] Items,byte[] Packet) SpendFishingBait(ItemPlacement[] items,ItemPlacement bait,bool padded){
            ushort left=(ushort)(Fishing.Remaining(bait)-1);
            if(left==0&&bait.Quantity==1){
                var remaining=ConsumeOne(items,bait);
                return (remaining,GamePackets.ConsumableRemove(bait.InventorySlot,EquipmentItems.WeightWord(EquipmentItems.CarriedWeightTotal(remaining)),padded:padded));
            }
            var updated=left==0?bait with {Quantity=bait.Quantity-1,BaitRemaining=null}:bait with {BaitRemaining=left};
            var next=items.Select(item=>item==bait?updated:item).ToArray();
            var packet=GamePackets.InventoryAdd(updated,EquipmentItems.WeightWord(EquipmentItems.CarriedWeightTotal(next)));
            return (next,padded?[..packet,0]:packet);
        }
        async Task CastFishingLine(Character angler){
            long now=Environment.TickCount64;
            if(now<nextFishingCast||presence==null)return;
            if(!Fishing.CanCast(angler,world.GetMap(angler.Map))){
                await Send(GamePackets.Message("물가에서 물을 바라보고 낚시해 주세요."));
                return;
            }
            var inventory=angler.Items??[];
            var bait=inventory.FirstOrDefault(item=>item.InventorySlot!=0&&item.Key==Fishing.BaitKey&&Fishing.Remaining(item)>0);
            if(bait==null){
                await Send(GamePackets.Message("낚시 미끼가 없습니다."));
                return;
            }
            var (items,baitUpdate)=SpendFishingBait(inventory,bait,padded:true);
            ItemPlacement[] catches=[];
            ushort mastery=Fishing.Mastery(angler);
            if(Random.Shared.NextDouble()<Fishing.SuccessChance(mastery)){
                string catchKey=Fishing.ChooseCatch(mastery);
                if(!Fishing.TryStoreCatch(angler,items,catchKey,out items,out catches)){
                    await Send(GamePackets.Message("소지품 창에 빈 칸이 없습니다."));
                    return;
                }
            }

            try{character=presence.Character=characters.RecordFishingCast(angler.Id,items);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} fishing save failed: {ex.Message}");
                await Send(GamePackets.Message("낚시 결과를 저장하지 못했습니다."));
                return;
            }
            nextFishingCast=now+Fishing.CastIntervalMs;
            byte[] motion=GamePackets.FishingMotion(angler.Id);
            var packets=GamePackets.FishingCastResult(angler,character,baitUpdate,motion,catches,
                slot=>inventoryPositions.GetValueOrDefault(slot));
            packets.AddRange(JobGrowthPackets(angler,character));
            await SendSnapshot(packets);
            await hub.BroadcastVisible(character,motion,except:angler.Id);
        }
        static IEnumerable<byte[]> JobGrowthPackets(Character previous,Character current){
            if(ReferenceEquals(previous.JobPoints,current.JobPoints))yield break;
            yield return GamePackets.JobList(current);
            if(previous.JobStats!=current.JobStats)yield return GamePackets.Status(current,mask:0x40);
        }
        async Task PublishMonsterDefeat(FieldMonster target,FieldObject[] loot){
            var defeated=target.Appearance;
            world.RemoveDefeatedTemporaryMonster(target);
            foreach(var drop in loot)await hub.BroadcastVisible(drop,GamePackets.Objects([],[drop]));
            await hub.BroadcastVisible(defeated,GamePackets.Death(defeated.Id));
        }
        async Task ReportSpellFailure(SpellResult result,LocalSpell spell){
            if(result is SpellResult.MissingMana or SpellResult.MissingStone)
                await Send(GamePackets.Message(result==SpellResult.MissingMana?"마력이 부족합니다.":
                    $"{EquipmentItems.Get(spell.StoneKey).Name}이 부족합니다."));
        }
        async Task SendSpellVitals(Character previous,Character current,LocalSpell spell){
            bool manaRaised=current.TrainingMp>previous.TrainingMp;
            var packets=MaterialDeltas(previous,current,spell.IsStone).ToList();
            packets.Add(manaRaised
                ?GamePackets.PaddedStatus(current,0x60,presence!.CombatMode)
                :GamePackets.CurrentVitals(current));
            if(manaRaised)packets.Add(GamePackets.Message("마력이 상승했습니다."));
            await SendSnapshot(packets);
        }
        async Task SendSpellTraining(Character previous,Character current,LocalSpell spell){
            var element=spell.Element;
            uint before=CombatAbilities.Points(previous,element);
            if(before!=CombatAbilities.Points(current,element)){
                if(before==0||CombatAbilities.Mastery(previous,element)!=CombatAbilities.Mastery(current,element))
                    await Send(GamePackets.AbilityMastery(current,element));
                await Send(GamePackets.AbilityProgress(current,element));
                foreach(var packet in JobGrowthPackets(previous,current))await Send(packet);
            }
            if(spell.Counter(previous)!=spell.Counter(current))
                await Send(GamePackets.SpellProgress(current,spell));
        }
        async Task CastConversionSpell(Character caster,LocalSpell spell){
            long now=Environment.TickCount64;
            if(presence is not {Ready:true}||!spell.IsKnown(caster)||now<nextSpellCast)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 마법을 사용해 주세요."));return;}
            SpellResult result;
            try{(character,result)=characters.RecordConversionCast(caster.Id,spell,trainingManaRoll);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                await Send(GamePackets.Message($"{spell.Name} 시전을 저장하지 못했습니다."));return;
            }
            if(result!=SpellResult.Applied){await ReportSpellFailure(result,spell);return;}
            nextSpellCast=now+spell.CastDelayMs;
            presence.Character=character;
            await SendSpellVitals(caster,character,spell);
            await hub.BroadcastVisible(character,GamePackets.ObjectEffect(character.Id,spell.Icon));
            if(presence.Party is {} group)await group.UpdateHealth(presence);
            await SendSpellTraining(caster,character,spell);
        }
        async Task CastTargetedSpell(Character caster,LocalSpell spell,uint targetId){
            long now=Environment.TickCount64;
            if(presence is not {Ready:true}||!spell.IsKnown(caster)||now<nextSpellCast)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 마법을 사용해 주세요."));return;}
            if(PlayerDebuff.All.FirstOrDefault(debuff=>debuff.Spell==spell) is {} debuff){
                if(!WorldSettings.AllowsDuel(caster.Map)||targetId==caster.Id||hub.FindVisible(caster,targetId) is not {} victim)return;
                var targetCharacter=victim.Character;
                if(targetCharacter.CurrentHp()==0||targetCharacter.Floor!=caster.Floor||
                    Math.Max(Math.Abs(caster.X-targetCharacter.X),Math.Abs(caster.Y-targetCharacter.Y))>DaraSpell.Range||
                    !world.GetMap(caster.Map).ClearAttackPath(caster.X,caster.Y,caster.Floor,targetCharacter.X,targetCharacter.Y))return;
                Character affected;
                SpellResult castResult;
                ushort duration;
                try{
                    (character,affected,castResult,duration)=characters.RecordDebuffCast(caster.Id,targetId,debuff,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),trainingManaRoll);
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await Send(GamePackets.Message($"{spell.Name} 시전을 저장하지 못했습니다."));return;
                }
                if(castResult!=SpellResult.Applied){await ReportSpellFailure(castResult,spell);return;}
                nextSpellCast=now+spell.CastDelayMs;
                presence.Character=character;
                victim.Character=affected;
                byte[] effect=[..GamePackets.ObjectEffect(affected.Id,spell.Icon),0];
                var hit=GamePackets.DebuffHit(caster.Id,affected.Id,debuff);
                await SendSpellVitals(caster,character,spell);
                await WorldHub.NotifyPackets(victim,[GamePackets.TimedSpell(debuff.Timer(duration)),
                    GamePackets.Status(affected,0x40,victim.CombatMode),GamePackets.Message(spell.Name),effect,hit]);
                await hub.BroadcastVisible(affected,effect,except:affected.Id);
                await hub.BroadcastVisible(affected,hit,except:affected.Id);
                await SendSpellTraining(caster,character,spell);
                return;
            }
            if(spell.Slot==RimediumSpell.Slot){
                if(hub.FindVisible(caster,targetId) is not {} recipient)return;
                var patient=recipient.Character;
                if(patient.AwaitingRevival||patient.Floor!=caster.Floor||
                    Math.Max(Math.Abs(caster.X-patient.X),Math.Abs(caster.Y-patient.Y))>DaraSpell.Range||
                    !world.GetMap(caster.Map).ClearAttackPath(caster.X,caster.Y,caster.Floor,patient.X,patient.Y))return;
                Character restored;
                SpellResult healing;
                try{(character,restored,healing)=characters.RecordRimediumCast(caster.Id,patient.Id,RimediumSpell.RollHealing(),trainingManaRoll);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    await Send(GamePackets.Message("리메듐 시전을 저장하지 못했습니다."));return;
                }
                if(healing!=SpellResult.Applied){await ReportSpellFailure(healing,spell);return;}
                nextSpellCast=now+spell.CastDelayMs;
                presence.Character=character;
                recipient.Character=restored;
                await SendSpellVitals(caster,character,spell);
                if(recipient!=presence)await WorldHub.Notify(recipient,GamePackets.CurrentVitals(restored));
                await hub.BroadcastVisible(restored,GamePackets.ObjectEffect(restored.Id,RimediumSpell.Effect));
                if(recipient.Party is {} group)await group.UpdateHealth(recipient);
                await SendSpellTraining(caster,character,spell);
                return;
            }
            if(world.MonsterById(targetId) is not {IsTreasureChest:false} target)return;
            var monster=target.Appearance;
            if(!WorldHub.InMonsterReach(monster,caster,DaraSpell.Range)||
                !world.GetMap(caster.Map).ClearAttackPath(caster.X,caster.Y,caster.Floor,monster.X,monster.Y))return;
            ushort damage=(ushort)Math.Min(target.Hp,spell==LocalSpell.Bris?BrisSpell.RollDamage():DaraSpell.RollDamage());
            bool lethal=damage>=target.Hp;
            ushort killFame=lethal?world.MonsterExperience(monster)??(ushort)0:(ushort)0;
            SpellResult result;
            try{(character,result)=characters.RecordDamageSpell(caster.Id,spell,target.TrainingLimit,lethal?monster:null,killFame,
                DateTimeOffset.UtcNow,trainingManaRoll);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                await Send(GamePackets.Message($"{spell.Name} 시전을 저장하지 못했습니다."));
                return;
            }
            if(result!=SpellResult.Applied){
                await ReportSpellFailure(result,spell);
                return;
            }
            nextSpellCast=now+spell.CastDelayMs;
            presence.Character=character;
            target.Hit(damage,now+target.RespawnDelayMs(respawnSeconds));
            var loot=lethal?world.DropLoot(target):[];
            await SendSpellVitals(caster,character,spell);
            await hub.BroadcastVisible(monster,GamePackets.DamageSpellHit(caster.Id,monster.Id,damage,spell.Icon));
            if(lethal)await PublishMonsterDefeat(target,loot);
            else target.Provoke(presence,now);
            if(killFame!=0)await Send(GamePackets.Status(character,mask:0x40,state:presence.CombatMode));
            if(selfLookRequested&&CombatAbilities.Experience(character)!=CombatAbilities.Experience(caster))
                await Send(GamePackets.SelfLook(character));
            await SendSpellTraining(caster,character,spell);
        }
        async Task Attack(Character attacker,uint? rangedTarget=null,byte heading=0,bool weakMerchantStrike=false){
            if(presence==null)return;
            if(Fishing.HasRod(attacker))return;
            var ability=CombatAbilities.For(attacker);
            if(ability.UsesBow!=rangedTarget.HasValue)return;
            var weapon=EquipmentItems.EquippedWeapon(attacker);
            var weaponItem=weapon==null?null:EquipmentItems.Get(weapon.Key);
            bool bastardSword=weaponItem?.Sprite==0x8007;
            if(weakMerchantStrike&&!bastardSword)return;
            ushort maximumDurability=weaponItem==null?EquipmentItems.FullDurability:EquipmentItems.MaximumDurability(weaponItem);
            ushort durability=weapon==null?(ushort)0:EquipmentItems.CurrentDurability(weapon,maximumDurability);
            if(!weakMerchantStrike&&weapon!=null&&durability==0){await Send(GamePackets.WeaponBrokenNotice());return;}
            FieldMonster? target;
            TrainingDoll? doll;
            LocalNpc? hiredMerchant=null,guildNpc=null;
            WorldSession? opponent=null;
            bool duel=WorldSettings.AllowsDuel(attacker.Map);
            bool CanHitOpponent(Character other,int range){
                if(!duel||other.Id==attacker.Id||other.Map!=attacker.Map||other.Floor!=attacker.Floor||
                    other.CurrentHp()==0)return false;
                int distance=Math.Max(Math.Abs(attacker.X-other.X),Math.Abs(attacker.Y-other.Y));
                return distance>0&&distance<=range&&
                    world.GetMap(attacker.Map).ClearAttackPath(attacker.X,attacker.Y,attacker.Floor,other.X,other.Y);
            }
            if(rangedTarget is uint oid){
                target=world.MonsterById(oid);
                doll=target==null?world.TrainingDollById(oid):null;
                // Four tiles were observed in the bow capture.
                if((target?.Appearance??doll?.Appearance) is {} at){
                    if(!WorldHub.InMonsterReach(at,attacker,4))return;
                }else{
                    if(hub.FindVisible(attacker,oid) is not {} peer||!CanHitOpponent(peer.Character,4))return;
                    opponent=peer;
                }
            }else if(attacker.Direction<4){
                var (dx,dy)=LocalMap.Delta(attacker.Direction);
                int x=attacker.X+dx,y=attacker.Y+dy;
                target=world.MonsterAt(attacker.Map,x,y,attacker.Floor);
                doll=target==null?world.TrainingDollAt(attacker.Map,x,y,attacker.Floor):null;
                if(target==null&&doll==null&&attacker.Map%10000==2&&ability!=CombatAbilities.Harp){
                    var npc=world.InMap(attacker.Map).FirstOrDefault(npc=>npc.Floor==attacker.Floor&&npc.X==x&&npc.Y==y);
                    if(npc!=null&&GuildFoundingDialog.IsGuildNpc(npc))guildNpc=npc;
                    else if(npc is {Category:2,Sprite:0x40C1}&&(weapon==null||bastardSword))hiredMerchant=npc;
                }
                if(duel&&target==null&&doll==null)
                    opponent=hub.InMap(attacker.Map).FirstOrDefault(peer=>peer.Character.X==x&&peer.Character.Y==y&&
                        CanHitOpponent(peer.Character,1));
            }else{target=null;doll=null;}
            if(weakMerchantStrike){
                if(hiredMerchant==null)return;
                if(durability==0){await Send(GamePackets.WeaponBrokenNotice());return;}
            }
            if(presence.CombatMode!=2){
                presence.CombatMode=2;
                await hub.BroadcastCombatMode(attacker,2);
            }
            ushort DamageAgainst(ushort defense)=>CombatAbilities.PhysicalDamage(attacker,ability,weaponItem,defense);
            byte[] HitEffect(uint targetId,ushort damage){
                if(ability.UsesBow)return GamePackets.BowAttack(attacker.Id,targetId,damage,heading,ability.AttackMotion);
                if(ability==CombatAbilities.Harp)
                    return GamePackets.HarpAttack(attacker.Id,targetId,CombatAbilities.Grade(attacker,ability)==0?(ushort)0:damage);
                var (motion,swing,impact)=ability.MeleeVisual(attacker,weaponItem,damage>0);
                return GamePackets.PlayerAttack(attacker.Id,targetId,damage,motion,swing,impact);
            }
            async Task SendSelfProgress(){
                if(selfLookRequested&&CombatAbilities.Experience(character)!=CombatAbilities.Experience(attacker))
                    await Send(GamePackets.SelfLook(character!));
            }
            async Task SendGrowth(Character previous,Character current,bool sendStatus=true,bool comboAward=false,bool fixedDoll=false){
                bool changed=CombatAbilities.Points(current,ability)!=CombatAbilities.Points(previous,ability);
                if(!changed&&!comboAward)return;
                var progress=GamePackets.AbilityProgress(current,ability);
                if(!changed){await Send(progress);return;}
                ushort beforeMastery=CombatAbilities.Mastery(previous,ability);
                ushort mastery=CombatAbilities.Mastery(current,ability);
                bool masteryRaised=mastery>beforeMastery;
                bool unarmed=ability==CombatAbilities.All[0];
                uint experienceAward=CombatAbilities.ObservedMasteryExperience(ability,beforeMastery,mastery)??0;
                bool experienceNotice=experienceAward>0&&CombatAbilities.Experience(current)>CombatAbilities.Experience(previous);
                bool greatswordExperience=experienceNotice&&ability==CombatAbilities.Greatsword;
                bool fullExperienceStatus=experienceNotice&&(unarmed||greatswordExperience&&mastery>=50);
                bool healthRaised=current.TrainingHp>previous.TrainingHp;
                bool healthAtUnarmed20=healthRaised&&unarmed&&beforeMastery<CombatAbilities.UnarmedReferenceBoundary&&
                    mastery>=CombatAbilities.UnarmedReferenceBoundary;
                bool padUnarmedHealth=healthRaised&&masteryRaised&&unarmed&&!fixedDoll;
                bool padDaggerHealth=healthRaised&&masteryRaised&&ability==CombatAbilities.All[3];
                bool baseChanged=previous.JobStats!=current.JobStats||CombatAbilities.Fame(current)!=CombatAbilities.Fame(previous)||
                    CombatAbilities.Level(CombatAbilities.Experience(current))!=CombatAbilities.Level(CombatAbilities.Experience(previous));
                if(healthRaised)presence.TrainingHealthStatusPending=true;
                var packets=new List<byte[]>();
                // Direct unarmed gains can use only the meter, status and mastery row (1:8845–8849).
                if(healthRaised&&!healthAtUnarmed20&&!fixedDoll)packets.Add(GamePackets.Message("체력이 상승했습니다."));
                else if(masteryRaised&&!comboAward&&!unarmed)packets.Add(GamePackets.Message($"{ability.Name} 어빌리티가 상승했습니다."));
                ushort oldReference=CombatAbilities.Reference(previous,ability);
                ushort newReference=CombatAbilities.Reference(current,ability);
                var fixedDollPadding=fixedDoll&&masteryRaised&&mastery<=CombatAbilities.LastRecordedBeginnerDollMastery&&
                    oldReference==CombatAbilities.BeginnerUnarmedReference&&
                    CombatAbilities.UsesRecordedBeginnerDollProfile(previous,ability)
                    ?CombatAbilities.FixedDollMasteryPadding(mastery):default;
                var firstProgress=oldReference==newReference?progress:GamePackets.AbilityProgress(current,ability,
                    (ushort)((CombatAbilities.Points(previous,ability)+CombatAbilities.CountersAdded(previous,current,ability))%oldReference),
                    oldReference);
                packets.Add(padUnarmedHealth||fixedDollPadding.FirstProgress?[..firstProgress,0]:firstProgress);
                if(sendStatus){
                    // Mastery also emits a status marker when there are no base fields to update.
                    if(baseChanged||fullExperienceStatus||masteryRaised&&presence.TrainingHealthStatusPending||
                        healthRaised&&doll is {IsPermanent:false}){
                        var status=GamePackets.Status(current,mask:0x40,state:presence.CombatMode);
                        packets.Add(presence.TrainingHealthStatusPending||greatswordExperience&&fullExperienceStatus?[..status,0]:status);
                    }else if(masteryRaised)packets.Add(comboAward||fixedDollPadding.StatusMarker?[8,0,presence.CombatMode,0]:[8,0,presence.CombatMode]);
                }
                // Retail 1:9024–9030 shows the effect after the new maximum HP is published.
                if(healthAtUnarmed20){
                    packets.Add(GamePackets.Message("체력이 상승했습니다."));
                    packets.Add(GamePackets.ObjectEffect(current.Id,122));
                }
                if(masteryRaised){
                    if(experienceNotice)packets.Add(GamePackets.Message($"경험치 {experienceAward} 획득했습니다."));
                    var row=GamePackets.AbilityMastery(current,ability);
                    packets.Add(padUnarmedHealth||greatswordExperience||fixedDoll&&healthRaised||fixedDollPadding.Mastery?[..row,0]:row);
                    packets.Add(padUnarmedHealth||padDaggerHealth||comboAward&&unarmed||fixedDollPadding.FinalProgress?[..progress,0]:progress);
                }
                if(!ReferenceEquals(previous.JobPoints,current.JobPoints))packets.Add(GamePackets.JobList(current));
                await SendSnapshot(packets);
            }
            (ItemPlacement[]? Placements,ItemPlacement? Stowed,bool Low,bool Broken) WearWeapon(ushort wear=1){
                if(weapon==null)return (null,null,false,false);
                ushort remaining=EquipmentItems.DurabilityAfterWear(durability,maximumDurability,wear);
                var worn=EquipmentItems.WithCurrentDurability(weapon,remaining,maximumDurability);
                bool low=durability*10>maximumDurability&&remaining*10<=maximumDurability;
                bool broken=remaining==0;
                var items=attacker.Items!;
                ItemPlacement? stowed=null;
                if(low||broken){
                    byte free=attacker.FreeInventorySlots(items).FirstOrDefault();
                    if(free!=0)stowed=ForcedBagPlacement(worn,free);
                }
                return (items.Select(p=>ReferenceEquals(p,weapon)?stowed??worn:p).ToArray(),stowed,low,broken);
            }
            async Task SendWeaponWear(Character updated,ItemPlacement? stowed,bool low,bool broken){
                if(stowed!=null){
                    var initial=stowed with {InventoryWord1=weapon!.InventoryWord1??0,InventoryWord2=weapon.InventoryWord2??0};
                    var packets=GamePackets.EquipmentStow(attacker,updated,1,initial,stowed,presence.CombatMode,presence.VitalsAux,padInventory:true);
                    await SendSnapshot(packets);
                    await hub.BroadcastVisible(updated,GamePackets.Human(updated),except:updated.Id);
                }
                if(broken)await Send(GamePackets.WeaponBrokenNotice());
                if(low)await Send(GamePackets.Message(stowed!=null?"무기 내구력이 10%에 도달해 장착을 해제했습니다."
                    :"무기 내구력이 10%에 도달했습니다. 소지품 창에 빈 칸이 없어 장착을 해제하지 못했습니다."));
            }
            async Task<(Character Hit,Character Combo)> Advance(int limit,ushort comboCounter=0,
                (ushort Hit,ushort Combo,ushort KillFame)? experience=null,FieldObject? defeated=null,bool gainHealth=false,
                ushort? fixedDollCounters=null,ushort weaponWear=1,ushort? recordedHitCounters=null){
                limit=Math.Min(limit,ShrineJob.CombatLimit(attacker.Job,ability));
                (ushort Hit,ushort Combo,ushort KillFame) awards=experience??(2,0,0);
                ushort previous=CombatAbilities.Mastery(attacker,ability);
                var (placements,stowed,low,broken)=WearWeapon(weaponWear);
                if(previous>=limit&&placements==null&&awards.KillFame==0&&defeated==null)return (attacker,attacker);
                ushort baseHitCounters=recordedHitCounters??abilityHitCount;
                ushort hitCounters=fixedDollCounters??presence!.Party?.TrainingCounters(presence,baseHitCounters,Random.Shared.Next(100))??baseHitCounters;
                if(!fixedDollCounters.HasValue&&GuardianQuest.CountersActive(attacker,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    hitCounters=(ushort)Math.Max(hitCounters,baseHitCounters*2);
                var (hit,combo,updated)=characters.RecordHit(attacker.Id,ability,limit,placements,comboCounter,awards,hitCounters,
                    defeated,defeated==null?null:DateTimeOffset.UtcNow,gainHealth);
                character=updated;
                presence!.Character=character;
                await SendGrowth(attacker,hit,sendStatus:stowed==null,fixedDoll:fixedDollCounters.HasValue);
                await SendWeaponWear(hit,stowed,low,broken);
                return (hit,combo);
            }
            if(opponent!=null){
                var victim=opponent.Character;
                ushort duelDamage=DamageAgainst(EquipmentItems.PhysicalDefense(EquipmentItems.EquippedEffects(victim)));
                var (placements,stowed,low,broken)=WearWeapon();
                try{
                    var result=characters.RecordDuelHit(attacker.Id,victim.Id,duelDamage,placements);
                    character=presence.Character=result.Attacker;
                    opponent.Character=result.Victim;
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
                    Console.Error.WriteLine($"Duel damage persistence: {ex.Message}");return;
                }
                await WorldHub.Notify(opponent,opponent.Character.AwaitingRevival
                    ?GamePackets.RevivalVitals(opponent.Character,opponent.CombatMode,includeMaxima:opponent.TrainingHealthStatusPending)
                    :GamePackets.PaddedStatus(opponent.Character,0x60,opponent.CombatMode));
                await hub.BroadcastVisible(victim,HitEffect(victim.Id,duelDamage));
                if(opponent.Character.AwaitingRevival){
                    await ShowDowned(opponent);
                    await hub.BroadcastVisible(opponent.Character,GamePackets.Human(opponent.Character),victim.Id);
                }
                if(opponent.Party is {} party)await party.UpdateHealth(opponent);
                await SendWeaponWear(character,stowed,low,broken);
                return;
            }
            if(target==null){
                if(doll!=null){
                    bool destroyed=false;
                    var placed=doll.Appearance;
                    if(doll.IsPermanent&&ability==CombatAbilities.All[0]){
                        bool beginner=CombatAbilities.UsesRecordedBeginnerDollProfile(attacker,ability);
                        var (displayed,counters)=beginner
                            ?CombatAbilities.FixedDollStrike(Random.Shared.Next(CombatAbilities.FixedDollStrikeSamples))
                            :CombatAbilities.ObservedTrainedDollDamage(attacker,ability,placed.Map) is ushort trainedDamage
                                ?(trainedDamage,(ushort)0)
                                :(DamageAgainst(0),Random.Shared.Next(3)==0?(ushort)2:(ushort)1);
                        await hub.BroadcastVisible(placed,HitEffect(placed.Id,displayed));
                        if(counters>0){
                            bool gainFixedHealth=displayed>0&&fixedDollHealthRoll>0&&Random.Shared.Next(fixedDollHealthRoll)==0;
                            await Advance(CombatAbilities.FixedDollTrainingLimit,experience:(0,0,0),gainHealth:gainFixedHealth,
                                fixedDollCounters:counters);
                        }
                    }else{
                        ushort dollDamage=DamageAgainst(0);
                        destroyed=doll.Hit(dollDamage);
                        await hub.BroadcastVisible(placed,HitEffect(placed.Id,dollDamage));
                        bool gainDollHealth=dollDamage>0&&fixedDollHealthRoll>0&&Random.Shared.Next(fixedDollHealthRoll)==0;
                        await Advance(doll.AbilityLimit,experience:doll.IsPermanent?(0,0,0):null,gainHealth:gainDollHealth);
                    }
                    if(destroyed){
                        world.RemoveTrainingDoll(doll);
                        var death=GamePackets.Death(placed.Id);
                        await hub.BroadcastVisible(placed,placed.Sprite==0x400C?[..death,0]:death);
                    }
                    await SendSelfProgress();
                }else if(guildNpc!=null){
                    // 26 of the 74 visible self strikes in the Alpha capture displayed positive damage.
                    ushort displayed=Random.Shared.Next(100)<35?DamageAgainst(0):(ushort)0;
                    await hub.BroadcastVisible(attacker,HitEffect(guildNpc.Id,displayed));
                    if(displayed>0)presence.ProvokeGuildNpc(Environment.TickCount64);
                }else if(hiredMerchant!=null){
                    ushort displayed=CombatAbilities.HiredMerchantDamage(DamageAgainst(0),Random.Shared.Next(100),
                        bastardSword,weakMerchantStrike);
                    var effect=HitEffect(hiredMerchant.Id,displayed);
                    foreach(var observer in hub.InMap(hiredMerchant.Map))
                        if(WorldHub.CanSee(observer.Character,hiredMerchant)||WorldHub.CanRefreshMerchant(observer.Character,hiredMerchant))
                            await WorldHub.Notify(observer,effect);
                    if(bastardSword&&displayed>0)
                        await Advance(0,experience:(0,0,0),weaponWear:EquipmentItems.BastardSwordMerchantWear);
                }else
                    await hub.BroadcastVisible(attacker,HitEffect(attacker.Id,0));
                return;
            }
            var recordedStrike=CombatAbilities.ObservedStoneGolemStrike(attacker,ability,target.Appearance);
            ushort damage=recordedStrike?.Damage??DamageAgainst(target.Defense);
            ushort fieldWear=EquipmentItems.FieldWeaponWear(weaponItem);
            if(target.IsTreasureChest&&!target.IsPirateChest&&!ability.UsesBow&&ability!=CombatAbilities.Harp)
                damage=TreasureChests.StationaryMeleeDamage(damage);
            long hitAt=Environment.TickCount64;
            target.Hit(damage,hitAt+target.RespawnDelayMs(respawnSeconds));
            var monster=target.Appearance;
            var loot=target.Hp==0?world.DropLoot(target):[];
            await hub.BroadcastVisible(monster,HitEffect(monster.Id,damage));
            if(target.IsTreasureChest){
                ushort killFame=target.Hp==0&&target.IsPirateChest?TreasureChests.PirateKillFame:(ushort)0;
                await Advance(0,experience:(0,0,killFame),weaponWear:fieldWear);
                if(target.Hp==0){
                    if(world.TryReleaseChestZombie(target,Random.Shared.Next(TreasureChests.ZombieChanceDenominator)) is {} zombie)
                        await hub.BroadcastVisible(zombie,GamePackets.Objects([],[zombie]));
                    foreach(var drop in loot)await hub.BroadcastVisible(drop,GamePackets.Objects([],[drop]));
                    await hub.BroadcastVisible(monster,GamePackets.Death(monster.Id));
                }else if(target.IsPirateChest)target.Provoke(presence!,hitAt);
                return;
            }
            int mastery=CombatAbilities.Mastery(attacker,ability);
            int skillLevel=mastery/100;
            int monsterLevel=target.Level;
            ushort comboSeconds=WorldSession.ComboWindow(monsterLevel,skillLevel);
            bool canCombo=monsterLevel>0&&mastery<=target.TrainingLimit;
            byte combo=target.Hp==0&&canCombo?presence!.RegisterKill((monster.Sprite,monster.Name),comboSeconds,hitAt):(byte)0;
            // Use a local minimum for monsters below the first observed positive bonus tier.
            ushort bonus=combo>1&&monsterLevel>0?(ushort)(combo*Math.Max(1,monsterLevel/5)):(ushort)0;
            ushort counterBonus=bonus==0?(ushort)0:CombatAbilities.ComboCounterAward(attacker,bonus,Random.Shared.Next(100));
            (ushort Hit,ushort Combo,ushort KillFame) experience=world.MonsterExperience(monster) is ushort recordedFame
                ?(0,0,target.Hp==0?recordedFame:(ushort)0):(2,bonus,0);
            bool gainHealth=damage>0&&trainingHealthRoll>0&&Random.Shared.Next(trainingHealthRoll)==0;
            ushort? recordedCounters=recordedStrike?.Counters;
            if(overrideAbilityHitCount&&recordedCounters>0)recordedCounters=abilityHitCount;
            var (hit,afterCombo)=await Advance(target.TrainingLimit,counterBonus,experience,target.Hp==0?monster:null,
                gainHealth,weaponWear:fieldWear,recordedHitCounters:recordedCounters);
            if(target.Hp==0){
                await PublishMonsterDefeat(target,loot);
                if(canCombo){
                    await SendGrowth(hit,afterCombo,comboAward:true);
                    uint earnedBonus=CombatAbilities.CountersAdded(hit,afterCombo,ability);
                    await Send(GamePackets.Combo(combo,(ushort)Math.Min(bonus,earnedBonus),(uint)skillLevel,(uint)monsterLevel,comboSeconds));
                    // The combo ends with S85. The next ordinary base status carries the saved Fame.
                }else if(CombatAbilities.Experience(character!)!=CombatAbilities.Experience(afterCombo)||
                    CombatAbilities.Fame(character)!=CombatAbilities.Fame(afterCombo))
                    await Send(GamePackets.Status(character,mask:0x40,state:presence.CombatMode));
            }else target.Provoke(presence!,hitAt);
            await SendSelfProgress();
        }
        async Task BurnInventoryItem(byte slot,uint quantity){
            if(character==null||presence==null||slot==0||slot==character.WalletSlot||character.Items is not {} items)return;
            var source=items.FirstOrDefault(item=>item.InventorySlot==slot);
            if(source==null)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환을 끝낸 뒤 아이템을 소각해 주세요."));return;}
            if(quantity==0||quantity>source.Quantity){await Send(GamePackets.Message("소각할 수량을 확인해 주세요."));return;}
            var item=EquipmentItems.Get(source.Key);
            ushort maximum=EquipmentItems.MaximumDurability(item);
            // Single-item burns in 20261108-113547 and 20261109-032625.
            (uint credit,bool compactFirstRemoval,bool padOldWallet,bool padNewWallet,bool baseStatus) profile=source.Quantity==1
                ?(item.Sprite,item.Name) switch {
                    (0x82C0,"시미터") when EquipmentItems.CurrentDurability(source,maximum)==maximum=>(22u,false,false,true,true),
                    (0x8F81,"개인방팻말(Lv1)")=>(1u,true,false,false,false),
                    (0x8C56,"미용지침서(2-23)")=>(1u,false,true,true,false),
                    (0x8CD5,"모래시계(중급)")=>(1u,false,true,true,false),
                    (0x8CD4,"모래시계(고급)")=>(2u,true,false,false,false),
                    (0x8010,"수선세트")=>(13u,false,true,false,false),
                    _=>default
                }:default;
            uint credit=profile.credit;
            if(!WorldSettings.CanCreditGold(character.Gold,credit)){await Send(GamePackets.Message(WorldSettings.GoldLimitMessage));return;}
            var remaining=source with {Quantity=source.Quantity-quantity};
            var next=remaining.Quantity==0?items.Where(item=>item!=source).ToArray():
                items.Select(item=>item==source?remaining:item).ToArray();
            var previous=character;
            try{character=characters.SetItems(character.Id,next,gold:character.Gold+credit,
                walletPosition:credit==0?null:NewBagPosition(previous.WalletX,previous.WalletY));}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} inventory burn save failed: {ex.Message}");
                await Send(GamePackets.Message("아이템 소각을 저장하지 못했습니다."));return;
            }
            presence.Character=character;
            var reply=new List<byte[]>();
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(remaining.Quantity==0){
                reply.Add(GamePackets.InventoryBurn(slot,carriedWeight,padded:!profile.compactFirstRemoval));
                reply.Add(GamePackets.InventoryBurn(slot,carriedWeight));
            }else reply.Add(GamePackets.InventoryAdd(remaining,carriedWeight));
            if(credit!=0){
                var initialWallet=character with {WalletX=previous.WalletX,WalletY=previous.WalletY};
                reply.Add(GamePackets.Wallet(initialWallet,carriedWeight,padded:profile.padOldWallet));
                reply.Add(GamePackets.Wallet(character,carriedWeight,padded:profile.padNewWallet));
                reply.Add(profile.baseStatus?GamePackets.PaddedStatus(character,0x50,presence.CombatMode):
                    GamePackets.Gold(character.Gold,presence.CombatMode));
            }
            reply.Add(GamePackets.Message(credit!=0?$"아이템을 소각하고 {credit}갈리드를 받았습니다.":"아이템을 소각했습니다."));
            await SendSnapshot(reply);
        }
        async Task DropItem(byte slot,int x,int y,uint quantity){
            if(character==null||presence==null||slot==0)return;
            bool wallet=slot==character.WalletSlot;
            var source=character.Items?.FirstOrDefault(i=>i.InventorySlot==slot);
            uint available=wallet?character.Gold:source?.Quantity??0;
            if(available==0)return;
            if(presence.Trade!=null){await Send(GamePackets.Message("교환이 끝난 뒤 아이템을 내려놓을 수 있습니다."));return;}
            if(Math.Abs(character.X-x)>2||Math.Abs(character.Y-y)>2||!world.GetMap(character.Map).CanStand(x,y,character.Floor)){
                await Send(GamePackets.Message("그 위치에 아이템을 내려놓을 수 없습니다."));
                return;
            }
            if(quantity==0)quantity=available;
            if(quantity>available){await Send(GamePackets.Message("내려놓을 수량을 확인해 주세요."));return;}
            var item=new GroundItem(world.NextDropId(),character.Map,x,y,character.Floor,
                wallet?WorldSettings.CurrencyKey:source!.Key,quantity,source?.Durability,source?.BaitRemaining){
                ExpiresAt=source?.ExpiresAt,NativeDurability=source?.NativeDurability};
            try{character=wallet?characters.PutGoldOnGround(character.Id,item):characters.PutOnGround(character.Id,source!,item);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} item drop save failed: {ex.Message}");
                await Send(GamePackets.Message("아이템을 바닥에 내려놓지 못했습니다."));
                return;
            }
            presence.Character=character;
            var drop=world.AddDrop(item,character.Id);
            await hub.BroadcastVisible(drop,GamePackets.Objects([],[drop]));
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(quantity==available)await Send(GamePackets.InventoryDrop(slot,carriedWeight));
            else await Send(wallet?GamePackets.Wallet(character,carriedWeight):GamePackets.InventoryAdd(source! with {Quantity=available-quantity},carriedWeight));
            if(wallet)await Send(GamePackets.Gold(character.Gold));
        }
        async Task PickUp(int x,int y,ushort word1,ushort word2){
            if(character==null||presence==null||Math.Abs(character.X-x)>2||Math.Abs(character.Y-y)>2)return;
            var drop=world.DropAt(character.Map,x,y,character.Floor);
            if(drop?.ItemKey is not string key)return;
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if(drop.ExpiresAt is long deadline&&deadline<=now){await RemoveExpiredDrop(drop,now);return;}
            var items=character.Items??[];
            uint oldGold=character.Gold;
            uint? balance=null;
            ItemPlacement? placement=null;
            var item=key==WorldSettings.CurrencyKey?null:EquipmentItems.Get(key);
            // In retail C07 captures, flag 1 items were assigned a new bag position and received an extra S0F.
            bool reposition=item?.InventoryFlag1==1;
            if(item==null){
                if(!WorldSettings.CanCreditGold(oldGold,drop.Quantity)){await Send(GamePackets.Message(WorldSettings.GoldLimitMessage));return;}
                balance=oldGold+drop.Quantity;
            }else{
                byte free=character.FreeInventorySlots(items).FirstOrDefault();
                if(free==0){await ReturnPickupToGround(drop);return;}
                var (bagX,bagY)=reposition?NewBagPosition(word1,word2):(word1,word2);
                placement=new(key,free,drop.Quantity,bagX,bagY,drop.Durability,drop.BaitRemaining){
                    NativeDurability=drop.NativeDurability,
                    ExpiresAt=drop.ExpiresAt??ElBlessing.NewItemDeadline(key,now)};
            }
            try{
                character=drop.DropOwnerId==0?characters.SetItems(character.Id,placement==null?items:[..items,placement],gold:balance):
                    characters.TakeFromGround(character.Id,drop.DropOwnerId,drop.Id,placement,balance);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} item pickup save failed: {ex.Message}");
                await Send(GamePackets.Message("아이템을 주울 수 없습니다."));
                return;
            }
            presence.Character=character;
            world.RemoveDrop(drop.Id);
            var pickup=GamePackets.Pickup(drop.Id,character.Id);
            var removal=GamePackets.Remove(drop.Id);
            var padding=item==null?default:GamePackets.PickupPadding(item);
            await hub.BroadcastVisible(drop,padding.World?[..pickup,0]:pickup);
            await hub.BroadcastVisible(drop,padding.World?[..removal,0]:removal);
            ushort carriedWeight=EquipmentItems.CarriedWeight(character);
            if(placement!=null){
                inventoryPositions.TryGetValue(placement.InventorySlot,out var oldPosition);
                var initial=placement with {InventoryWord1=oldPosition.X,InventoryWord2=oldPosition.Y};
                var initialPacket=GamePackets.InventoryAdd(initial,carriedWeight);
                var positionedPacket=GamePackets.InventoryAdd(placement,carriedWeight);
                if(padding.Initial)initialPacket=[..initialPacket,0];
                if(padding.Positioned)positionedPacket=[..positionedPacket,0];
                await SendSnapshot(reposition?[initialPacket,positionedPacket,positionedPacket]:[initialPacket,positionedPacket]);
            }else{
                await SendWalletUpdate(character,oldGold,carriedWeight);
                await Send(GamePackets.Gold(character.Gold));
            }
        }
        async Task ReturnPickupToGround(FieldObject drop){
            var picker=character!;
            var returned=drop with {X=picker.X,Y=picker.Y};
            if(drop.DropOwnerId!=0&&(drop.X!=returned.X||drop.Y!=returned.Y)){
                try{
                    var updated=characters.RelocateGroundDrop(drop.DropOwnerId,drop.Id,returned.X,returned.Y);
                    if(updated.Id==picker.Id)character=updated;
                    if(hub.Attached(updated.Id) is {} owner)owner.Character=updated;
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException){
                    Console.Error.WriteLine($"{role}/{id} item pickup return save failed: {ex.Message}");
                    await Send(GamePackets.Message("아이템을 주울 수 없습니다."));
                    return;
                }
            }
            world.UpdateDrop(returned);
            await hub.BroadcastVisible(drop,[..GamePackets.Pickup(drop.Id,picker.Id),0]);
            await hub.BroadcastVisible(drop,[..GamePackets.Remove(drop.Id),0]);
            await Send([..GamePackets.Message("소지품이 가득 찼습니다."),0]);
            await hub.BroadcastVisible(returned,[..GamePackets.Objects([],[returned]),0]);
        }
        Character? SavePosition(Character current,int x,int y,byte direction,int? map=null,byte? floor=null){
            try{return characters.UpdatePosition(current.Id,x,y,direction,map,floor);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"{role}/{id} character {current.Id} position save failed ({ex.GetType().Name}): {ex.Message}");
                return null;
            }
        }
        async Task Command(string text){
            if(character==null||presence==null)return;
            var words=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(words.Length==0)return;
            switch(words[0].ToLowerInvariant()){
                case "/speed":
                    var culture=System.Globalization.CultureInfo.InvariantCulture;
                    if(words.Length==2&&decimal.TryParse(words[1],System.Globalization.NumberStyles.AllowDecimalPoint,culture,out decimal speed)
                        &&speed is >=0.5m and <=3m){
                        character=characters.SetMovementSpeed(character.Id,speed);
                        presence.Character=character;
                    }else if(words.Length!=1){await Send(GamePackets.Message("/speed 0.5~3"));break;}
                    await Send(GamePackets.Message($"이동 배속 {(character.MovementSpeed??1m).ToString("0.##",culture)}배"));
                    break;
                case "/1:1":
                    if(words.Length!=2||!CharacterStore.ValidName(words[1])){
                        await Send(GamePackets.Message("/1:1 캐릭터이름"));break;
                    }
                    if(hub.FindOnline(words[1]) is {} chatTarget)
                        await Send(GamePackets.WhisperOpen(chatTarget.Character.Name));
                    else await Send(GamePackets.Message("접속 중인 캐릭터가 없습니다."));
                    break;
                case "/where":await Send(GamePackets.Message($"맵 {WorldSettings.ClientMap(character.Map)}, 좌표 {character.X}, {character.Y}, 층 {character.Floor}"));break;
                case "/craft":await OpenCraft();break;
                case "/refresh":await RefreshWorld();break;
                case "/heal":
                    character=characters.SetHealth(character.Id,0);presence.Character=character;
                    await Send(GamePackets.Status(character));await Send(GamePackets.Message("체력을 모두 회복했습니다."));break;
                case "/look":
                    if(words.Length!=2){await Send(GamePackets.Message("/look reference | recent | reset"));break;}
                    AppearanceProfile? profile=null;
                    if(words[1]!="reset"&&!appearanceProfiles.TryGetValue(words[1],out profile)){await Send(GamePackets.Message("해당 비교용 외형이 없습니다."));break;}
                    character=characters.SetLook(character.Id,profile);presence.Character=character;
                    await hub.BroadcastVisible(character,GamePackets.Human(character));await Send(GamePackets.Message("외형을 갱신했습니다. 생성 당시 외형: /look reset"));break;
                case "/go":
                    if(words.Length is not (2 or 5)||!int.TryParse(words[1],out int mapId)||mapId<0||mapId>65535){await Send(GamePackets.Message("/go 맵번호 [X Y 층]"));break;}
                    if(await BlockRegionTravel(mapId))break;
                    LocalMap map;
                    try{map=world.GetMap(mapId);}catch(Exception ex) when(ex is IOException or InvalidDataException){await Send(GamePackets.Message("해당 맵 파일을 읽을 수 없습니다."));break;}
                    int x,y;byte floor;
                    if(words.Length==5){
                        if(!int.TryParse(words[2],out x)||!int.TryParse(words[3],out y)||!byte.TryParse(words[4],out floor)){await Send(GamePackets.Message("좌표와 층은 숫자로 입력하세요."));break;}
                    }else{
                        var spawn=map.FindSpawn((sx,sy,sf)=>!world.IsOccupied(mapId,sx,sy,sf)&&!hub.IsOccupied(mapId,sx,sy,sf,character.Id));if(spawn==null){await Send(GamePackets.Message("확인된 평면 출발 지점이 없습니다."));break;}(x,y,floor)=spawn.Value;
                    }
                    if(!map.CanWalk(x,y,floor)||world.IsOccupied(mapId,x,y,floor)||hub.IsOccupied(mapId,x,y,floor,character.Id)){await Send(GamePackets.Message("이동할 수 없는 위치입니다."));break;}
                    if(SavePosition(character,x,y,character.Direction,mapId,floor) is {} warped)await EnterMap(warped);
                    else await Send(GamePackets.Message("위치를 저장하지 못했습니다. 다시 시도해 주세요."));
                    break;
                default:await Send(GamePackets.Message("로컬 명령: /where /refresh /heal /craft /go 맵번호 [X Y 층] /look reference|recent|reset"));break;
            }
        }
        // Explicit opt-in, local developer packet injection for protocol investigation.
        async Task Control() {
            if(!args.Contains("--dev-control"))return;
            string path=Path.Combine("logs","control",id+".json");
            try {while(!ct.IsCancellationRequested) {
                await Task.Delay(200,ct);
                if(!File.Exists(path))continue;
                string data=await File.ReadAllTextAsync(path,ct);File.Delete(path);
                foreach(string hex in JsonSerializer.Deserialize<string[]>(data)??[])await Send(Convert.FromHexString(hex));
            }}catch(Exception ex) when(ex is IOException or OperationCanceledException or FormatException or JsonException or SocketException){Console.WriteLine("Dev control: "+ex.Message);}
        }
        Task? control=null;
        try {
            if(role!="World")await stream.WriteAsync(Framing.Wrap(Encoding.ASCII.GetBytes("~\u001bCONNECTED SERVER\n")),ct);
            control=Control();
            while(await Framing.ReadAsync(stream,ct) is {} body) {
                if(!authenticated)timeout.CancelAfter(unauthenticatedTimeout);
                var p=crypto.Decode(body,false);Log("C",p);
                bool worldPacket=role=="World";
                // Keep a move's coordinates, acknowledgement and visibility changes in order
                // relative to movements and map changes from other sessions.
                if(worldPacket)await worldUpdateLock.WaitAsync(ct);
                try{
                if(transferPending)continue;
                if(presence!=null){
                    if(!await UpdateTimedItems(presence,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))continue;
                    character=presence.Character;
                }
                if(presence!=null&&character is {AwaitingRevival:true}&&p[0] is not (0x0B or 0x0C or 0x1B or 0x2D or 0x35 or 0x36 or 0x42 or 0x45 or 0x71)){
                    if(p[0]==0x33)await Send(GamePackets.Move(8,character.X,character.Y,1,character.Floor));
                    continue;
                }
                switch(p[0]) {
                    case 0:
                        // SVersionCheckPacket: reply=0, uint32, seed, key length, key.
                        await Send(new PacketWriter(0).U8(0).U32(0).U8(0).Str8("NexonInc.").Build());break;
                    case 0x57:
                        await Send(GamePackets.ServerList(loginEndpoint,secure!=null));break;
                    case 0x7B when p.Length==2 && p[1]==1:
                        await Send(metadata.List());break;
                    case 0x7B when p.Length>2 && p[1]==0:
                        var requested=PacketReader.Name(p[1..]);var metaPacket=metadata.FilePacket(requested);
                        if(metaPacket!=null)await Send(metaPacket);break;
                    case 0x62:break; // Native initial 'baram' probe, before CVersion.
                    case 4:
                        if(role=="World")return;
#if ELANCIA_RELEASE_BUNDLE
                        authenticated=secure!=null;
#else
                        authenticated=secure!=null||LoginPacket.IsTestAccount(p);
#endif
                        timeout.CancelAfter(authenticated?Timeout.InfiniteTimeSpan:unauthenticatedTimeout);
                        if(!authenticated){await Send(GamePackets.Check(false,"Invalid local account"));break;}
                        await Send(GamePackets.CharacterList(characters.List(account)));
                        break;
                    case 0x8D when authenticated && role!="World":
                        var selection=PacketReader.Selection(p);
                        if(selection.Domain!=0){await Send(GamePackets.Check(false,"Invalid domain"));break;}
                        if(selection.Name.Length==0){creating=true;pendingName=null;await Send(GamePackets.CheckCode(1));break;}
                        creating=false;pendingName=null;
                        character=characters.Find(account,selection.Name);
                        if(character==null){await Send(GamePackets.Check(false,"Character not found"));break;}
                        await Send(GamePackets.Check(true));
                        await Send(GamePackets.Transfer(worldEndpoint,NewTransferTicket(character.Id)));
                        break;
                    case 2 when authenticated && creating && role!="World":
                        pendingName=null;
                        var name=PacketReader.Name(p);
                        if(!characters.Available(name)){await Send(GamePackets.CheckCode(4,"Name unavailable"));break;}
                        pendingName=name;await Send(GamePackets.Check(true));break;
                    case 3 when authenticated && creating && pendingName!=null && role!="World":
                        if(p.Length!=13){await Send(GamePackets.CheckCode(4,"Invalid appearance"));break;}
                        character=characters.Create(account,pendingName,p[1..]);
                        if(character==null){await Send(GamePackets.CheckCode(4,"Name unavailable or character limit"));break;}
                        creating=false;pendingName=null;
                        await Send(GamePackets.Check(true));
                        await Send(GamePackets.CharacterList(characters.List(account)));
                        break;
                    case 0x10:
                        if(role is not ("World" or "Login")||authenticated)return;
                        WorldHandoff? incomingHandoff=null;
                        string transferKey=Convert.ToHexString(p.AsSpan(1));
                        if(p.Length==17&&tickets.TryGetValue(transferKey,out var ticketData)&&ticketData.Account==account&&
                            (ticketData.CharacterId.HasValue==(role=="World"))&&ticketData.Expires>DateTimeOffset.UtcNow&&tickets.TryRemove(transferKey,out _)){
                            if(ticketData.CharacterId is {} selectedId){
                                character=characters.Find(selectedId);
                                authenticated=character?.Account==account;
                                incomingHandoff=ticketData.Handoff;
                            }else{
                                authenticated=true;
                                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                                await Send(new PacketWriter(0x60).U8(0).U32(0).Build());
                                break;
                            }
                        }
                        if(!authenticated){await Send(GamePackets.Check(false,"Expired transfer"));return;}
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        var attached=hub.Attached(character!.Id);
                        if(incomingHandoff!=null){
                            if(attached!=incomingHandoff.Session)return;
                        }else if(attached?.SupersedeConnection is {} supersede){
                            await supersede();
                        }
                        if(incomingHandoff==null)character=characters.RestoreEquipmentAppearance(character.Id);
                        long buffNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        character=characters.UpdateTimedItems(character.Id,buffNow).Character;
                        if(character!.SpeedPotionUntil>0||character.HealthPotionUntil>0||character.ElBlessingUntil>0)
                            character=characters.ExpirePotionBuffs(character.Id,buffNow);
                        if(character.SlowUntil>0||character.CurseUntil>0)
                            character=characters.ExpireDebuffs(character.Id,buffNow);
                        bool returnedFromUnreleased=!CanEnterRegion(character.Map);
                        if(returnedFromUnreleased){
                            var openTown=StartLocation.Rolancia with {Map=character.Map/10000*10000+2};
                            var returnAt=RevivalArrival(openTown,character.Id);
                            character=characters.UpdatePosition(character.Id,returnAt.X,returnAt.Y,character.Direction,returnAt.Map,returnAt.Floor);
                        }
                        var startingMap=world.GetMap(character.Map);
                        if((!startingMap.CanStand(character.X,character.Y,character.Floor)&&!startingMap.CanSwim(character.X,character.Y,character.Floor))||
                            world.IsOccupied(character.Map,character.X,character.Y,character.Floor)||hub.IsOccupied(character.Map,character.X,character.Y,character.Floor,character.Id)){
                            var spawn=startingMap.FindSpawn((sx,sy,sf)=>!world.IsOccupied(character.Map,sx,sy,sf)&&!hub.IsOccupied(character.Map,sx,sy,sf,character.Id));if(spawn==null)throw new InvalidDataException("Map has no safe spawn");
                            character=characters.UpdatePosition(character.Id,spawn.Value.X,spawn.Value.Y,character.Direction,floor:spawn.Value.Floor);
                        }
                        if(incomingHandoff is {} handoff){
                            presence=handoff.Session;
                            presence.Character=character;
                            presence.Reconnect(Send,FinishRevival,SendSnapshot);
                            presence.SupersedeConnection=SupersedeWorld;
                            handoff.Completion.SetResult();
                        }else{
                            presence=new WorldSession(character,Send,SendSnapshot){PoisonShown=character.DaggerPoisonActive(),
                                FinishRevival=FinishRevival,SupersedeConnection=SupersedeWorld};
                            if(!hub.Enter(presence)){presence=null;await Send(GamePackets.Check(false,"Character already online"));return;}
                        }
                        presence.MovePersonalRoom=MovePersonalRoom;
                        await Snapshot(initialLogin:true,handoff:incomingHandoff);
                        if(!character.AwaitingRevival&&character.ElBlessingUntil>buffNow)
                            await Send(GamePackets.TimedSpell(ElBlessing.Timer(ElBlessing.Remaining(character,buffNow))));
                        if(returnedFromUnreleased)await Send(GamePackets.Message(WorldSettings.UnreleasedRegionMessage));
                        if(character.AwaitingRevival)await ShowDowned(presence,appearanceSent:true);
                        presence.Ready=true;
                        await hub.BroadcastAppearance(presence);
                        await Send(FriendsPacket(character,padded:statusDimension==1));
                        if(TryAwardOnlineFame(presence,Environment.TickCount64))await SendOnlineFame(presence);
                        character=presence.Character;
                        if(incomingHandoff!=null&&presence.Party is {} restoredParty)await restoredParty.SendRoster(presence);
                        await UpdateFriendPresence(character.Id);
                        if(character.DaggerPoisonActive()){
                            ushort seconds=(ushort)Math.Clamp((character.DaggerPoisonUntil-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+999)/1000,1,ushort.MaxValue);
                            await Send(GamePackets.TimedSpell(EquipmentItems.DaggerPoisonTimer with {Duration=seconds}));
                        }
                        if(character.SpeedPotionUntil>0){
                            ushort remaining=character.SpeedPotionUntil-buffNow>PotionBuffs.DurationMs?
                                GuardianQuest.Remaining(character.SpeedPotionUntil,buffNow):PotionBuffs.Remaining(character.SpeedPotionUntil,buffNow);
                            await Send(GamePackets.TimedSpell(PotionBuffs.Timer(PotionBuff.Speed,remaining)));
                            await Send(GamePackets.PotionBuffStatus(character));
                        }
                        if(character.HealthPotionUntil>0)
                            await Send(GamePackets.TimedSpell(PotionBuffs.Timer(PotionBuff.Health,PotionBuffs.Remaining(character.HealthPotionUntil,buffNow))));
                        if(GuardianQuest.StrengthActive(character,buffNow))
                            await Send(GamePackets.TimedSpell(GuardianQuest.Timer(GuardianReward.Strength,
                                GuardianQuest.Remaining(character.Guardian!.StrengthUntil,buffNow))));
                        if(GuardianQuest.CountersActive(character,buffNow))
                            await Send(GamePackets.TimedSpell(GuardianQuest.Timer(GuardianReward.Counters,
                                GuardianQuest.Remaining(character.Guardian!.CounterUntil,buffNow))));
                        Console.WriteLine($"{role}/{id} character {character.Id} entered map {character.Map}");
                        break;
                    case 0x45 when role=="World"&&presence!=null:
                        if(p.Length==3&&presence.CheckCrcReply(new PacketReader(p.AsSpan(1)).U16())==false)
                            Console.WriteLine($"{role}/{id} world CRC reply did not match the pending request");
                        break;
                    case 0x42:break; // Client diagnostic report; content redacted.
                    case 0x71:break; // Native ping/keepalive.
                    case 0x15 when authenticated && role!="World":creating=false;pendingName=null;await Send(GamePackets.CharacterList(characters.List(account)));break;
                    case 0x1B when role=="World" && authenticated && character!=null:
                        if(p.AsSpan().SequenceEqual(new byte[]{0x1B,1,0})&&presence is {Ready:true}){
                            try{character=characters.ToggleStatusEffectBit0(character.Id);}
                            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                                Console.Error.WriteLine($"Status option save: {ex.Message}");
                                await Send(GamePackets.Message("상태 옵션을 저장하지 못했습니다."));
                                break;
                            }
                            presence.Character=character;
                            await Send(GamePackets.StatusOptionReply(character,character.AwaitingRevival?(byte)2:presence.CombatMode));
                        }else if(p.Length==3)await Send(GamePackets.Status(character,state:character.AwaitingRevival?(byte)0x20:null,
                            vitalsExtra:presence?.VitalsAux??0));
                        break;
                    case 0x07 when role=="World" && authenticated:
                        if(p.Length==10&&p[1]==0){
                            var pickup=new PacketReader(p.AsSpan(2));
                            await PickUp(pickup.U16(),pickup.U16(),pickup.U16(),pickup.U16());
                        }
                        break;
                    case 0x08 when role=="World" && authenticated:
                        if(p.Length==10){
                            var drop=new PacketReader(p.AsSpan(1));
                            await DropItem(drop.U8(),drop.U16(),drop.U16(),drop.U32());
                        }
                        break;
                    case 0x6D when role=="World"&&authenticated&&p.Length==6:
                        var burn=new PacketReader(p.AsSpan(1));
                        await BurnInventoryItem(burn.U8(),burn.U32());
                        break;
                    case 0x0C when role=="World" && authenticated && character!=null && presence is {Ready:true} refreshObserver:
                        if(p.Length!=5)break;
                        uint missingId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1));
                        if(hub.FindOnline(missingId) is {} missing){
                            var target=missing.Character;
                            bool visible=WorldHub.CanSee(character,target);
                            if(visible||WorldHub.CanRefreshPeer(character,target)){
                                if(!visible)refreshObserver.RefreshedObjects.TryAdd(missingId,0);
                                await Send(GamePackets.Human(target));
                            }
                        }else if(hub.InMap(character.Map).Select(owner=>owner.Leon)
                            .FirstOrDefault(leon=>leon?.Id==missingId) is {} missingLeon){
                            if(WorldHub.CanSee(character,missingLeon))await Send(GamePackets.Human(missingLeon));
                        }else if(world.InMap(character.Map).FirstOrDefault(n=>n.Id==missingId) is {} missingNpc){
                            bool visible=WorldHub.CanSee(character,missingNpc);
                            if(visible||WorldHub.CanRefreshMerchant(character,missingNpc,includeDistant:true)){
                                if(!visible)refreshObserver.RefreshedObjects.TryAdd(missingId,0);
                                var appearance=GamePackets.Objects([missingNpc]);
                                bool distantHire=missingNpc.Sprite==0x40C1&&(missingNpc.Y-character.Y>10||
                                    WorldHub.CanRefreshDistantHiredMerchant(character,missingNpc));
                                if(WorldHub.CanRefreshBankHiredMerchant(character,missingNpc)){
                                    // The recorded bank burst returns a padded row, then a bare row for the duplicate.
                                    long now=Environment.TickCount64;
                                    distantHire=lastBankMerchantRefresh is not {} last||
                                        (last.Id,last.X,last.Y)!=(missingId,character.X,character.Y)||now-last.At>100;
                                    lastBankMerchantRefresh=(missingId,character.X,character.Y,now);
                                }
                                await Send(distantHire?[..appearance,0]:appearance);
                            }
                        }else if(world.FieldObjects(character.Map).FirstOrDefault(o=>o.Id==missingId) is {} field&&
                            WorldHub.TrackFieldRefresh(refreshObserver,field)){
                            var appearance=GamePackets.Objects([],[field]);
                            bool restoredSign=field.Category==6&&!WorldHub.CanSee(character,field);
                            await Send(restoredSign?[..appearance,0]:appearance);
                        }
                        break;
                    case 0x38 when role=="World" && authenticated && character!=null:
                        if(p.Length==1)await RefreshWorld();break;
                    case 0x35 when role=="World" && authenticated && character!=null:
                        if(p.Length!=5||presence==null)break;
                        var receivedMap=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1));
                        if(receivedMap!=WorldSettings.ClientMap(character.Map)||!presence.HasPendingMapCheck(character.Map))break;
                        var receivedChecksum=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3));
                        var expectedChecksum=world.GetMap(character.Map).ClientChecksum;
                        if(receivedChecksum!=expectedChecksum){
                            Console.WriteLine($"{role}/{id} map {receivedMap} checksum mismatch ({receivedChecksum:X4} != {expectedChecksum:X4})");
                            await Send(GamePackets.Message("로컬 맵 파일의 체크섬이 다릅니다."));
                            break;
                        }
                        if(presence.TakeMapCheck(character.Map) is not {} mapCheck)break;
                        if(mapCheck.Entry){
                            byte state=character.AwaitingRevival?(byte)2:presence.CombatMode;
                            byte[] status=statusDimension==0?[8,0,state]:GamePackets.Status(character,0x40,state);
                            await SendSnapshot([mapCheck.RoomExit&&statusDimension==0?[..status,0]:status,status,GamePackets.MapCheck(2)]);
                        }else await Send(GamePackets.MapCheck(2));
                        break;
                    case 0x56 when role=="World"&&authenticated&&
                        character!=null&&LocalSpell.All.Any(spell=>spell.IsKnown(character))&&presence is {Ready:true}:
                        if(p.Length==2&&p[1]<=1&&castingSpell!=(p[1]==1)){
                            castingSpell=p[1]==1;
                            await hub.BroadcastVisible(character,GamePackets.CastSpell(character.Id,castingSpell));
                        }
                        break;
                    case 0x0F when role=="World"&&authenticated&&character!=null:
                        if(p.Length>=2&&LocalSpell.All.FirstOrDefault(entry=>entry.IsKnown(character)&&entry.WireSlot(character)==p[1]) is {} requestedSpell){
                            if(requestedSpell.TargetMode==5){
                                if(p.Length==2||p.Length==3&&p[2]==0)await CastConversionSpell(character,requestedSpell);
                            }else if(p.Length==7)
                                await CastTargetedSpell(character,requestedSpell,System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2)));
                        }
                        break;
                    case 0x1C when role=="World" && authenticated:
                        if(p.Length==2)await UseItem(p[1]);
                        break;
                    case 0x30 when role=="World" && authenticated:
                        // Native inventory drag: mode 4, source slot, zero, window X/Y.
                        if(p.Length==8&&p[1]==4&&p[3]==0){
                            var position=new PacketReader(p.AsSpan(4));
                            await MoveInventoryItem(p[2],position.U16(),position.U16());
                        }
                        break;
                    case 0x58 when role=="World" && authenticated:
                        if(p.Length==11){
                            var bundle=new PacketReader(p.AsSpan(1));
                            await MoveInventoryBundle(bundle.U8(),bundle.U8(),bundle.U16(),bundle.U16(),bundle.U32());
                        }
                        break;
                    case 0x1E when role=="World" && authenticated:
                        if(p.Length==2)await ChangeEquipment(p[1],true,direct:true);
                        break;
                    case 0x44 when role=="World" && authenticated:
                        if(p.Length==6){
                            var drop=new PacketReader(p.AsSpan(2));
                            await ChangeEquipment(p[1],false,(drop.U16(),drop.U16()));
                        }
                        break;
                    case 0x19 or 0x65 when role=="World" && authenticated && character!=null:
                        var tell=new PacketReader(p.AsSpan(1));
                        bool privateWindow=p[0]==0x65;
                        // C65 1 closes the composition window and C65 3 closes a received message.
                        if(privateWindow&&tell.U8() is not (0 or 2))break;
                        string recipient=tell.Str8();
                        if(!CharacterStore.ValidName(recipient))break;
                        string privateText=privateWindow?tell.Str16():tell.Str8();
                        tell.End();
                        if(string.IsNullOrWhiteSpace(privateText)||privateText.Contains('\0')||privateWindow&&privateText.Length>511)break;
                        if(hub.FindOnline(recipient) is not {} recipientSession){
                            await Send(GamePackets.Message("접속 중인 캐릭터가 없습니다."));
                            break;
                        }
                        if(privateWindow){
                            await WorldHub.Notify(recipientSession,GamePackets.Whisper(character.Name,privateText));
                            if(recipientSession!=presence)
                                await Send(GamePackets.Message($"{recipientSession.Character.Name}님에게 1:1 채팅을 보냈습니다."));
                        }else{
                            await WorldHub.Notify(recipientSession,GamePackets.Tell(character.Name,privateText));
                            if(recipientSession!=presence)
                                await Send(GamePackets.Tell(recipientSession.Character.Name,privateText,outgoing:true));
                        }
                        break;
                    case 0x77 when role=="World"&&authenticated&&presence!=null&&character!=null:
                        if(p.Length<3)break;
                        if(p[1]==0){if(p.Length==3&&p[2]==0)await Send(FriendsPacket(character,padded:true));break;}
                        if(p[1] is not (1 or 2)||p[2]!=p.Length-3)break;
                        var friendReader=new PacketReader(p.AsSpan(2));
                        string friendName=friendReader.Str8();
                        if(!CharacterStore.ValidName(friendName)||characters.FindByName(friendName) is not {} friend||friend.Id==character.Id){
                            await Send(GamePackets.Message("친구로 등록할 수 없는 캐릭터입니다."));
                            break;
                        }
                        if(p[1]==1&&character.Friends is {Length:>=FriendLimit} registered&&!registered.Contains(friend.Id)){
                            await Send(GamePackets.Message("친구 목록이 가득 찼습니다."));
                            break;
                        }
                        character=characters.SetFriend(character.Id,friend.Id,p[1]==1);
                        presence.Character=character;
                        await Send(FriendsPacket(character,padded:true));
                        break;
                    case 0x2D when role=="World"&&authenticated&&character!=null:
                        if(p.AsSpan().SequenceEqual(new byte[]{0x2D,0})){
                            selfLookRequested=true;
                            await Send(GamePackets.SelfLook(character));
                        }
                        break;
                    case 0x2E when role=="World" && authenticated && presence!=null && character!=null:
                        if(p.Length==2&&p[1]==0){
                            try{presence.Character=character=characters.TogglePartyAllowed(character.Id);}
                            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                                Console.Error.WriteLine($"{role}/{id} party preference save failed: {ex.Message}");
                            }
                            await Send(GamePackets.PartyAllowed(character.PartyAllowed));
                        }else if(p.Length==6&&p[1]==1){
                            var requestParty=new PacketReader(p.AsSpan(2));
                            if(hub.FindVisible(character,requestParty.U32()) is {} invited)
                                await WorldParty.Join(presence,invited);
                            else await Send(GamePackets.Message("화면 안의 캐릭터에게 파티를 신청할 수 있습니다."));
                        }else if(p.Length==3&&p[1]==2&&presence.Party is {} party){
                            await party.Remove(presence,p[2]);
                        }
                        break;
                    case 0x0E when role=="World" && authenticated && character!=null && p is [0x0E,5,..]:
                        var privateChat=new PacketReader(p.AsSpan(2));
                        string chatName,chatText;
                        try{
                            _=privateChat.U8(); // Outgoing display color from the sender.
                            chatName=privateChat.Str8();
                            chatText=privateChat.Str8();
                            privateChat.End();
                        }catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException){break;}
                        if(!CharacterStore.ValidName(chatName)||string.IsNullOrWhiteSpace(chatText)||chatText.Contains('\0'))break;
                        if(hub.FindOnline(chatName) is not {} chatPeer){
                            await Send(GamePackets.Message("접속 중인 캐릭터가 없습니다."));
                            break;
                        }
                        // The native dialog appends the outgoing text locally.
                        if(chatPeer!=presence)
                            await WorldHub.Notify(chatPeer,GamePackets.PrivateChat(character.Name,chatText));
                        break;
                    case 0x0E when role=="World" && authenticated && character!=null:
                        if(p.Length<4||p[1]!=4)break;
                        var speech=PacketReader.Say(p);
                        if(speech.Text.StartsWith('/')){
                            if(emotionCommands.TryGetValue(speech.Text[1..].Trim(),out byte emotion)){
                                if(!character.AwaitingRevival)await hub.BroadcastVisible(character,GamePackets.Emotion(character.Id,emotion));
                            }else await Command(speech.Text);
                        }
                        else{
                            await hub.Broadcast(character.Map,GamePackets.Say(character,speech.Color,speech.Text));
                            string keyword=speech.Text.Trim();
                            if(keyword=="라니체"&&!character.AwaitingRevival&&world.InMap(character.Map)
                                .FirstOrDefault(npc=>RanicheQuest.IsRex(npc)&&CanTalkTo(character,npc,8)) is {} rex){
                                ClearNpcDialogs();
                                await ShowRaniche(rex);
                            }else if(keyword=="명성"&&FameExchangeDialog.InRange(character)){
                                ClearNpcDialogs();
                                fameDialog=new();
                                await Send(fameDialog.Packet(character));
                            }else if(ShrineDialog.InRange(character)){
                                if(keyword=="기도")await ShowShrinePage(ShrinePage.Prayer);
                                else if(keyword=="직업")await ShowShrinePage(ShrinePage.JobMenu);
                                else if(keyword=="성향포인트")await ShowShrinePage(ShrinePage.FateMenu);
                            }
                        }
                        break;
                    case 0x1D when role=="World"&&authenticated&&character!=null:
                        if(p.Length==2&&emotionMotions.Contains(p[1])&&!character.AwaitingRevival)
                            await hub.BroadcastVisible(character,GamePackets.Emotion(character.Id,p[1]));
                        break;
                    case 0x16 when role=="World" && authenticated && character!=null:
                        var request=PacketReader.Script(p);
                        // The native Ras confirmation serializes C16 with OID zero and one domain-adjusted map ID.
                        if(request.ObjectId==0&&request.Choices is [uint destinationId]){
                            var rasSlot=pendingRasSlot;pendingRasSlot=null;
                            if(rasSlot is {} fromSlot&&WorldSettings.RasDestination(destinationId,character.Map) is {} arrival&&
                                character.Items?.SingleOrDefault(i=>i.InventorySlot==fromSlot&&i.Key=="ras-feather"&&i.Quantity>0) is {} feather){
                                if(character.Map<0&&destinationId==2)arrival=new(2,127,155,1);
                                await UseFeather(feather,arrival);
                            }
                            break;
                        }
                        var npc=world.InMap(character.Map).SingleOrDefault(n=>n.Id==request.ObjectId);
                        if(npc!=null&&CanTalkTo(character,npc,6))await OpenNpc(npc,false);
                        break;
                    case 0x54 when role=="World"&&authenticated&&character is {AwaitingRevival:false}:
                        if(p.Length>=2&&p[1]==2){
                            if(presence is not {Ready:true})break;
                            if(p is [0x54,2,10]&&(presence.Leon!=null||presence.PendingLeonArrival!=null)){
                                var closingLeon=presence.Leon;
                                presence.LeonDismissed=true;
                                await hub.RemoveLeon(presence);
                                var close=MercenaryHiring.CloseControls(closingLeon?.Id??MercenaryHiring.LeonId(character.Id));
                                if(closingLeon!=null)await SendSnapshot([close,GamePackets.Remove(closingLeon.Id)]);
                                else await Send(close);
                                break;
                            }
                            if(presence.Leon is not {} activeLeon||activeLeon.Map!=character.Map)break;
                            if(p is [0x54,2,0])
                                await Send(MercenaryHiring.Profile(activeLeon,presence.LeonCommandSlot));
                            else if(p is [0x54,2,3])
                                await Send(MercenaryHiring.Inventory(character));
                            else if(p is [0x54,2,9,0,MercenaryHiring.ChaseCommandSlot or MercenaryHiring.AttackCommandSlot]){
                                presence.LeonCommandSlot=p[4];
                                await Send(MercenaryHiring.CommandSelection(p[4]));
                            }
                            break;
                        }
                        if(character.Map<0||p.Length<7||p[1]!=3||p[6]>4||presence?.Trade!=null)break;
                        uint roomSignId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                        if(world.InspectRoomSign(roomSignId,Environment.TickCount64) is not {} roomSign||
                            !WorldHub.CanRefreshRoomSign(character,roomSign.Sign))break;
                        if(roomSign.Owner!=character.Id){
                            if(p[6]==4&&p.Length==7&&roomSign.Open&&presence is {} guest&&
                                guest.RequestedRoomSign!=roomSignId&&hub.FindOnline(roomSign.Owner) is {} roomHost){
                                guest.RequestedRoomSign=roomSignId;
                                await WorldHub.Notify(roomHost,GamePackets.Message($"{character.Name}님이 개인방 초대를 요청했습니다."));
                            }
                            break;
                        }
                        if(p[6]==0&&p.Length==7)await EnterPersonalRoom(roomSign.Owner);
                        else if(p[6]==2&&p.Length==7)world.ToggleRoomSign(roomSignId,character.Id);
                        else if(p[6]==3&&p.Length==7&&world.RemoveRoomSign(roomSignId,character.Id) is {} removedSign)
                            await hub.BroadcastVisible(removedSign,GamePackets.Remove(removedSign.Id));
                        else if(p[6]==1&&p.Length>=8&&p[7]==p.Length-8&&p[7]<=30){
                            string invitation;
                            try{invitation=new PacketReader(p.AsSpan(7)).Str8();}
                            catch(DecoderFallbackException){break;}
                            if(invitation.Any(ch=>char.IsControl(ch)||ch is '\\' or '\'' or '"'))break;
                            if(world.SetRoomSignInvitation(roomSignId,character.Id,invitation) is {} editedSign)
                                await hub.BroadcastVisible(editedSign,[..GamePackets.Objects([],[editedSign]),0]);
                        }
                        break;
                    case 0x36 when role=="World"&&authenticated&&character!=null&&presence is {Ready:true}&&p.Length==2&&p[1]<=1:
                        var clanMembers=characters.GuildMembers(character);
                        string clanMaster=clanMembers.FirstOrDefault(member=>member.Id==character.GuildLeaderId)?.Name??"";
                        var clanRoster=p[1]==1?clanMembers.Select(member=>(member.Name,hub.FindOnline(member.Id)!=null)).ToArray():[];
                        await Send(GamePackets.ClanStatus(character.GuildName??"",clanMaster,clanRoster,p[1]==1));
                        break;
                    case 0x6C when role=="World"&&authenticated&&character!=null&&presence is {Ready:true}:
                        if(p.Length==2&&p[1]==2)await Send(GamePackets.CashShopList(character));
                        else if(p.Length==10&&p[1]==1&&presence.Trade==null){
                            uint orderId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                            uint entryId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(6));
                            ItemPlacement? granted;
                            try{(character,granted)=characters.ClaimCashShopItem(character.Id,orderId,entryId,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
                            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                                Console.Error.WriteLine($"Cash shop delivery save failed: {ex.Message}");
                                break;
                            }
                            presence.Character=character;
                            var listing=GamePackets.CashShopList(character);
                            if(granted!=null)await SendSnapshot([..GamePackets.PositionedInventoryAdd(character,granted),listing]);
                            else await Send(listing);
                        }
                        break;
                    case 0x6A when role=="World"&&authenticated&&character is {Map:<0,AwaitingRevival:false}:
                        if(p.Length<3||p[1]!=3)break;
                        if(p[2] is 0 or 12)await HandleRoomGuestCommand(p,character);
                        else if(p.Length==3&&p[2]==1)
                            await Send(GamePackets.PersonalRoomExitList(PersonalRoomMenuDestination(character)));
                        else if(p.Length==5&&p[2]==2)
                            await LeavePersonalRoom(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3)));
                        break;
                    case 0x43 when role=="World" && authenticated && character!=null:
                        if(p.Length!=6)break;
                        uint requestedId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                        if(p[1]==1&&world.InspectRoomSign(requestedId,Environment.TickCount64) is {} sign&&WorldHub.CanRefreshRoomSign(character,sign.Sign)){
                            await Send(GamePackets.RoomSignDialog(sign.Sign.Id,sign.Minutes,sign.Owner==character.Id,sign.Open));
                            break;
                        }
                        if(p[1]==1&&hub.FindVisible(character,requestedId) is {} inspected){
                            await Send(GamePackets.InspectCharacter(inspected.Character));
                            break;
                        }
                        var infoNpc=world.InMap(character.Map).SingleOrDefault(n=>n.Id==requestedId);
                        if(infoNpc!=null&&CanTalkTo(character,infoNpc,20,allowMerchantAcrossFloor:true))await OpenNpc(infoNpc,true);
                        break;
                    case 0x76 when role=="World" && authenticated && character!=null:
                        if(p.Length!=6)break;
                        uint relationId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                        if((p[1]==1&&relationId==character.Id)||
                            (p[1]==2&&relationId!=character.Id&&hub.FindVisible(character,relationId)!=null))
                            await Send(GamePackets.RelationshipInfo(relationId,p[1]));
                        break;
                    case 0x39 when role=="World" && authenticated && character!=null:
                        if(p.Length<8)break;
                        uint merchantId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                        ushort menu=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6));
                        var merchant=world.InMap(character.Map).SingleOrDefault(n=>n.Id==merchantId);
                        if(merchant==null||!CanTalkTo(character,merchant,20,allowMerchantAcrossFloor:true))break;
                        if(WorldBank.IsBanker(merchant)){
                            await HandleBankLogin(merchant,menu,p);
                            break;
                        }
                        if(activeNpcDialog==merchantId){
                            if(MercenaryHiring.IsGuard(merchant)&&MercenaryHiring.IsReceiptPage(activePursuitPath)&&menu==0x6F)break;
                            neruaDialog=null;
                            rubyDialog=null;
                            calasLesson=null;
                            oldSmithPage=null;
                            conversationStage=null;
                            metiniPage=null;
                            torionPage=null;
                            ranicheOffer=null;
                            cookingChoices=false;
                            if(p.Length==8&&p[1]==1&&CalendarQuest.For(merchant) is {} calendarQuest){
                                if(menu==0x6F&&calendarQuest!=CalendarQuest.Tree){
                                    await Send(calendarQuest.Menu(merchant));break;
                                }
                                if(menu is CalendarQuest.Visit or CalendarQuest.Claim){
                                    await HandleCalendarQuest(merchant,calendarQuest,menu==CalendarQuest.Claim);break;
                                }
                            }
                            if(TorionQuest.IsLena(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F&&!character.AwaitingRevival){
                                activePursuitPath=null;
                                await ShowTorion(merchant);
                                break;
                            }
                            if(MetiniQuest.IsNpc(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F&&
                                character.MetiniProgress!=MetiniStage.None){
                                await ShowMetini(merchant,MetiniQuest.Resume(character));
                                break;
                            }
                            if(OldSmithQuest.IsNpc(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F&&!character.AwaitingRevival){
                                activePursuitPath=null;
                                oldSmithPage=OldSmithPage.Menu;
                                await Send(OldSmithQuest.Packet(merchant,OldSmithPage.Menu));
                                break;
                            }
                            if(CalasMusicLesson.IsNpc(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F){
                                activePursuitPath=null;
                                await BeginCalasLesson(merchant);
                                break;
                            }
                            if(RubyJewelry.IsNpc(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F){
                                activePursuitPath=null;
                                activeShop=null;
                                activeShopService=null;
                                rubyDialog=new();
                                await Send(rubyDialog.Open(merchant));
                                break;
                            }
                            if(NeruaDialog.IsNpc(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F){
                                activePursuitPath=null;
                                neruaDialog=new();
                                await Send(neruaDialog.Packet(merchant));
                                break;
                            }
                            if(HairSalonDialog.IsBarber(merchant)&&p.Length==8&&p[1]==1&&menu==0x6F){
                                activePursuitPath=null;
                                hairSalon=new(merchant);
                                await Send(hairSalon.Packet());
                                break;
                            }
                            activePursuitPath=null;
                            guildFounding=null;
                            string key=Convert.ToHexString(p.AsSpan(0,2))+Convert.ToHexString(p.AsSpan(6));
                            if(p.Length==8&&key=="3901006F"&&MutaiButcher.IsNpc(merchant)){
                                activeShop=null;
                                activeShopService=null;
                                activePursuitPath=MutaiButcher.MenuPath;
                                await Send(GamePackets.NpcPursuitPage(merchant,"안녕하세요.? 어떤 고기를 다듬어 드릴까요.?",
                                    ["닭을 다듬어 주세요.!","나중에 다시 오겠습니다."]));
                                break;
                            }
                            var pursuit=p.Length==8?GamePackets.NpcPursuit(merchant,key):null;
                            bool unrecordedTalk=key=="3901006F"&&pursuit==null&&
                                merchant.DialogReplies?.Any(r=>r.Flags==1&&r.Menu==menu&&r.Length==8)!=true;
                            if(unrecordedTalk&&!character.AwaitingRevival&&KondQuest.IsOblo(merchant)){
                                torionPage=TorionQuest.ObloMenuPage;
                                await Send(TorionQuest.ObloMenu(merchant));
                                break;
                            }
                            if(unrecordedTalk&&!character.AwaitingRevival&&KondQuest.IsTrainer(merchant)){
                                await ShowKondQuest(merchant);
                                break;
                            }
                            if(unrecordedTalk&&RaddHunt.IsNpc(merchant)){
                                await ShowRaddHunt(merchant);
                                break;
                            }
                            if(p.Length==8&&GatheringQuest.ForNpc(merchant) is {} gathering&&
                                (key==KayGatheringQuest.MenuPath&&gathering==GatheringQuest.Kay&&
                                    character.GatheringDeadline(gathering)>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()||
                                key=="3901006F"&&gathering==GatheringQuest.Pini&&
                                    character.GatheringDeadline(gathering)>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()||
                                unrecordedTalk&&gathering!=GatheringQuest.Kay)){
                                await ShowGatheringQuest(merchant,gathering);
                                break;
                            }
                            if(unrecordedTalk&&TeddyTailoring.IsTeddy(merchant)){
                                activePursuitPath=TeddyTailoring.MenuPath;
                                await Send(GamePackets.NpcPursuitPage(merchant,"어떤 옷을 만들어 드릴까요?",
                                    TeddyTailoring.Recipes.Select(recipe=>recipe.Menu).ToArray()));
                                break;
                            }
                            if(unrecordedTalk&&SektiSmithing.IsNpc(merchant)){
                                await ConfirmNpcCrafting(merchant,SektiSmithing.Recipe);
                                break;
                            }
                            if(p.Length==8&&pursuit!=null){
                                activePursuitPath=key;
                                if(key=="3901006F"&&(merchant.Map%10000,merchant.Name) is (210,"홀리") or (211,"테레즈"))
                                    await RecoverAtNpc(merchant,pursuit);
                                else await Send(pursuit);
                                break;
                            }
                        }
                        if(activeNpcDialog==merchantId&&merchant.DialogReplies?.FirstOrDefault(
                            r=>r.Flags==p[1]&&r.Menu==menu&&r.Length==p.Length) is {} reply){
                            await Send(GamePackets.NpcDialog(merchant,reply.Dialog));
                            break;
                        }
                        if(p[1]!=merchant.ShopFlags)break;
                        if(p.Length==8&&activeNpcDialog==merchantId&&
                            (menu is GamePackets.ShopSellAction or GamePackets.ShopRepairAction)&&merchant.HasService(menu)){
                            await OpenShopService(merchant,menu);
                        }else if(p.Length>=10&&activeShopService is {} service&&service.Merchant==merchantId&&service.Order==menu){
                            await CommitShopService(merchant,p,menu,service.Offers);
                        }else if(p.Length==8&&menu==GamePackets.ShopBuyAction&&merchant.Shop!=null){
                            activeShopService=null;
                            activeShop=merchant;
                            await SendShopCatalog(merchant);
                        }else if(p.Length==8&&menu==0x96){
                            activeShop=null;
                            activeShopService=null;
                        }else if(menu==GamePackets.ShopBuyOrder&&activeShop?.Id==merchantId&&merchant.Shop!=null&&p.Length>=10){
                            if(activeShop.Shop!.SequenceEqual(merchant.Shop!))
                                await BuyItems(merchant,p);
                            else{
                                activeShop=null;
                                await Send(GamePackets.ShopNotice(p[8],false,"상품 목록이 바뀌었습니다. 사기 창을 다시 열어 주세요."));
                            }
                        }
                        break;
                    case 0x72 when role=="World"&&authenticated&&p.Length>=3&&p[1]==2:
                        await HandleBank(p);
                        break;
                    case 0x72 when role=="World"&&authenticated&&p.Length>=3&&p[1]==3:
                        await HandleLeonInventory(p);
                        break;
                    case 0x3A when role=="World" && authenticated && character!=null:
                        if(p.Length<11)break;
                        uint pursuitId=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2));
                        ushort pursuitScript=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6));
                        if(pursuitId==GuardianQuest.SpeakerId&&pursuitScript==GuardianQuest.Script){
                            await AdvanceGuardian(p);
                            break;
                        }
                        if(metiniPage is {} lessonPage&&activeNpcDialog==pursuitId){
                            var teacher=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&MetiniQuest.IsNpc(n));
                            if(teacher!=null&&CanTalkTo(character,teacher,20)&&!character.AwaitingRevival)
                                await AdvanceMetini(teacher,lessonPage,p);
                            else metiniPage=null;
                            break;
                        }
                        if(pursuitScript>=0x6000&&pursuitScript<0x8000){
                            if(p[1]!=1||presence==null||!guildFoundings.TryGetValue(pursuitScript,out var founding)||
                                pursuitId!=founding.Npc.Id||presence!=founding.First&&presence!=founding.Second)break;
                            if(Environment.TickCount64>=founding.Due){await FinishGuildFounding(founding,GuildRegistration.Expired);break;}
                            if(!founding.Participants.All(member=>CanFoundGuildHere(member,founding.Npc))){
                                await FinishGuildFounding(founding,GuildRegistration.MissingMembers);break;
                            }
                            if(p.Length==11&&p[8]==0&&p[9]==1&&p[10]==0||
                                p.Length==12&&p[8]==0&&p[9]==2&&p[10]==1&&p[11]==2){
                                await FinishGuildFounding(founding,GuildRegistration.Declined);break;
                            }
                            if(p.Length==12&&p[8]==0&&p[9]==2&&p[10]==1&&p[11]==1&&
                                founding.Accepted.Add(presence)&&founding.Accepted.Count==2)
                                await FinishGuildFounding(founding,GuildRegistration.Created);
                            break;
                        }
                        if(guildFlagOffer==pursuitId&&activeNpcDialog==pursuitId){
                            if(p[1]!=1||p[6]!=0||p[7]!=0||p[8]!=0)break;
                            guildFlagOffer=null;
                            if(p.Length==12&&p[9]==2&&p[10]==1&&p[11]==1&&
                                world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&GuildHouseCapture.IsFlag(n)) is {} guildFlag)
                                await StartGuildCapture(guildFlag);
                            break;
                        }
                        if(hairSalon is {} salon&&pursuitId==activeNpcDialog){
                            await AdvanceHairSalon(salon,p,pursuitId);
                            break;
                        }
                        bool guildBackToIntro=guildFounding?.AtFirstMember==true&&p.Length==11&&p[1]==1&&
                            p.AsSpan(6,5).SequenceEqual(new byte[]{0,0,0,0,0});
                        if(guildFounding is {} form&&form.NpcId==pursuitId&&activeNpcDialog==pursuitId&&!guildBackToIntro){
                            if(p[1]!=1||p[6]!=0||p[7]!=0||p[8]!=0)break;
                            var guildNpc=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId);
                            if(guildNpc==null||guildNpc.Floor!=character.Floor||
                                Math.Max(Math.Abs(guildNpc.X-character.X),Math.Abs(guildNpc.Y-character.Y))>20)break;
                            byte pageCode=p[9],action=p[10],choice=0;
                            string? input=null;
                            if(action==0&&p.Length==11){
                                if(pageCode==1){guildFounding=null;activePursuitPath=null;break;}
                            }else if(action==1&&p.Length==12)choice=p[11];
                            else if(action==2&&p.Length>=12&&p[11]<=24&&p.Length==12+p[11]){
                                try{input=new PacketReader(p.AsSpan(11)).Str8();}
                                catch(DecoderFallbackException){break;}
                            }else break;
                            WorldSession? Nearby(string name)=>hub.FindOnline(name) is {} member&&CanFoundGuildHere(member,guildNpc)?member:null;
                            var founder=character;
                            GuildFoundingRequest? invitation=null;
                            GuildRegistration Invite(string first,string second,string name){
                                if(presence==null||!CanFoundGuildHere(presence,guildNpc)||
                                    Nearby(first) is not {} firstMember||Nearby(second) is not {} secondMember)
                                    return GuildRegistration.MissingMembers;
                                if(characters.IsGuildNameTaken(name))return GuildRegistration.NameTaken;
                                WorldSession[] participants=[presence,firstMember,secondMember];
                                if(participants.Any(member=>member.Character.GuildName!=null))return GuildRegistration.AlreadyMember;
                                if(founder.Gold<WorldSettings.GuildFoundingCost)return GuildRegistration.InsufficientGold;
                                if(guildFoundings.Values.Any(other=>other.Participants.Any(participants.Contains)))return GuildRegistration.Busy;
                                ushort script;
                                do{
                                    script=nextGuildFoundingScript;
                                    nextGuildFoundingScript=script==0x7FFF?(ushort)0x6000:(ushort)(script+1);
                                }while(guildFoundings.ContainsKey(script));
                                invitation=new(script,guildNpc,form,presence,firstMember,secondMember,name,Environment.TickCount64+30_000);
                                guildFoundings.Add(script,invitation);
                                return GuildRegistration.Waiting;
                            }
                            if(form.Advance(pageCode,action,choice,input,founder.Name,Invite)){
                                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                if(invitation==null)await Send(form.Packet(guildNpc));
                                else{
                                    await Send(GamePackets.Message(GuildFoundingDialog.WaitingMessage));
                                    var prompt=invitation.Invitation();
                                    await WorldHub.Notify(invitation.First,prompt);
                                    await WorldHub.Notify(invitation.Second,prompt);
                                }
                            }
                            break;
                        }
                        if(fameDialog is {} exchangeDialog&&pursuitId==FameExchangeDialog.SpeakerId){
                            await AdvanceFameDialog(exchangeDialog,p);
                            break;
                        }
                        if(oldSmithPage is {} smithPage&&activeNpcDialog==pursuitId){
                            var smith=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&OldSmithQuest.IsNpc(n));
                            if(smith!=null&&CanTalkTo(character,smith,20)&&!character.AwaitingRevival)
                                await AdvanceOldSmith(smith,smithPage,p);
                            else oldSmithPage=null;
                            break;
                        }
                        if(conversationStage is int exam&&activeNpcDialog==pursuitId){
                            var teacher=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&SenseTraining.IsTeacher(n));
                            if(teacher!=null&&CanTalkTo(character,teacher,20)&&!character.AwaitingRevival)
                                await AdvanceConversationExam(teacher,exam,p);
                            else conversationStage=null;
                            break;
                        }
                        if(p[10]==2&&shrinePage==ShrinePage.GradeInput&&pursuitId==ShrineDialog.SpeakerId(character.Map)){
                            if(p[1]==4&&pursuitScript==0&&p[8]==0&&p[9]==2&&p.Length>=12&&
                                p[11]<=ShrineGrade.InputLimit&&p.Length==12+p[11]){
                                string input;
                                try{input=new PacketReader(p.AsSpan(11)).Str8();}
                                catch(DecoderFallbackException){break;}
                                await AdvanceShrinePage(ShrinePage.GradeInput,2,0,input);
                            }
                            break;
                        }
                        if(!((p.Length==11&&p[10]==0)||(p.Length==12&&p[10]==1)))break;
                        if(ranicheOffer is {} ranicheStage&&activeNpcDialog==pursuitId){
                            var storyteller=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&RanicheQuest.CanSpeak(n,ranicheStage));
                            if(storyteller!=null&&CanTalkTo(character,storyteller,RanicheQuest.IsRex(storyteller)?8:20)&&!character.AwaitingRevival)
                                await AdvanceRaniche(storyteller,ranicheStage,p);
                            else ranicheOffer=null;
                            break;
                        }
                        if(cookingChoices&&activeNpcDialog==pursuitId){
                            if(p[1]!=1||p[6]!=(CookingRecipe.Script>>8)||p[7]!=(CookingRecipe.Script&255)||
                                p[8]!=0||p[9]!=(p.Length==12?2:1))break;
                            cookingChoices=false;
                            if(p.Length!=12||p[11]==0||p[11]>CookingRecipe.All.Length||character.AwaitingRevival)break;
                            var recipe=CookingRecipe.All[p[11]-1];
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            if(recipe.Requirement(character,useKit:true) is {} reason)await Send(GamePackets.Message(reason));
                            else await OpenCraft(pursuitId,recipe);
                            break;
                        }
                        if(torionPage is ushort storyPage&&activeNpcDialog==pursuitId){
                            var speaker=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId);
                            if(speaker!=null&&CanTalkTo(character,speaker,20)&&!character.AwaitingRevival)
                                await AdvanceTorion(speaker,storyPage,p);
                            else torionPage=null;
                            break;
                        }
                        if(erenoaPage is {} exchangePage&&activeNpcDialog==pursuitId){
                            var trader=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&ErenoaQuest.IsNpc(n));
                            if(trader!=null&&CanTalkTo(character,trader,20)&&!character.AwaitingRevival)
                                await AdvanceErenoa(trader,exchangePage,p);
                            else erenoaPage=null;
                            break;
                        }
                        if(rubyDialog is {} jewelry&&activeNpcDialog==pursuitId){
                            var jeweler=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&RubyJewelry.IsNpc(n));
                            if(jeweler==null||!CanTalkTo(character,jeweler,20)){rubyDialog=null;break;}
                            var (jewelryReply,craft,finished)=jewelry.Advance(jeweler,p);
                            if(finished)rubyDialog=null;
                            if(jewelryReply!=null||craft!=null)await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            if(jewelryReply!=null)await Send(jewelryReply);
                            else if(craft!=null)await FinishJewelry(jeweler,craft);
                            break;
                        }
                        if(calasLesson is {} lesson&&lesson.NpcId==pursuitId&&activeNpcDialog==pursuitId){
                            var musician=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&CalasMusicLesson.IsNpc(n));
                            if(musician!=null&&CanTalkTo(character,musician,20)&&!character.AwaitingRevival)
                                await AdvanceCalasLesson(musician,lesson,p);
                            else calasLesson=null;
                            break;
                        }
                        if(neruaDialog is {} conversation&&activeNpcDialog==pursuitId){
                            var speaker=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId&&NeruaDialog.IsNpc(n));
                            if(speaker!=null&&CanTalkTo(character,speaker,20))await AdvanceNeruaDialog(speaker,conversation,p);
                            else neruaDialog=null;
                            break;
                        }
                        if(shrinePage is {} currentShrinePage&&pursuitId==ShrineDialog.SpeakerId(character.Map)){
                            if(p[1]==4&&p[6]==0&&p[7]==0&&p[8]==0&&p[9]==2)
                                await AdvanceShrinePage(currentShrinePage,p[10],p.Length==12?p[11]:(byte)0);
                            else if(p.Length==11&&p[1]==4&&pursuitScript==0&&p[8]==0&&p[9]==0&&p[10]==0&&
                                !character.AwaitingRevival&&ShrineDialog.InRange(character)){
                                ShrinePage? back=currentShrinePage switch {
                                    ShrinePage.Welcome=>ShrinePage.PrayerWarning,
                                    ShrinePage.PrayerWarning=>ShrinePage.Prayer,
                                    ShrinePage.FateRemaining=>ShrinePage.FateAllocated,
                                    ShrinePage.FateOrderChanged=>ShrinePage.FateRemaining,
                                    ShrinePage.GradeInput=>ShrinePage.GradeKind,
                                    _=>null
                                };
                                if(back is {} previousPage)
                                    await ShowShrinePage(previousPage,aptitude:shrineFateAptitude,previousOrder:shrinePreviousFateOrder);
                            }
                            break;
                        }
                        if(activeNpcDialog!=pursuitId||activePursuitPath==null)break;
                        string nextPage=activePursuitPath+"/"+Convert.ToHexString(p.AsSpan(0,2))+Convert.ToHexString(p.AsSpan(6));
                        var pursuitNpc=world.InMap(character.Map).FirstOrDefault(n=>n.Id==pursuitId);
                        if(pursuitNpc==null||pursuitNpc.Floor!=character.Floor||
                            Math.Max(Math.Abs(pursuitNpc.X-character.X),Math.Abs(pursuitNpc.Y-character.Y))>20)break;
                        if(MercenaryHiring.IsGuard(pursuitNpc)){
                            if(nextPage is MercenaryHiring.Confirmed or MercenaryHiring.Declined){
                                activePursuitPath=null;
                                if(nextPage==MercenaryHiring.Confirmed&&MercenaryHiring.ReceiptPage(pursuitNpc,nextPage) is {} receipt)
                                    await BuyLeonDeed(pursuitNpc,receipt);
                                break;
                            }
                            if(MercenaryHiring.IsReceiptPage(activePursuitPath)){
                                if(activePursuitPath!=MercenaryHiring.Confirmed&&p.Length==11&&p[1]==1&&
                                    p.AsSpan(6).SequenceEqual(new byte[]{0,0,0,0,0}))
                                    nextPage=activePursuitPath==MercenaryHiring.Farewell?MercenaryHiring.Dismissal:MercenaryHiring.Confirmed;
                                else if(p.Length!=11||p[1]!=1||!p.AsSpan(6).SequenceEqual(new byte[]{0,0,0,2,0}))break;
                                activePursuitPath=null;
                                if(MercenaryHiring.ReceiptPage(pursuitNpc,nextPage) is {} followup){
                                    activePursuitPath=nextPage;
                                    await Send(followup);
                                }
                                break;
                            }
                        }
                        if(CastleOffice.IsClerk(pursuitNpc)&&
                            activePursuitPath is CastleOffice.ResignationOffer or CastleOffice.BattleChargeOffer){
                            string offer=activePursuitPath;
                            if(nextPage==offer+CastleOffice.Confirm||nextPage==offer+CastleOffice.Decline){
                                activePursuitPath=null;
                                if(nextPage==offer+CastleOffice.Confirm)
                                    await CompleteCastleOffice(pursuitNpc,offer==CastleOffice.BattleChargeOffer,nextPage);
                                break;
                            }
                        }
                        if(MetiniQuest.IsNpc(pursuitNpc)&&nextPage==MetiniQuest.HealthCheckPath&&MetiniQuest.MeetsHealthRequirement(character)){
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            await ShowMetini(pursuitNpc,MetiniPage.Explanation);
                            break;
                        }
                        if(SenseTraining.IsTeacher(pursuitNpc)){
                            if(nextPage==ConversationTraining.StartPath){
                                if(!character.AwaitingRevival)await BeginConversationExam(pursuitNpc);
                                break;
                            }
                            if(GrepaCombatSkill.All.FirstOrDefault(skill=>skill.OfferPath==activePursuitPath) is {} skillOffer){
                                if(p[1]!=1||new PacketReader(p.AsSpan(6)).U16()!=MeditationTraining.Script)break;
                                var requestPage=new PacketReader(p.AsSpan(8)).U16();
                                if(p.Length==11&&requestPage==skillOffer.OfferPage+1)break;
                                activePursuitPath=null;
                                if(!character.AwaitingRevival&&p.Length==12&&requestPage==skillOffer.OfferPage+1&&p[11]==1){
                                    await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                    await EnrollGrepaSkill(pursuitNpc,skillOffer);
                                }
                                break;
                            }
                            if(nextPage==GrepaCombatSkill.MartialJobCheck&&character.Job!=GrepaCombatSkill.BodyBlow.Job){
                                await OfferGrepaSkill(pursuitNpc,GrepaCombatSkill.BodyBlow,nextPage);
                                break;
                            }
                            if(GrepaCombatSkill.All.FirstOrDefault(skill=>skill.CheckPath==nextPage) is {} grepaSkill){
                                if(!character.AwaitingRevival)await OfferGrepaSkill(pursuitNpc,grepaSkill,nextPage);
                                break;
                            }
                            if(activePursuitPath is MeditationTraining.OfferPath or MeditationTraining.NextOfferPath&&
                                p[1]==1&&p.AsSpan(6,4).SequenceEqual(new byte[]{0,0xDC,0,0xB0})){
                                if(character.AwaitingRevival||p.Length==12){
                                    activePursuitPath=null;
                                    if(!character.AwaitingRevival&&p[11]==1){
                                        await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                        await EnrollMeditation(pursuitNpc);
                                    }
                                    break;
                                }
                                if(MeditationTraining.PureIntelligence(character)>=15){
                                    // The arrow can inspect the offer; only a Yes selection can debit the wallet.
                                    if(GamePackets.NpcPursuit(pursuitNpc,activePursuitPath) is {} offer)await Send(offer);
                                    break;
                                }
                            }
                            if(nextPage==SenseTraining.CheckPath){await OfferSense(pursuitNpc);break;}
                            if(activePursuitPath==SenseTraining.OfferPath){
                                activePursuitPath=null;
                                if(p.Length==12&&p[1]==1&&p.AsSpan(6).SequenceEqual(new byte[]{0,0xDC,1,0x45,1,1})){
                                    await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                    await EnrollSense(pursuitNpc);
                                }
                                break;
                            }
                        }
                        if(activePursuitPath==MutaiButcher.MenuPath&&MutaiButcher.IsNpc(pursuitNpc)){
                            activePursuitPath=null;
                            if(p.Length==12&&p[1]==1&&p.AsSpan(6).SequenceEqual(new byte[]{0,0,0,2,1,1})){
                                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                await DressChicken(pursuitNpc);
                            }
                            break;
                        }
                        if(KondQuest.IsTrainer(pursuitNpc)&&activePursuitPath is KondQuest.OfferPath or KondQuest.TurnInPath){
                            var path=activePursuitPath;
                            byte kondPage=(byte)(path==KondQuest.TurnInPath?KondQuest.TurnInPage:KondQuest.OfferPage);
                            if(p[1]!=1||p[6]!=0||p[7]!=0||p[8]!=0||p[9]!=kondPage+(p[10]==1?1:0))break;
                            activePursuitPath=null;
                            if(!character.AwaitingRevival&&p.Length==12&&p[11]==1){
                                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                await AdvanceKondQuest(pursuitNpc,path);
                            }
                            break;
                        }
                        if(RaddHunt.IsNpc(pursuitNpc)&&activePursuitPath is RaddHunt.OfferPath or RaddHunt.SelectPath or RaddHunt.TurnInPath){
                            var path=activePursuitPath;
                            activePursuitPath=null;
                            if(!character.AwaitingRevival&&p.Length==12&&p[1]==1&&p.AsSpan(6,5).SequenceEqual(new byte[]{0,0,0,2,1}))
                                await AdvanceRaddHunt(pursuitNpc,path,p[11]);
                            break;
                        }
                        if(SektiSmithing.IsNpc(pursuitNpc)&&activePursuitPath==SektiSmithing.ShieldKey){
                            activePursuitPath=null;
                            if(p.Length==12&&p[1]==1&&p.AsSpan(6).SequenceEqual(new byte[]{0,0,0,2,1,1})){
                                await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                await FinishNpcCrafting(pursuitNpc,SektiSmithing.Recipe);
                            }
                            break;
                        }
                        if(TeddyTailoring.IsTeddy(pursuitNpc)){
                            if(nextPage==TeddyTailoring.RecordedPantsMaterialsPath||nextPage==TeddyTailoring.RecordedShoesPath){
                                var recipe=TeddyTailoring.Recipes[nextPage==TeddyTailoring.RecordedPantsMaterialsPath?0:1];
                                if(GamePackets.NpcPursuit(pursuitNpc,nextPage) is {} recorded&&
                                    (nextPage==TeddyTailoring.RecordedShoesPath||recipe.Materials.Any(material=>!material.IsAvailable(character)))){
                                    activePursuitPath=nextPage;
                                    await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                    await Send(recorded);
                                }else{
                                    activePursuitPath=null;
                                    await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                                    await ConfirmNpcCrafting(pursuitNpc,recipe);
                                }
                                break;
                            }
                            bool choosing=activePursuitPath==TeddyTailoring.MenuPath;
                            var selectedRecipe=TeddyTailoring.Recipes.FirstOrDefault(recipe=>recipe.ProductKey==activePursuitPath);
                            if(choosing||selectedRecipe!=null){
                                activePursuitPath=null;
                                if(p.Length!=12||p[1]!=1||!p.AsSpan(6,5).SequenceEqual(new byte[]{0,0,0,2,1}))break;
                                byte recipeChoice=p[11];
                                if(choosing&&recipeChoice>0&&recipeChoice<=TeddyTailoring.Recipes.Length)
                                    await ConfirmNpcCrafting(pursuitNpc,TeddyTailoring.Recipes[recipeChoice-1]);
                                else if(selectedRecipe!=null&&recipeChoice==1)await FinishNpcCrafting(pursuitNpc,selectedRecipe);
                                break;
                            }
                        }
                        var gatheringQuest=GatheringQuest.ForNpc(pursuitNpc);
                        if(gatheringQuest==GatheringQuest.Pini&&nextPage==GatheringQuest.PiniAcceptPath){
                            activePursuitPath=null;
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            if(await AcceptGatheringQuest(pursuitNpc,gatheringQuest))
                                await Send(GamePackets.NpcPursuitPage(pursuitNpc,$"12시간 안에 비늘 20개를 가져와 주세요."));
                            break;
                        }
                        if(SektiSmithing.IsNpc(pursuitNpc)&&nextPage==SektiSmithing.RecordedAcceptPath){
                            activePursuitPath=null;
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            if(!SektiSmithing.Recipe.Materials[0].IsAvailable(character)&&GamePackets.NpcPursuit(pursuitNpc,nextPage) is {} recorded)
                                await Send(recorded);
                            else await FinishNpcCrafting(pursuitNpc,SektiSmithing.Recipe);
                            break;
                        }
                        if(gatheringQuest!=null&&activePursuitPath is GatheringQuest.OfferPath or GatheringQuest.TurnInPath){
                            bool turnIn=activePursuitPath==GatheringQuest.TurnInPath;
                            activePursuitPath=null;
                            if(p.Length!=12||p[1]!=1||!p.AsSpan(6).SequenceEqual(new byte[]{0,0,0,2,1,1}))break;
                            await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            if(turnIn)await FinishGatheringQuest(pursuitNpc,gatheringQuest);
                            else if(await AcceptGatheringQuest(pursuitNpc,gatheringQuest))
                                await Send(GamePackets.NpcPursuitPage(pursuitNpc,
                                    $"의뢰를 맡아 주셔서 고마워요. {gatheringQuest.Hours}시간 안에 {gatheringQuest.MaterialName} {gatheringQuest.Required}개를 가져와 주세요."));
                            break;
                        }
                        bool citizenConfirmation=pursuitNpc.Name=="그레파"&&pursuitNpc.Map%10000==210&&activePursuitPath==rolanciaConfirmation;
                        if(citizenConfirmation&&nextPage!=rolanciaAccept){activePursuitPath=null;break;}
                        bool guildNpcSelected=GuildFoundingDialog.IsGuildNpc(pursuitNpc);
                        if(guildNpcSelected&&nextPage==GuildFoundingDialog.FoundingIntro&&character.GuildName!=null){
                            activePursuitPath=null;
                            await Send(GuildFoundingDialog.Notice(pursuitNpc,
                                "자네는 이미 소속 길드가 있지 않은가..!!|길드를 새로 창립하려면, 우선 그 길드에서 탈퇴하게!! ",next:true));
                            break;
                        }
                        bool mineTravel=TiponeMineTravel.IsTipone(pursuitNpc)&&activePursuitPath==TiponeMineTravel.OfferPath;
                        if(gatheringQuest==GatheringQuest.Kay&&nextPage==KayGatheringQuest.DeclinePath){
                            activePursuitPath=null;
                            break;
                        }
                        if(GamePackets.NpcPursuit(pursuitNpc,nextPage) is {} page){
                            if(nextPage==KayGatheringQuest.AcceptPath&&gatheringQuest==GatheringQuest.Kay&&
                                !await AcceptGatheringQuest(pursuitNpc,gatheringQuest))break;
                            if(citizenConfirmation&&!await RegisterRolancia(pursuitNpc)){activePursuitPath=null;break;}
                            bool guildDeparture=guildNpcSelected&&nextPage==GuildFoundingDialog.DepartureAccept;
                            if(guildDeparture&&!await LeaveGuild(pursuitNpc)){activePursuitPath=null;break;}
                            activePursuitPath=nextPage;
                            guildFounding=guildNpcSelected&&nextPage==GuildFoundingDialog.FirstInputPath?new(pursuitId):null;
                            if(guildNpcSelected)await Task.Delay(GamePackets.PursuitReplyDelayMs,ct);
                            await Send(page);
                            if(guildDeparture){
                                await hub.BroadcastVisible(character!,GamePackets.Human(character!));
                                if(selfLookRequested)await Send(GamePackets.SelfLook(character!));
                            }
                        }else if(pursuitNpc.PursuitTravel?.FirstOrDefault(r=>r.Request==nextPage) is {} route){
                            if(mineTravel)activePursuitPath=null;
                            await TravelFromNpc(route,mineTravel&&nextPage==TiponeMineTravel.AcceptPath?TiponeMineTravel.Fee:0);
                        }else if(TiponeMineTravel.IsTipone(pursuitNpc)&&nextPage==TiponeMineTravel.OfferPath){
                            activePursuitPath=nextPage;
                            await Send(GamePackets.NpcPursuitPage(pursuitNpc,"50갈리드를 내고 로랜시아 광산 앞으로 이동하시겠어요?",["예","아니오"]));
                        }else if(mineTravel){
                            activePursuitPath=null;
                            if(nextPage==TiponeMineTravel.AcceptPath)
                                await TravelFromNpc(new(nextPage,TiponeMineTravel.Arrival(character.Map)),TiponeMineTravel.Fee);
                        }else if(p.Length==11&&GamePackets.NpcPursuit(pursuitNpc,activePursuitPath) is {} shown&&
                            shown[2]==p[1]&&shown.AsSpan(15,2).SequenceEqual(p.AsSpan(6,2))){
                            var shownPage=new PacketReader(shown.AsSpan(17)).U16();
                            var requestedPage=new PacketReader(p.AsSpan(8)).U16();
                            if(requestedPage==shownPage){activePursuitPath=null;guildFounding=null;}
                            else if(shown[19]==1&&requestedPage==unchecked((ushort)(shownPage-1))){
                                int split=activePursuitPath.LastIndexOf('/');
                                if(split>0&&GamePackets.NpcPursuit(pursuitNpc,activePursuitPath[..split]) is {} previousPage){
                                    activePursuitPath=activePursuitPath[..split];
                                    guildFounding=null;
                                    await Send(previousPage);
                                }
                            }
                        }else if(p.Length==12&&p[11]==2&&
                            pursuitNpc.PursuitTravel?.Any(r=>r.Request==nextPage[..^2]+"01")==true){
                            activePursuitPath=null;
                        }
                        break;
                    case 0x4A when role=="World" && presence!=null && character!=null:
                        byte tradeMode=p.Length>1?p[1]:byte.MaxValue;
                        if(p.Length!=(tradeMode switch{0 or 4 or 5=>6,1=>7,2=>11,_=>0}))break;
                        var tradeReader=new PacketReader(p.AsSpan(2));
                        uint partnerId=tradeReader.U32();
                        if(tradeMode==0){
                            await WorldTrade.Open(presence,hub.FindOnline(partnerId));
                        }else if(presence.Trade is {} trade){
                            byte slot=tradeMode is 1 or 2?tradeReader.U8():(byte)0;
                            uint quantity=tradeMode==2?tradeReader.U32():0;
                            await trade.Handle(presence,partnerId,tradeMode,slot,quantity,characters);
                        }
                        break;
                    case 0x6E when role=="World" && presence!=null:
                        await HandleCraft(p);
                        break;
                    case 0x11 when role=="World" && authenticated && character!=null && presence!=null:
                        if(p.Length!=2||p[1]>7)break;
                        if(character.Direction!=p[1]){
                            if(SavePosition(character,character.X,character.Y,p[1]) is not {} turned){
                                await Send(GamePackets.Turn(character));break;
                            }
                            character=turned;presence.Character=turned;
                        }
                        var turn=GamePackets.Turn(character);
                        bool betaTurn=statusDimension==1&&character.Map/10000==1;
                        await WorldHub.Notify(presence,betaTurn?[..turn,0]:turn);
                        await hub.BroadcastVisible(character,turn,except:character.Id);
                        break;
                    case 0x51 when role=="World" && authenticated && presence!=null && character!=null:
                        if(p.Length==2&&p[1] is 0 or 1 or 2 or 4 or 8){
                            presence.CombatMode=p[1];
                            // The Alpha East Plain C51 reply has one trailing zero on the actor's connection.
                            bool paddedReply=statusDimension==0&&character.Map==4&&p[1] is 1 or 2;
                            await hub.BroadcastCombatMode(character,p[1],paddedReply?presence:null);
                        }
                        break;
                    case 0x85 when role=="World"&&authenticated&&character!=null:
                        // 0049F95E requests slots 1–9 with mode 0, action 5.
                        if(p.Length==4&&p[1]==0&&p[2] is >=1 and <=9&&p[3]==5)
                            await Send(GamePackets.EmptyPetSlot(p[2]));
                        break;
                    case 0x13 when role=="World" && authenticated && character!=null:
                        if(p.Length==3&&p[1]==5&&p[2]==0&&Fishing.HasRod(character))await CastFishingLine(character);
                        else if(p is [0x13,1,0])await Attack(character,weakMerchantStrike:true);
                        // v611 encodes the X attack as the auxiliary gauge divided by 100.
                        else if(p.Length==3&&(p[1]==0||p[1]==5&&
                            (p[2]==35||p[2]<=Math.Min(1000u,presence?.VitalsAux??0)/100)))await Attack(character);
                        break;
                    case 0x14 when role=="World" && authenticated && character!=null:
                        // CFarAttack 004D056A emits mode, target OID, then a 32-direction heading.
                        if(p.Length==7&&p[1]==0&&p[6]<32)await Attack(character,new PacketReader(p.AsSpan(2)).U32(),p[6]);
                        break;
                    case 0x3E when role=="World"&&authenticated&&character!=null:
                        if(p.Length==6&&p[1]==SenseTraining.Slot)
                            await UseSense(character,new PacketReader(p.AsSpan(2)).U32());
                        else if(p.Length==2&&p[1]==MeditationTraining.Slot)
                            await UseMeditation(character);
                        else if(p.Length==2&&p[1]==ConversationTraining.Slot)
                            await UseConversation(character);
                        else if(p.Length==2&&GrepaCombatSkill.All.FirstOrDefault(skill=>skill.Slot==p[1]) is {} grepaSkill)
                            await UseGrepaCombatSkill(character,grepaSkill);
                        break;
                    case 0x33 when role=="World" && authenticated && character!=null:
                        if(p.Length!=3||p[1]>7)break;
                        var localMap=world.GetMap(character.Map);
                        var (dx,dy)=LocalMap.Delta(p[1]);
                        // The field's north exit is blocked in the terrain layer.
                        // Check the observed server portal before ordinary tile collision.
                        if(world.StepDestination(character.Map,character.X+dx,character.Y+dy,character.Floor,p[1]) is {} portal){
                            if(!CanEnterRegion(portal.Map)){
                                await BlockRegionTravel(portal.Map,GamePackets.Move(8,character.X,character.Y,1,character.Floor));
                                break;
                            }
                            if(world.AvailableDoorArrival(portal,(x,y,floor)=>hub.IsOccupied(portal.Map,x,y,floor,character.Id)) is not {} arrival){
                                await Send(GamePackets.Move(8,character.X,character.Y,1,character.Floor));break;
                            }
                            if(SavePosition(character,arrival.X,arrival.Y,p[1],arrival.Map,arrival.Floor) is not {} transferred){
                                await Send(GamePackets.Move(8,character.X,character.Y,1,character.Floor));break;
                            }
                            await EnterMap(transferred,southGate:WorldData.IsSouthGate(character.Map,portal.Map),
                                recordedArrival:WorldData.UsesRecordedDoorArrival(character.Map,portal.Map),
                                door:world.DoorArrival(character.Map,portal.Map));
                            break;
                        }
                        if(!localMap.TryStep(character.X,character.Y,character.Floor,p[1],out var next,allowSwimming:true)
                            ||world.IsOccupied(character.Map,next.X,next.Y,next.Floor)
                            ||hub.IsOccupied(character.Map,next.X,next.Y,next.Floor,character.Id)){
                            await Send(GamePackets.Move(8,character.X,character.Y,1,character.Floor));break;
                        }
                        var previous=character;
                        if(SavePosition(previous,next.X,next.Y,p[1],floor:next.Floor) is not {} moved){
                            await Send(GamePackets.Move(8,previous.X,previous.Y,1,previous.Floor));break;
                        }
                        character=moved;presence!.Character=moved;
                        if((activeBankNpc!=null||bankLoginStage!=null)&&activeNpcDialog is uint tellerId&&
                            !world.InMap(moved.Map).Any(n=>n.Id==tellerId&&CanTalkTo(moved,n,20))){
                            bankLoginStage=null;
                            activeBankNpc=null;
                        }
                        guildFlagOffer=null;
                        if(GuildFlagUnder(moved) is {} steppedFlag){
                            // Retail opens the flag pursuit before acknowledging the step onto its north tile.
                            await OpenNpc(steppedFlag,false);
                        }
                        // The response carries the pre-step position/floor. The captured
                        // S0B movement tag is 1 regardless of C33 input mode.
                        var acknowledgement=GamePackets.Move(p[1],previous.X,previous.Y,1,previous.Floor);
                        var objectUpdates=MovedObjectPackets(presence,previous);
                        hub.AppendMovedPeerPackets(presence,previous,objectUpdates);
                        if(objectUpdates.Count==0)await Send(acknowledgement);
                        else{
                            objectUpdates.Add(acknowledgement);
                            await SendSnapshot(objectUpdates);
                        }
                        await InterruptGuildCapture(presence,"깃발 앞을 벗어났습니다.");
                        byte motion=p[2] is 1 or 2 or 3?p[2]:(byte)2;
                        await hub.BroadcastPeerStep(presence,previous,motion);
                        if(presence.Trade is {} movedTrade)await movedTrade.CheckRange();
                        if(presence.CraftStation is {} station&&!NearCraftStation(character,station))await CloseCraft();
                        break;
                    case 0x0B when role=="World"&&presence!=null&&p.Length==1:
                        await LeaveWorld();
                        await Send(GamePackets.Transfer(loginEndpoint,NewTransferTicket(null)));
                        transferPending=true;
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        break;
                    case 0x0B:return;
                    default:Console.WriteLine($"{role}/{id}: unimplemented C 0x{p[0]:X2}");break;
                }
                }finally{if(worldPacket)worldUpdateLock.Release();}
            }
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or OperationCanceledException or SocketException or InvalidDataException or DecoderFallbackException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException){Console.WriteLine($"{role}/{id}: {ex.GetType().Name}");}
        finally{
            timeout.Cancel();if(control!=null)await control;
            if(outgoingHandoff is {} handoff&&handoff.Expires-DateTimeOffset.UtcNow is {Ticks:>0} remaining){
                // The native client half-closes and waits for EOF before opening the new world socket.
                stream.Dispose();
                try{await handoff.Completion.Task.WaitAsync(remaining,stop.Token);}
                catch(Exception ex) when(ex is TimeoutException or OperationCanceledException){}
            }
            if(presence!=null){
                await worldUpdateLock.WaitAsync();
                try{
                    if(outgoingHandoff?.Completion.Task.IsCompletedSuccessfully!=true)await LeaveWorld();
                }finally{worldUpdateLock.Release();}
            }
            Console.WriteLine($"{role}/{id} closed");
        }
    }
}

sealed record WorldHandoff(WorldSession Session,DateTimeOffset Expires,bool FromPersonalRoom){
    public TaskCompletionSource Completion{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
}
