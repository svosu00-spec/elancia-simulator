using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ElanciaLocal;

public sealed record GroundItem(uint Id,int Map,int X,int Y,byte Floor,string Key,uint Quantity,ushort? Durability,
    ushort? BaitRemaining=null){
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? NativeDurability { get; init; }
}
public sealed record CashShopDelivery(uint OrderId,uint EntryId,string Key,uint Quantity=1,DateOnly? PurchasedOn=null){
    internal bool IsAvailable=>OrderId!=0&&EntryId!=0&&Quantity>0&&EquipmentItems.HasKey(Key)&&
        (Quantity==1||EquipmentItems.Get(Key).EquipmentSlot==0);
}
public sealed record FateAllocation(byte Warrior=0,byte Adventurer=0,byte Merchant=0){
    internal int Used=>Warrior+Adventurer+Merchant;
    internal byte For(int aptitude)=>aptitude switch{0=>Warrior,1=>Adventurer,_=>Merchant};
}

public sealed record Character(uint Id,string Account,string Name,string Appearance,int Map=0,int X=30,int Y=15,byte Direction=0,byte Floor=0,string? DisplayAppearance=null,ushort[]? Equipment=null,ItemPlacement[]? Items=null,uint Gold=1000,uint DamageTaken=0,decimal? MovementSpeed=null,Dictionary<byte,uint>? Abilities=null,long DaggerPoisonUntil=0,uint Satiety=WorldSettings.MaxSatiety,int AbilityCountsVersion=0,uint? CombatExperience=null,int? HomeTownMap=null,bool AwaitingRevival=false,uint[]? Friends=null,long RevivalDueAt=0,string? Job=null,GroundItem[]? GroundItems=null,byte WalletSlot=0,ushort WalletX=45,ushort WalletY=44,uint ManaSpent=0,ItemPlacement[]? BankItems=null,int? OranTownMap=null,bool PartyAllowed=true,string? GuildName=null,uint? GuildLeaderId=null,uint? Fame=null,long KayQuestExpiresAt=0,int[]? OwnedGuildHouses=null,Dictionary<string,long>? GatheringDeadlines=null){
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FateAllocation? FatePoints { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool LeonHired { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ItemPlacement[]? LeonItems { get; init; }
    public uint FishingPoints { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ushort GreatswordCounterReference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ushort UnarmedCounterReference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public uint BattlePoints { get; init; }
    public uint TrainingHp { get; init; }
    public uint TrainingMp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? InnateHp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? InnateMp { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public byte? Age { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public byte ArmorWearCarry { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? ThirdBaseStatusWord { get; init; }
    public byte HarpGrade { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string,byte>? SkillGrades { get; init; }
    public bool FishingLearned { get; init; }
    public Dictionary<string,ushort>? LifeMastery { get; init; }
    public Dictionary<string,byte>? JobPoints { get; init; }
    public GuardianProgress? Guardian { get; init; }
    public Dictionary<string,CalendarQuestProgress>? CalendarQuests { get; init; }
    public JobAttributes? JobStats { get; init; }
    public ushort? SenseMastery { get; init; }
    public uint? MeditationCounters { get; init; }
    public Dictionary<byte,uint>? GrepaSkillCounters { get; init; }
    public uint? ConversationCounters { get; init; }
    public bool DaraLearned { get; init; }
    public bool RimediumLearned { get; init; }
    public bool BraemaLearned { get; init; }
    public bool BrelLearned { get; init; }
    public bool SlowLearned { get; init; }
    public bool CurseLearned { get; init; }
    public bool BrisLearned { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<byte,byte>? SpellSlots { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<byte,ushort>? SpellCounters { get; init; }
    public long SlowUntil { get; init; }
    public long CurseUntil { get; init; }
    public MetiniStage MetiniProgress { get; init; }
    public byte MetiniRoosters { get; init; }
    public TorionStage TorionProgress { get; init; }
    public byte TorionKills { get; init; }
    public RanicheStage RanicheProgress { get; init; }
    public long ConversationRetryAt { get; init; }
    public BankPassword? BankPassword { get; init; }
    public RaddHuntProgress? RaddHunt { get; init; }
    public KondQuestStage KondStage { get; init; }
    public OldSmithStage OldSmithProgress { get; init; }
    public ShopRestock[]? PendingShopRestocks { get; init; }
    public long SpeedPotionUntil { get; init; }
    public long HealthPotionUntil { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long ElBlessingUntil { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CashShopDelivery[]? CashShopItems { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? StatusEffectBit0 { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? PersonalRoomExitMap { get; init; }
    internal long GatheringDeadline(GatheringQuest quest)=>quest==GatheringQuest.Kay
        ?KayQuestExpiresAt:GatheringDeadlines?.GetValueOrDefault(quest.Key)??0;
    internal Character WithGatheringDeadline(GatheringQuest quest,long deadline){
        if(quest==GatheringQuest.Kay)return this with {KayQuestExpiresAt=deadline};
        var deadlines=GatheringDeadlines is {} saved?new Dictionary<string,long>(saved):[];
        if(deadline==0)deadlines.Remove(quest.Key);
        else deadlines[quest.Key]=deadline;
        return this with {GatheringDeadlines=deadlines.Count==0?null:deadlines};
    }
    public bool DaggerPoisonActive()=>DaggerPoisonUntil>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public uint CurrentHp()=>CurrentHp(MaxHp(EquipmentItems.EquippedEffects(this)));
    internal uint BaseHp(IEnumerable<EquipmentEffect> effects)=>
        (uint)Math.Min(uint.MaxValue,(ulong)EquipmentItems.MaxHp(effects,InnateHp??100)+TrainingHp);
    internal uint MaxHp(IEnumerable<EquipmentEffect> effects){
        uint maximum=BaseHp(effects);
        return HealthPotionUntil>0?maximum+PotionBuffs.HealthBonus(maximum):maximum;
    }
    public uint CurrentHp(uint maxHp)=>AwaitingRevival?0:maxHp-Math.Min(DamageTaken,maxHp-1);
    internal uint MaxMp(IEnumerable<EquipmentEffect> effects)=>
        (uint)Math.Clamp((long)(InnateMp??100)+TrainingMp+effects.Sum(e=>e.Mp),0,uint.MaxValue);
    internal uint CurrentMp(uint maxMp)=>maxMp-Math.Min(ManaSpent,maxMp);
    internal IEnumerable<byte> FreeInventorySlots(IEnumerable<ItemPlacement> items)=>
        Enumerable.Range(1,WorldSettings.InventorySlots).Select(slot=>(byte)slot)
            .Where(slot=>slot!=WalletSlot&&items.All(item=>item.InventorySlot!=slot));
    internal Character ReserveWalletSlot(){
        bool Available(byte slot)=>slot is >=1 and <=WorldSettings.InventorySlots&&(Items??[]).All(item=>item.InventorySlot!=slot);
        if(Available(WalletSlot))return this;
        byte slot=Available(WorldSettings.DefaultWalletSlot)?WorldSettings.DefaultWalletSlot:FreeInventorySlots(Items??[]).LastOrDefault();
        return this with {WalletSlot=slot};
    }
    internal byte[] DisplayModel(){
        byte[] appearance=Convert.FromHexString(DisplayAppearance??Appearance[..12]);
        if(DisplayAppearance==null){
            // C03 stores the head before the body. S33 uses the reverse order.
            (appearance[0],appearance[4])=(appearance[4],appearance[0]);
            (appearance[1],appearance[5])=(appearance[5],appearance[1]);
        }
        return appearance;
    }
    internal byte TotalFatePoints()=>(byte)(CombatAbilities.Level(CombatAbilities.Experience(this))-1);
    internal byte AvailableFatePoints()=>(byte)Math.Max(0,TotalFatePoints()-(FatePoints?.Used??0));
    internal byte AptitudeOrder(){
        byte initial=Appearance.Length==24&&byte.TryParse(Appearance.AsSpan(22),
            System.Globalization.NumberStyles.HexNumber,null,out byte order)&&order is 6 or 9 or 18 or 24 or 33 or 36?order:(byte)6;
        if(FatePoints is not {Used:>0} allocation)return initial;
        int[] original=[initial>>4,(initial>>2)&3,initial&3];
        byte current=0;
        foreach(int aptitude in original.OrderByDescending(allocation.For))current=(byte)((current<<2)|aptitude);
        return current;
    }
}
public sealed class CharacterStore {
    const int CharacterLimit=10;
    readonly string path;
    readonly object gate=new();
    readonly StartLocation start;
    readonly AccountDatabase? database;
    List<Character> characters;
    public CharacterStore(string path,StartLocation? start=null):this(path,start,null){}
    internal CharacterStore(string path,StartLocation? start,AccountDatabase? database) {
        this.start=start??StartLocation.Rolancia;
        this.database=database;
        this.path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        var stored=database?.LoadCharacters(this.path)??(File.Exists(this.path)
            ?JsonSerializer.Deserialize<List<Character>>(File.ReadAllText(this.path))??[]:[]);
        characters=stored
            .Select(CombatAbilities.UpgradeCounters)
            .Select(c=>c is {InnateHp:40,InnateMp:20,UnarmedCounterReference:CombatAbilities.BeginnerUnarmedReference}
                ?c with {Age=c.Age??1,ThirdBaseStatusWord=c.ThirdBaseStatusWord??7}:c)
            .Select(Fishing.LearnEquipped)
            .Select(c=>c.ReserveWalletSlot())
            .Select(c=>c with {HomeTownMap=WorldSettings.HomeTown(c.HomeTownMap??this.start.Map)}).ToList();
    }
    internal Character[] All(){lock(gate)return characters.ToArray();}
    internal void EditManaged(uint id,string account,uint gold,ItemPlacement[] placements,ItemPlacement[] bank)=>Update(id,c=>
        (ApplyEquipment(c,placements) with {Account=account,Gold=gold,BankItems=bank}).ReserveWalletSlot());
    public static bool ValidName(string name)=>Regex.IsMatch(name,@"\A[A-Za-z0-9가-힣]{2,12}\z") && Encoding.GetEncoding(949).GetByteCount(name)<=24;
    public Character[] List(string account){lock(gate)return characters.Where(c=>c.Account==account).ToArray();}
    public Character? Find(string account,string name){lock(gate)return characters.SingleOrDefault(c=>c.Account==account&&c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));}
    public Character? Find(uint id){lock(gate)return characters.SingleOrDefault(c=>c.Id==id);}
    internal Character[] GuildMembers(Character member){
        if(member is not {GuildName:{},GuildLeaderId:>0})return [];
        lock(gate)return characters.Where(c=>c.GuildLeaderId==member.GuildLeaderId&&
            string.Equals(c.GuildName,member.GuildName,StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c=>c.Id==member.GuildLeaderId).ThenBy(c=>c.Id).ToArray();
    }
    internal Character? FindByName(string name){lock(gate)return characters.SingleOrDefault(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));}
    internal Character RestoreEquipmentAppearance(uint id)=>Update(id,c=>{
        ushort[]? appearance=null;
        foreach(var placement in c.Items??[]){
            if(placement.InventorySlot!=0)continue;
            var item=EquipmentItems.Get(placement.Key);
            if(item.AppearanceLayer is <0 or >=10||item.AppearanceWord==0||
                c.Equipment is {Length:10} saved&&saved[item.AppearanceLayer]==item.AppearanceWord)continue;
            appearance??=c.Equipment is {Length:10} words?words.ToArray():new ushort[10];
            appearance[item.AppearanceLayer]=item.AppearanceWord;
        }
        return appearance==null?c:c with {Equipment=appearance};
    });
    internal (Character Trainee,Character? Guardian,ItemPlacement? Badge,ItemPlacement? Pencil,GuardianResult Result)
        AppointGuardian(uint id,byte certificateSlot,string name){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            int recipient=characters.FindIndex(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(index<0)throw new InvalidDataException("Character missing");
            var trainee=characters[index];
            bool IsCertificate(ItemPlacement item)=>item.InventorySlot==certificateSlot&&item.Key==GuardianQuest.Certificate;
            if(recipient<0||recipient==index||trainee.AwaitingRevival||trainee.Guardian?.GuardianId!=null||
                NpcMaterials.Count(trainee,IsCertificate)==0)return (trainee,null,null,null,GuardianResult.Invalid);
            var guardian=characters[recipient];
            var remaining=NpcMaterials.Consume(trainee,IsCertificate,1);
            byte pencilSlot=trainee.FreeInventorySlots(remaining).FirstOrDefault();
            var guardianItems=guardian.Items??[];
            bool needsBadge=!guardianItems.Any(item=>item.Key==GuardianQuest.Badge);
            byte badgeSlot=needsBadge?guardian.FreeInventorySlots(guardianItems).FirstOrDefault():(byte)0;
            if(pencilSlot==0||needsBadge&&badgeSlot==0)return (trainee,null,null,null,GuardianResult.InventoryFull);
            var pencil=new ItemPlacement(GuardianQuest.Pencil,pencilSlot);
            ItemPlacement? badge=needsBadge?new(GuardianQuest.Badge,badgeSlot):null;
            var next=characters.ToList();
            next[index]=trainee with {Items=[..remaining,pencil],
                Guardian=(trainee.Guardian??new()) with {GuardianId=guardian.Id}};
            next[recipient]=guardian with {Items=badge==null?guardianItems:[..guardianItems,badge],
                Guardian=guardian.Guardian??new()};
            Save(next);
            return (next[index],next[recipient],badge,pencil,GuardianResult.Applied);
        }
    }
    internal (uint Pending,uint Points,uint Cost) GuardianBalance(uint id,DateTimeOffset now){
        lock(gate){
            var guardian=characters.Single(c=>c.Id==id);
            uint pending=(uint)characters.Where(c=>c.Guardian?.GuardianId==id).Sum(c=>(long)c.Guardian!.Pending);
            return (pending,guardian.Guardian?.Points??0,GuardianCost(WorldSettings.KoreanDay(now)));
        }
    }
    uint GuardianCost(int day)=>5+(uint)(characters.Where(c=>c.Guardian?.RewardDay==day)
        .Sum(c=>(long)c.Guardian!.RewardsToday)/100);
    internal (Character Character,uint Claimed) ClaimGuardianPoints(uint id){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            var guardian=characters[index];
            if(guardian.Guardian is not {} progress||guardian.AwaitingRevival||
                !(guardian.Items??[]).Any(i=>i.Key==GuardianQuest.Badge&&i.InventorySlot>0))return (guardian,0);
            uint amount=0;
            var next=characters.ToList();
            for(int i=0;i<next.Count;i++){
                if(next[i].Guardian is not {GuardianId:var sponsor,Pending:>0} trainee||sponsor!=id)continue;
                amount+=trainee.Pending;
                next[i]=next[i] with {Guardian=trainee with {Pending=0}};
            }
            if(amount==0)return (guardian,0);
            next[index]=guardian with {Guardian=progress with {Points=progress.Points+amount}};
            Save(next);
            return (next[index],amount);
        }
    }
    internal (Character Character,GuardianResult Result) RedeemGuardianReward(uint id,GuardianReward reward,DateTimeOffset now){
        var result=GuardianResult.Invalid;
        var character=Update(id,c=>{
            if(c.Guardian is not {} progress||c.AwaitingRevival||
                !(c.Items??[]).Any(i=>i.Key==GuardianQuest.Badge&&i.InventorySlot>0))return c;
            long time=now.ToUnixTimeMilliseconds(),until=now.AddSeconds(GuardianQuest.Duration).ToUnixTimeMilliseconds();
            if((reward switch {
                GuardianReward.Speed=>c.SpeedPotionUntil,
                GuardianReward.Strength=>progress.StrengthUntil,
                GuardianReward.Counters=>progress.CounterUntil,
                _=>long.MaxValue
            })>time){result=GuardianResult.AlreadyActive;return c;}
            int day=WorldSettings.KoreanDay(now);
            uint cost=GuardianCost(day);
            if(progress.Points<cost){result=GuardianResult.InsufficientPoints;return c;}
            progress=progress with {Points=progress.Points-cost,RewardDay=day,
                RewardsToday=progress.RewardDay==day?progress.RewardsToday+1:1};
            if(reward==GuardianReward.Strength)progress=progress with {StrengthUntil=until};
            if(reward==GuardianReward.Counters)progress=progress with {CounterUntil=until};
            result=GuardianResult.Applied;
            return c with {Guardian=progress,SpeedPotionUntil=reward==GuardianReward.Speed?until:c.SpeedPotionUntil};
        });
        return (character,result);
    }
    internal Character ExpireGuardianRewards(uint id,long now)=>Update(id,c=>{
        if(c.Guardian is not {} progress)return c;
        return c with {Guardian=progress with {
            StrengthUntil=progress.StrengthUntil<=now?0:progress.StrengthUntil,
            CounterUntil=progress.CounterUntil<=now?0:progress.CounterUntil}};
    });
    internal (Character Character,CalendarQuestResult Result,ItemPlacement? Reward)
        RecordCalendarQuest(uint id,CalendarQuest quest,bool claim,DateTimeOffset now){
        var result=CalendarQuestResult.NotReady;
        ItemPlacement? reward=null;
        var updated=Update(id,c=>{
            if(c.AwaitingRevival)return c;
            var progress=quest.Progress(c);
            if(progress.Claimed){result=CalendarQuestResult.Completed;return c;}
            var items=c.Items??[];
            if(claim){
                if(progress.Visits<CalendarQuest.RequiredVisits)return c;
                byte slot=c.FreeInventorySlots(items).FirstOrDefault();
                if(slot==0){result=CalendarQuestResult.InventoryFull;return c;}
                reward=new(quest.RingKey,slot,Durability:EquipmentItems.FullDurability);
                items=[..items,reward];
                progress=progress with {Claimed=true};
                result=CalendarQuestResult.Rewarded;
            }else{
                if(progress.Visits>=CalendarQuest.RequiredVisits)return c;
                int day=WorldSettings.KoreanDay(now);
                if(progress.LastVisit>=day){result=CalendarQuestResult.AlreadyVisited;return c;}
                if(quest.NeedsWater){
                    if(NpcMaterials.Count(c,CalendarQuest.IsWater)==0){result=CalendarQuestResult.MissingWater;return c;}
                    items=NpcMaterials.Consume(c,CalendarQuest.IsWater,1);
                }
                progress=new(day,(byte)(progress.Visits+1));
                result=CalendarQuestResult.Visited;
            }
            var quests=new Dictionary<string,CalendarQuestProgress>(c.CalendarQuests??[]){[quest.RingKey]=progress};
            return c with {Items=items,CalendarQuests=quests};
        });
        return (updated,result,reward);
    }
    internal Character SetFriend(uint id,uint friendId,bool add)=>Update(id,c=>{
        var friends=c.Friends??[];
        return add==friends.Contains(friendId)?c:c with {Friends=add?[..friends,friendId]:friends.Where(friend=>friend!=friendId).ToArray()};
    });
    public bool Available(string name){lock(gate)return ValidName(name)&&!characters.Any(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase));}
    public Character? Create(string account,string name,byte[] appearance) {
        lock(gate) {
            if(!Available(name)||appearance.Length!=12||characters.Count(c=>c.Account==account)>=CharacterLimit)return null;
            uint id=characters.Count==0?1:checked(characters.Max(c=>c.Id)+1);
            var c=new Character(id,account,name,Convert.ToHexString(appearance),start.Map,start.X,start.Y,Floor:start.Floor,
                AbilityCountsVersion:CombatAbilities.CounterVersion,HomeTownMap:WorldSettings.HomeTown(start.Map),
                WalletSlot:WorldSettings.DefaultWalletSlot){
                    InnateHp=40,InnateMp=20,UnarmedCounterReference=CombatAbilities.BeginnerUnarmedReference,
                    Age=1,ThirdBaseStatusWord=7
                };
            Save([..characters,c]);return c;
        }
    }
    public Character SetLook(uint id,AppearanceProfile? profile){
        profile?.Validate();
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);if(index<0)throw new InvalidDataException("Character missing");
            var next=characters.ToList();
            next[index]=EquipmentItems.ApplyAppearance(next[index] with {DisplayAppearance=profile?.Appearance,Equipment=profile?.Equipment.ToArray()});
            Save(next);return next[index];
        }
    }
    public Character SetItems(uint id,ItemPlacement[] placements,StartLocation? destination=null,uint? gold=null,long? poisonUntil=null,
        (ushort X,ushort Y)? walletPosition=null){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            if(gold is uint balance)ValidateWalletIncrease(characters[index].Gold,balance);
            var next=characters.ToList();
            next[index]=ApplyEquipment(next[index],placements.ToArray());
            if(destination!=null)
                next[index]=next[index] with { Map=destination.Map,X=destination.X,Y=destination.Y,Floor=destination.Floor,
                    PersonalRoomExitMap=destination.Map<0?next[index].PersonalRoomExitMap:null };
            if(gold.HasValue)next[index]=(next[index] with {Gold=gold.Value}).ReserveWalletSlot();
            if(walletPosition is {} wallet)next[index]=next[index] with {WalletX=wallet.X,WalletY=wallet.Y};
            if(poisonUntil.HasValue)next[index]=next[index] with { DaggerPoisonUntil=poisonUntil.Value };
            Save(next);
            return next[index];
        }
    }
    internal LocalNpc WithShopStock(LocalNpc merchant,long now){
        lock(gate){
            var pending=MerchantPending(merchant,now);
            return merchant with {Shop=merchant.Shop?.Select(entry=>!MerchantRestock.HasFixedStock(merchant,entry)&&MerchantRestock.Capacity(merchant,entry) is byte capacity
                ?entry with {Flags=(byte)Math.Max(0,capacity-pending[MerchantRestock.StockKey(merchant,entry)].Sum(restock=>restock.Quantity))}:entry).ToArray()};
        }
    }
    internal LocalNpc RefreshShopStock(LocalNpc merchant,long now,IEnumerable<string>? purchased=null){
        var replenishing=(merchant.Shop??[]).Where(entry=>MerchantRestock.ReplenishesOnCatalog(merchant,entry))
            .Select(entry=>entry.Key).Except(purchased??[]).ToHashSet();
        lock(gate){
            List<Character>? next=null;
            for(int index=0;index<characters.Count&&replenishing.Count>0;index++){
                var buyer=characters[index];
                if(buyer.PendingShopRestocks is not {} restocks)continue;
                int before=replenishing.Count;
                var remaining=restocks.Select(row=>row.Map==merchant.Map&&row.Merchant==merchant.Name&&
                    row.MerchantId==0&&row.EntryId==0&&row.Due>now&&row.Quantity>0&&replenishing.Remove(row.Item)
                    ?row with {Quantity=row.Quantity-1}:row).Where(row=>row.Quantity>0).ToArray();
                if(before==replenishing.Count)continue;
                next??=characters.ToList();
                next[index]=buyer with {PendingShopRestocks=remaining.Length==0?null:remaining};
            }
            if(next!=null)Save(next);
            return WithShopStock(merchant,now);
        }
    }
    ILookup<(string Item,uint EntryId),ShopRestock> MerchantPending(LocalNpc merchant,long now)=>characters
        .SelectMany(character=>character.PendingShopRestocks??[])
        .Where(restock=>restock.Map==merchant.Map&&restock.Due>now&&
            (merchant.Category==2?restock.MerchantId==merchant.Id:restock.MerchantId==0&&restock.Merchant==merchant.Name))
        .ToLookup(restock=>(restock.Item,restock.EntryId));
    internal Character? PurchaseShopItems(uint id,ItemPlacement[] placements,uint cost,LocalNpc merchant,
        IEnumerable<(ShopEntry Entry,int Quantity)> order,long now,(ushort X,ushort Y)? refundWalletPosition=null){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            var current=characters[index];
            if(cost>current.Gold)return null;
            var existing=MerchantPending(merchant,now);
            var restocks=(current.PendingShopRestocks??[]).Where(restock=>restock.Due>now).ToList();
            foreach(var (entry,quantity) in order){
                if(MerchantRestock.Capacity(merchant,entry) is not byte capacity)continue;
                if(MerchantRestock.HasFixedStock(merchant,entry)){
                    if(quantity>capacity)return null;
                    continue;
                }
                var pending=existing[MerchantRestock.StockKey(merchant,entry)];
                if(quantity>capacity-pending.Sum(restock=>restock.Quantity))return null;
                if(merchant.Category==2){
                    restocks.Add(new(merchant.Map,merchant.Name,entry.Key,long.MaxValue,quantity,merchant.Id,entry.Id));
                }else if(MerchantRestock.IsLuckyPaper(merchant,entry)){
                    long due=pending.Select(restock=>restock.Due).DefaultIfEmpty(now).Max();
                    for(int unit=0;unit<quantity;unit++){
                        due+=MerchantRestock.IntervalMs;
                        restocks.Add(new(merchant.Map,merchant.Name,entry.Key,due,1));
                    }
                }else restocks.Add(new(merchant.Map,merchant.Name,entry.Key,now+MerchantRestock.IntervalMs,quantity));
            }
            var updated=ApplyEquipment(current,placements) with {Gold=current.Gold-cost,
                PendingShopRestocks=restocks.Count==0?null:restocks.ToArray()};
            if(refundWalletPosition is {} wallet)updated=updated with {WalletX=wallet.X,WalletY=wallet.Y};
            var next=characters.ToList();
            next[index]=updated.ReserveWalletSlot();
            Save(next);
            return next[index];
        }
    }
    public Character SetHealth(uint id,uint damageTaken,ItemPlacement[]? items=null,uint? satiety=null,uint? manaSpent=null){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            var next=characters.ToList();
            var current=next[index];
            next[index]=current with {DamageTaken=damageTaken,Items=items??current.Items,Satiety=satiety??current.Satiety,
                ManaSpent=manaSpent??current.ManaSpent};
            Save(next);
            return next[index];
        }
    }
    internal Character StartPotionBuff(uint id,PotionBuff buff,ItemPlacement[] items,long now)=>Update(id,c=>{
        if(PotionBuffs.Until(c,buff)>0||c.AwaitingRevival)return c;
        long until=now+PotionBuffs.DurationMs;
        if(buff==PotionBuff.Speed)return c with {Items=items,SpeedPotionUntil=until};
        uint maximum=c.BaseHp(EquipmentItems.EquippedEffects(c));
        uint damage=maximum-c.CurrentHp(maximum)+PotionBuffs.HealthBonus(maximum);
        return c with {Items=items,HealthPotionUntil=until,DamageTaken=damage};
    });
    internal Character ExpirePotionBuffs(uint id,long now)=>Update(id,c=>{
        if(c.SpeedPotionUntil>0&&c.SpeedPotionUntil<=now)c=c with {SpeedPotionUntil=0};
        if(c.ElBlessingUntil>0&&c.ElBlessingUntil<=now)c=c with {ElBlessingUntil=0};
        if(c.HealthPotionUntil>0&&c.HealthPotionUntil<=now){
            uint maximum=c.BaseHp(EquipmentItems.EquippedEffects(c));
            uint damage=c.DamageTaken-Math.Min(c.DamageTaken,PotionBuffs.HealthBonus(maximum));
            c=c with {HealthPotionUntil=0,DamageTaken=damage};
        }
        return c;
    });
    internal Character StartElBlessing(uint id,long now)=>Update(id,c=>c.AwaitingRevival||c.ElBlessingUntil>now?c:
        c with {ElBlessingUntil=now+ElBlessing.DurationSeconds*1000L});
    internal (Character Character,ItemPlacement? Granted) ClaimCashShopItem(uint id,uint orderId,uint entryId,long now){
        ItemPlacement? granted=null;
        var updated=Update(id,c=>{
            var deliveries=c.CashShopItems??[];
            var matches=deliveries.Where(row=>row.OrderId==orderId&&row.EntryId==entryId&&row.IsAvailable).Take(2).ToArray();
            if(c.AwaitingRevival||matches.Length!=1)return c;
            var delivery=matches[0];
            var items=c.Items??[];
            byte slot=c.FreeInventorySlots(items).FirstOrDefault();
            if(slot==0)return c;
            var placement=new ItemPlacement(delivery.Key,slot,delivery.Quantity,(ushort)(25+(slot-1)%10*20),(ushort)(15+(slot-1)/10*16)){
                ExpiresAt=ElBlessing.NewItemDeadline(delivery.Key,now)};
            granted=placement;
            var remaining=deliveries.Where(row=>row!=delivery).ToArray();
            return c with {Items=[..items,placement],CashShopItems=remaining.Length==0?null:remaining};
        });
        return (updated,granted);
    }
    internal (Character Character,ItemPlacement? Unwrapped) UnwrapElBlessing(uint id,byte slot,long now){
        ItemPlacement? unwrapped=null;
        var updated=Update(id,c=>{
            var items=c.Items??[];
            var package=items.SingleOrDefault(item=>item.InventorySlot==slot&&item.Key==ElBlessing.WrappedItemKey);
            if(c.AwaitingRevival||package is not {Quantity:>0})return c;
            byte destination=package.Quantity==1?slot:c.FreeInventorySlots(items).FirstOrDefault();
            if(destination==0)return c;
            ushort? x=destination==slot?package.InventoryWord1:(ushort)(25+(destination-1)%10*20);
            ushort? y=destination==slot?package.InventoryWord2:(ushort)(15+(destination-1)/10*16);
            var potion=new ItemPlacement(ElBlessing.ItemKey,destination,InventoryWord1:x,InventoryWord2:y){
                ExpiresAt=ElBlessing.NewItemDeadline(ElBlessing.ItemKey,now)};
            unwrapped=potion;
            if(destination==slot)return c with {Items=items.Select(item=>item==package?potion:item).ToArray()};
            return c with {Items=[..items.Select(item=>item==package?item with {Quantity=item.Quantity-1}:item),potion]};
        });
        return (updated,unwrapped);
    }
    internal static bool TimedItemNeedsUpdate(ItemPlacement item,long now)=>
        item.IsExpired(now)||item.ExpiresAt==null&&item.Key==ElBlessing.ItemKey;
    internal (Character Character,ItemPlacement[] Bag,ItemPlacement[] Bank,ItemPlacement[] Leon) UpdateTimedItems(uint id,long now){
        ItemPlacement[] bag=[],bank=[],leon=[];
        ItemPlacement[]? UpdateItems(ItemPlacement[]? items,out ItemPlacement[] expired){
            expired=[];
            if(items==null||!items.Any(item=>TimedItemNeedsUpdate(item,now)))return items;
            expired=items.Where(item=>item.IsExpired(now)).ToArray();
            return items.Where(item=>!item.IsExpired(now)).Select(item=>item.ExpiresAt==null&&item.Key==ElBlessing.ItemKey
                ?item with {ExpiresAt=ElBlessing.NewItemDeadline(item.Key,now)}:item).ToArray();
        }
        var updated=Update(id,c=>c with {Items=UpdateItems(c.Items,out bag),BankItems=UpdateItems(c.BankItems,out bank),
            LeonItems=UpdateItems(c.LeonItems,out leon)});
        return (updated,bag,bank,leon);
    }
    internal (uint OwnerId,GroundItem Item)[] GroundItems(){
        lock(gate)return characters.SelectMany(c=>(c.GroundItems??[]).Select(item=>(c.Id,item))).ToArray();
    }
    internal Character PutOnGround(uint id,ItemPlacement source,GroundItem item)=>Update(id,c=>{
        var inventory=(c.Items??[]).ToList();
        int index=inventory.IndexOf(source);
        if(index<0)throw new InvalidDataException("Drop source is no longer in inventory");
        if(item.Quantity==source.Quantity)inventory.RemoveAt(index);
        else inventory[index]=source with {Quantity=source.Quantity-item.Quantity};
        return c with {Items=inventory.ToArray(),GroundItems=[..c.GroundItems??[],item]};
    });
    internal Character ExpireGroundItem(uint ownerId,uint itemId,long now)=>Update(ownerId,c=>{
        if(c.GroundItems is not {} drops||!drops.Any(item=>item.Id==itemId&&item.ExpiresAt is long deadline&&deadline<=now))return c;
        var remaining=drops.Where(item=>item.Id!=itemId).ToArray();
        return c with {GroundItems=remaining.Length==0?null:remaining};
    });
    internal Character PutGoldOnGround(uint id,GroundItem item)=>Update(id,c=>
        c with {Gold=c.Gold-item.Quantity,GroundItems=[..c.GroundItems??[],item]});
    internal Character RelocateGroundDrop(uint ownerId,uint itemId,int x,int y)=>Update(ownerId,c=>{
        var drops=c.GroundItems??[];
        int index=Array.FindIndex(drops,item=>item.Id==itemId);
        if(index<0)throw new InvalidDataException("Drop is no longer on the ground");
        var updated=drops.ToArray();
        updated[index]=updated[index] with {X=x,Y=y};
        return c with {GroundItems=updated};
    });
    internal Character MoveWallet(uint id,ushort x,ushort y)=>Update(id,c=>c with {WalletX=x,WalletY=y});
    internal Character TakeFromGround(uint id,uint ownerId,uint itemId,ItemPlacement? placement,uint? gold=null){
        lock(gate){
            int picker=characters.FindIndex(c=>c.Id==id),owner=characters.FindIndex(c=>c.Id==ownerId);
            if(picker<0||owner<0||characters[owner].GroundItems is not {} items||!items.Any(item=>item.Id==itemId))
                throw new InvalidDataException("Drop is no longer on the ground");
            if(gold is uint balance)ValidateWalletIncrease(characters[picker].Gold,balance);
            var next=characters.ToList();
            var remaining=items.Where(item=>item.Id!=itemId).ToArray();
            next[owner]=next[owner] with {GroundItems=remaining.Length==0?null:remaining};
            if(placement!=null)next[picker]=ApplyEquipment(next[picker],[..next[picker].Items??[],placement]);
            if(gold.HasValue)next[picker]=next[picker] with {Gold=gold.Value};
            Save(next);
            return next[picker];
        }
    }
    static void ValidateWalletIncrease(uint previous,uint balance){
        if(balance>previous&&!WorldSettings.CanCreditGold(previous,balance-previous))
            throw new InvalidDataException(WorldSettings.GoldLimitMessage);
    }
    internal Character KnockOut(uint id)=>Update(id,Downed);
    internal Character RecordMonsterDamage(uint id,ushort damage,bool physical)=>Update(id,c=>{
        if(damage==0||c.AwaitingRevival)return c;
        var injured=damage>=c.CurrentHp()?Downed(c):c with {DamageTaken=c.DamageTaken+damage};
        return physical?EquipmentItems.WearArmorFromMonsterHit(injured,damage):injured;
    });
    static Character Downed(Character c)=>c with {
        AwaitingRevival=true,DaggerPoisonUntil=0,
        RevivalDueAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+WorldSettings.RevivalDelayMs
    };
    internal (Character Attacker,Character Victim) RecordDuelHit(uint attackerId,uint victimId,ushort damage,ItemPlacement[]? placements){
        lock(gate){
            int attacker=characters.FindIndex(c=>c.Id==attackerId),victim=characters.FindIndex(c=>c.Id==victimId);
            if(attacker<0||victim<0||attacker==victim)throw new InvalidDataException("Invalid duel participants");
            var next=characters.ToList();
            var target=next[victim];
            if(target.AwaitingRevival)return (next[attacker],target);
            next[victim]=damage>=target.CurrentHp()
                ?Downed(target) with {Items=EquipmentItems.WearArmorOnDuelDefeat(target.Items)}
                :target with {DamageTaken=target.DamageTaken+damage};
            if(placements!=null)next[attacker]=ApplyEquipment(next[attacker],placements);
            Save(next);
            return (next[attacker],next[victim]);
        }
    }
    internal Character Revive(uint id,StartLocation shrine)=>Update(id,c=>{
        var effects=EquipmentItems.EquippedEffects(c).ToArray();
        uint maxHp=c.MaxHp(effects),maxMp=c.MaxMp(effects);
        uint restoredHp=Math.Min(maxHp,Math.Max(1,c.BaseHp([])/10));
        return c with {Map=shrine.Map,X=shrine.X,Y=shrine.Y,Floor=shrine.Floor,AwaitingRevival=false,RevivalDueAt=0,ElBlessingUntil=0,
            DamageTaken=maxHp-restoredHp,ManaSpent=maxMp-maxMp/10};
    });
    public (Character First,Character Second) ExchangeItems(uint firstId,ItemPlacement[] firstItems,uint secondId,ItemPlacement[] secondItems){
        lock(gate){
            int first=characters.FindIndex(c=>c.Id==firstId), second=characters.FindIndex(c=>c.Id==secondId);
            if(first<0||second<0||first==second)throw new InvalidDataException("Invalid exchange participants");
            var next=characters.ToList();
            next[first]=next[first] with {Items=firstItems};
            next[second]=next[second] with {Items=secondItems};
            Save(next);
            return (next[first],next[second]);
        }
    }
    public Character UpdatePosition(uint id,int x,int y,byte direction,int? map=null,byte? floor=null) {
        lock(gate) {
            int index=characters.FindIndex(c=>c.Id==id);if(index<0)throw new InvalidDataException("Character missing");
            var current=characters[index];
            int targetMap=map??current.Map;
            var next=characters.ToList();
            next[index]=current with {X=x,Y=y,Direction=direction,Map=targetMap,Floor=floor??current.Floor,
                PersonalRoomExitMap=targetMap<0?current.PersonalRoomExitMap:null};
            Save(next);
            return next[index];
        }
    }
    internal Character EnterPersonalRoom(uint id,StartLocation arrival,int exitMap,ItemPlacement[]? inventory=null)=>Update(id,c=>c with {
        Map=arrival.Map,X=arrival.X,Y=arrival.Y,Floor=arrival.Floor,PersonalRoomExitMap=exitMap,
        Items=inventory??c.Items
    });
    public Character SetMovementSpeed(uint id,decimal speed)=>Update(id,c=>c with {MovementSpeed=speed});
    internal Character PayNpcTravel(uint id,StartLocation to,uint fee)=>Update(id,c=>
        c.Gold<fee?c:(c with {Map=to.Map,X=to.X,Y=to.Y,Floor=to.Floor,Gold=c.Gold-fee}).ReserveWalletSlot());
    internal (Character Character,JobChangeResult Result) ChangeJob(uint id,ShrineJob? job,uint resignationFee=0){
        JobChangeResult result=JobChangeResult.AlreadySelected;
        var updated=Update(id,c=>{
            if(c.Job==job?.Name)return c;
            if(job!=null&&!job.Qualifies(c)){result=JobChangeResult.SkillRequired;return c;}
            uint offering=job?.Fee(c)??resignationFee;
            if(c.Gold<offering){result=JobChangeResult.InsufficientGold;return c;}
            result=JobChangeResult.Changed;
            var points=new Dictionary<string,byte>(c.JobPoints??[]);
            if(c.Job is {} previous)points.TryAdd(previous,0);
            if(job!=null)points.TryAdd(job.Name,0);
            return c with {Job=job?.Name,JobPoints=points,Gold=c.Gold-offering};
        });
        return (updated,result);
    }
    internal (Character Character,bool Applied) ChargeStartingBattlePoints(uint id){
        bool applied=false;
        var updated=Update(id,c=>{
            if(c.BattlePoints!=0||c.Gold<CastleOffice.BattleChargeFee)return c;
            applied=true;
            return c with {BattlePoints=CastleOffice.StartingBattlePoints,Gold=c.Gold-CastleOffice.BattleChargeFee};
        });
        return (updated,applied);
    }
    internal Character SaveBankMove(uint id,StorageMove move)=>Update(id,c=>c with {Items=move.Inventory,BankItems=move.Storage});
    internal Character SaveLeonMove(uint id,StorageMove move)=>Update(id,c=>c with {Items=move.Inventory,LeonItems=move.Storage});
    internal Character SetBankPassword(uint id,BankPassword password)=>Update(id,c=>
        c.BankPassword==null?c with {BankPassword=password}:c);
    internal Character TogglePartyAllowed(uint id)=>Update(id,c=>c with {PartyAllowed=!c.PartyAllowed});
    internal Character ToggleStatusEffectBit0(uint id)=>Update(id,c=>c with {StatusEffectBit0=!(c.StatusEffectBit0??true)});
    internal bool IsGuildNameTaken(string name){
        lock(gate)return characters.Any(c=>string.Equals(c.GuildName,name,StringComparison.OrdinalIgnoreCase));
    }
    internal (GuildRegistration Result,Character[] Members) RegisterGuild(uint leaderId,uint firstId,uint secondId,string name){
        lock(gate){
            uint[] ids=[leaderId,firstId,secondId];
            var members=ids.Select(id=>characters.FindIndex(c=>c.Id==id)).ToArray();
            if(ids.Distinct().Count()!=3||members.Any(index=>index<0))return (GuildRegistration.MissingMembers,[]);
            if(characters.Any(c=>string.Equals(c.GuildName,name,StringComparison.OrdinalIgnoreCase)))
                return (GuildRegistration.NameTaken,[]);
            if(members.Any(index=>characters[index].GuildName!=null))return (GuildRegistration.AlreadyMember,[]);
            if(characters[members[0]].Gold<WorldSettings.GuildFoundingCost)return (GuildRegistration.InsufficientGold,[]);
            var next=characters.ToList();
            foreach(int index in members)next[index]=next[index] with {GuildName=name,GuildLeaderId=leaderId};
            next[members[0]]=next[members[0]] with {Gold=next[members[0]].Gold-WorldSettings.GuildFoundingCost};
            Save(next);
            return (GuildRegistration.Created,members.Select(index=>next[index]).ToArray());
        }
    }
    internal Character LeaveGuild(uint id)=>Update(id,c=>
        c.GuildName==null||c.GuildLeaderId==id||c.Gold<WorldSettings.GuildDepartureCost?c:
            c with {GuildName=null,GuildLeaderId=null,Gold=c.Gold-WorldSettings.GuildDepartureCost});
    internal string? GuildHouseOwner(int map){
        lock(gate)return characters.FirstOrDefault(c=>GuildHouseCapture.IsGuildMaster(c)&&
            c.OwnedGuildHouses?.Contains(map)==true)?.GuildName;
    }
    internal Character PayGuildHouseFee(uint id,int map)=>Update(id,c=>
        c.Map==map&&WorldSettings.IsGuildHouse(map)&&GuildHouseCapture.IsGuildMaster(c)&&!c.AwaitingRevival&&
        c.Gold>=GuildHouseCapture.Fee&&c.OwnedGuildHouses?.Contains(map)!=true
            ?c with {Gold=c.Gold-GuildHouseCapture.Fee}:c);
    internal Character? CaptureGuildHouse(uint id,StartLocation post){
        lock(gate){
            int winner=characters.FindIndex(c=>c.Id==id);
            if(winner<0)return null;
            var current=characters[winner];
            if(!WorldSettings.IsGuildHouse(post.Map)||current.Map!=post.Map||current.X!=post.X||
                current.Y!=post.Y||current.Floor!=post.Floor||current.AwaitingRevival||
                !GuildHouseCapture.IsGuildMaster(current)||current.OwnedGuildHouses?.Contains(post.Map)==true||
                !WorldSettings.CanCreditGold(current.Gold,GuildHouseCapture.Refund))return null;
            var next=characters.Select(c=>c.OwnedGuildHouses?.Contains(post.Map)==true
                ?c with {OwnedGuildHouses=c.OwnedGuildHouses.Where(map=>map!=post.Map).ToArray()}:c).ToList();
            next[winner]=(current with {OwnedGuildHouses=[..current.OwnedGuildHouses??[],post.Map],
                Gold=current.Gold+GuildHouseCapture.Refund}).ReserveWalletSlot();
            Save(next);
            return next[winner];
        }
    }
    internal Character RegisterRolancia(uint id)=>Update(id,c=>
        c.OranTownMap==2||c.Gold<WorldSettings.RolanciaCitizenshipCost?c:
            c with {OranTownMap=2,Gold=c.Gold-WorldSettings.RolanciaCitizenshipCost});
    internal (Character Character,SenseEnrollment Result) LearnSense(uint id){
        var result=SenseEnrollment.AlreadyLearned;
        var current=Update(id,c=>{
            if(c.SenseMastery!=null)return c;
            result=SenseEnrollment.Ineligible;
            if(!SenseTraining.Eligible(c))return c;
            result=SenseEnrollment.InsufficientGold;
            if(c.Gold<SenseTraining.Price)return c;
            result=SenseEnrollment.Learned;
            return (c with {SenseMastery=0,Gold=c.Gold-SenseTraining.Price}).ReserveWalletSlot();
        });
        return (current,result);
    }
    internal Character TrainSense(uint id)=>Update(id,c=>c.SenseMastery is ushort mastery&&mastery<ShrineJob.LifeLimit(c,ShrineJob.SenseSkill)
        ?ShrineJob.AwardLife(c with {SenseMastery=(ushort)(mastery+1)},ShrineJob.SenseSkill,mastery,mastery+1):c);
    internal (Character Character,MeditationEnrollment Result) LearnMeditation(uint id){
        var result=MeditationEnrollment.AlreadyLearned;
        var updated=Update(id,c=>{
            if(c.MeditationCounters.HasValue)return c;
            result=MeditationEnrollment.LowInt;
            if(MeditationTraining.PureIntelligence(c)<15)return c;
            result=MeditationEnrollment.WrongJob;
            if(c.Job!="악사")return c;
            result=MeditationEnrollment.InsufficientGold;
            if(c.Gold<MeditationTraining.Price)return c;
            result=MeditationEnrollment.Learned;
            return c with {MeditationCounters=0,Gold=c.Gold-MeditationTraining.Price};
        });
        return (updated,result);
    }
    internal Character Meditate(uint id)=>Update(id,c=>{
        if(c.MeditationCounters is not uint counters||c.AwaitingRevival)return c;
        ushort before=MeditationTraining.Mastery(counters);
        uint next=before<ShrineJob.LifeLimit(c,MeditationTraining.Name)?counters+1:counters;
        ushort after=MeditationTraining.Mastery(next);
        var updated=c with {MeditationCounters=next,ManaSpent=c.ManaSpent-Math.Min(c.ManaSpent,MeditationTraining.ManaRecovery(before))};
        if(after>before)updated=AwardNonCombatMastery(updated);
        return ShrineJob.AwardLife(updated,MeditationTraining.Name,before,after);
    });
    static Character AwardNonCombatMastery(Character character)=>GuardianQuest.Accrue(character,character with {
        CombatExperience=(uint)Math.Min(uint.MaxValue,(ulong)CombatAbilities.Experience(character)+1),
        Fame=CombatAbilities.Fame(character)
    });
    internal Character FinishConversationExam(uint id,bool passed,DateTimeOffset now)=>Update(id,c=>
        c.ConversationCounters.HasValue||!ConversationTraining.Eligible(c)||c.ConversationRetryAt>now.ToUnixTimeMilliseconds()?c:
        passed?c with {ConversationCounters=0,ConversationRetryAt=0}:
        c with {ConversationRetryAt=now.Add(ConversationTraining.RetryDelay).ToUnixTimeMilliseconds()});
    internal (Character Caster,Character[] Recovered) SpeakToNearby(uint id,uint[] listeners){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            var caster=characters[index];
            if(caster.ConversationCounters is not uint points||caster.AwaitingRevival)return (caster,[]);
            var nearby=characters.Select((c,i)=>(c,i)).Where(entry=>entry.c.Id!=id&&listeners.Contains(entry.c.Id)&&
                !entry.c.AwaitingRevival&&WorldHub.CanSee(caster,entry.c)&&WorldHub.CanSee(entry.c,caster)).ToArray();
            if(nearby.Length==0)return (caster,[]);
            byte rank=ShrineGrade.Conversation.Rank(caster);
            ushort mastery=ConversationTraining.Mastery(points,rank);
            uint amount=1u+(uint)mastery/100;
            var next=characters.ToList();
            var recovered=new List<Character>();
            foreach(var (listener,i) in nearby){
                if(listener.DamageTaken==0&&listener.ManaSpent==0)continue;
                bool heal=listener.DamageTaken>0&&(listener.ManaSpent==0||Random.Shared.Next(2)==0);
                next[i]=heal?listener with {DamageTaken=listener.DamageTaken-Math.Min(listener.DamageTaken,amount)}:
                    listener with {ManaSpent=listener.ManaSpent-Math.Min(listener.ManaSpent,amount)};
                recovered.Add(next[i]);
            }
            if(mastery<ShrineJob.LifeLimit(caster,ConversationTraining.Name)){
                ushort trained=ConversationTraining.Mastery(points+1,rank);
                var updated=caster with {ConversationCounters=points+1};
                if(trained>mastery)updated=AwardNonCombatMastery(updated);
                next[index]=ShrineJob.AwardLife(updated,ConversationTraining.Name,mastery,trained);
            }
            if(next[index]!=caster||recovered.Count>0)Save(next);
            return (next[index],recovered.ToArray());
        }
    }
    internal (Character Character,string? Refusal) LearnGrepaSkill(uint id,GrepaCombatSkill skill){
        string? refusal=null;
        var updated=Update(id,c=>{
            refusal=skill.Refusal(c);
            if(refusal!=null)return c;
            var skills=new Dictionary<byte,uint>(c.GrepaSkillCounters??[]){[skill.Slot]=0};
            return c with {GrepaSkillCounters=skills,Gold=c.Gold-skill.Price};
        });
        return (updated,refusal);
    }
    internal Character TrainGrepaSkill(uint id,GrepaCombatSkill skill,int targetLimit)=>Update(id,c=>{
        if(skill.Counters(c) is not uint points||c.AwaitingRevival||
            GrepaCombatSkill.Mastery(points)>=Math.Min(targetLimit,ShrineJob.LifeLimit(c,skill.Name)))return c;
        var skills=new Dictionary<byte,uint>(c.GrepaSkillCounters!){[skill.Slot]=points+1};
        return ShrineJob.AwardLife(c with {GrepaSkillCounters=skills},skill.Name,
            GrepaCombatSkill.Mastery(points),GrepaCombatSkill.Mastery(points+1));
    });
    internal Character AcceptGatheringQuest(uint id,GatheringQuest quest,DateTimeOffset now)=>Update(id,c=>
        c.GatheringDeadline(quest)>now.ToUnixTimeMilliseconds()?c:
            c.WithGatheringDeadline(quest,now.AddHours(quest.Hours).ToUnixTimeMilliseconds()));
    internal (Character Character,GatheringTurnIn Result) CompleteGatheringQuest(uint id,GatheringQuest quest,DateTimeOffset now){
        GatheringTurnIn result=GatheringTurnIn.Expired;
        var current=Update(id,c=>{
            if(c.GatheringDeadline(quest)<=now.ToUnixTimeMilliseconds())return c;
            result=GatheringTurnIn.MissingItems;
            if(quest.Count(c)<quest.Required)return c;
            result=GatheringTurnIn.WalletFull;
            if(!WorldSettings.CanCreditGold(c.Gold,quest.Reward))return c;
            var remaining=NpcMaterials.Consume(c,quest.IsMaterial,quest.Required);
            var updated=(c with {Items=remaining,Gold=c.Gold+quest.Reward}).WithGatheringDeadline(quest,0).ReserveWalletSlot();
            if(updated.WalletSlot==0)return c;
            result=GatheringTurnIn.Completed;
            return updated;
        });
        return (current,result);
    }
    internal Character AllocateFatePoint(uint id,byte choice)=>Update(id,c=>{
        if(choice is <1 or >3||c.AwaitingRevival||!ShrineDialog.InRange(c)||c.AvailableFatePoints()==0)return c;
        var points=c.FatePoints??new();
        return c with {FatePoints=choice switch{
            1=>points with {Warrior=(byte)(points.Warrior+1)},
            2=>points with {Adventurer=(byte)(points.Adventurer+1)},
            _=>points with {Merchant=(byte)(points.Merchant+1)}
        }};
    });
    internal (Character Buyer,ItemPlacement? Deed) BuyLeonDeed(uint id){
        ItemPlacement? deed=null;
        var buyer=Update(id,c=>{
            if(c.AwaitingRevival||c.Gold<MercenaryHiring.Price)return c;
            var items=c.Items??[];
            byte slot=c.FreeInventorySlots(items).FirstOrDefault();
            if(slot==0)return c;
            deed=new(MercenaryHiring.LeonDeed,slot,1,(ushort)Random.Shared.Next(25,226),(ushort)Random.Shared.Next(15,86));
            return (c with {Gold=c.Gold-MercenaryHiring.Price,Items=[..items,deed]}).ReserveWalletSlot();
        });
        return (buyer,deed);
    }
    internal Character? ConsumeLeonSummonItem(uint id,byte slot,bool hire){
        string key=hire?MercenaryHiring.LeonDeed:MercenaryHiring.SummonScroll;
        bool consumed=false;
        var owner=Update(id,c=>{
            if(c.LeonHired==hire||c.AwaitingRevival||c.Items is not {} items)return c;
            var scroll=items.FirstOrDefault(item=>item.InventorySlot==slot&&item.Key==key&&item.Quantity>0);
            if(scroll==null)return c;
            var remaining=scroll.Quantity==1?items.Where(item=>item!=scroll).ToArray():
                items.Select(item=>item==scroll?item with {Quantity=item.Quantity-1}:item).ToArray();
            consumed=true;
            return c with {Items=remaining,LeonHired=true};
        });
        return consumed?owner:null;
    }
    internal Character RecordFishingCast(uint id,ItemPlacement[] items)=>Update(id,c=>{
        uint limit=(uint)ShrineJob.LifeLimit(c,ShrineJob.FishingSkill)*Fishing.CounterReference(c);
        uint points=Fishing.AdvanceCounters(c.FishingPoints,limit,Random.Shared.Next(Fishing.CounterRollRange));
        var updated=c with {Items=items,FishingPoints=points};
        return ShrineJob.AwardLife(updated,ShrineJob.FishingSkill,Fishing.Mastery(c),Fishing.Mastery(updated));
    });
    internal (Character Character,CookingResult Result,ItemPlacement? Product) Cook(uint id,CookingRecipe recipe,bool useKit){
        CookingResult result=CookingResult.Requirements;
        ItemPlacement? product=null;
        var updated=Update(id,c=>{
            if(c.AwaitingRevival||recipe.Requirement(c,useKit)!=null)return c;
            result=CookingResult.MissingItems;
            if(recipe.Materials.Any(material=>!material.IsAvailable(c)))return c;
            var items=c.Items??[];
            var remaining=items;
            foreach(var material in recipe.Materials)
                remaining=NpcMaterials.Consume(c with {Items=remaining},material.Matches,material.Quantity);
            if(recipe!=CookingRecipe.Egg&&remaining.FirstOrDefault(i=>i.Key==recipe.Key&&i.InventorySlot!=0&&i.Quantity<uint.MaxValue) is {} stack){
                var combined=stack with {Quantity=stack.Quantity+1};
                product=combined;
                result=CookingResult.Cooked;
                return AwardCooking(c with {Items=remaining.Select(i=>i.InventorySlot==combined.InventorySlot?combined:i).ToArray()});
            }
            byte slot=items.FirstOrDefault(i=>recipe.IsMaterial(i)&&remaining.All(left=>left.InventorySlot!=i.InventorySlot))?.InventorySlot
                ??c.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0){result=CookingResult.InventoryFull;return c;}
            product=new(recipe.Key,slot);
            result=CookingResult.Cooked;
            return AwardCooking(c with {Items=[..remaining,product]});
        });
        return (updated,result,product);
    }
    static Character AwardCooking(Character c){
        var skills=new Dictionary<string,ushort>(c.LifeMastery??[]);
        ushort mastery=CookingRecipe.Mastery(c);
        // Keep the existing local cooking rate until retail mastery gains are captured.
        skills[ShrineJob.CookingSkill]=(ushort)Math.Max(mastery,Math.Min(ShrineJob.LifeLimit(c,ShrineJob.CookingSkill),mastery+25));
        return ShrineJob.AwardLife(c with {LifeMastery=skills},ShrineJob.CookingSkill,
            mastery,skills[ShrineJob.CookingSkill]);
    }
    internal (Character Character,RanicheResult Result,ItemPlacement? Reward) AdvanceRaniche(uint id,RanicheStage expected){
        var result=RanicheResult.Unavailable;
        ItemPlacement? reward=null;
        var updated=Update(id,c=>{
            if(c.AwaitingRevival||c.RanicheProgress!=expected||!RanicheQuest.CanAdvance(c))return c;
            var items=c.Items??[];
            if(RanicheQuest.Required(expected) is {} material)items=NpcMaterials.Consume(c,material.Matches,material.Quantity);
            if(RanicheQuest.RewardKey(expected) is {} key){
                byte slot=c.FreeInventorySlots(items).FirstOrDefault();
                if(slot==0){result=RanicheResult.InventoryFull;return c;}
                reward=new(key,slot,Durability:expected==RanicheStage.Pies?EquipmentItems.FullDurability:null);
                items=[..items,reward];
            }
            result=RanicheResult.Advanced;
            uint fee=expected==RanicheStage.Supplies?RanicheQuest.TravelOffering:0;
            return c with {RanicheProgress=expected+1,Items=items,Gold=c.Gold-fee};
        });
        return (updated,result,reward);
    }

    internal Character ReferKondQuest(uint id)=>Update(id,c=>c.KondStage==KondQuestStage.None
        ?c with {KondStage=KondQuestStage.Referred}:c);

    internal Character AcceptKondQuest(uint id)=>Update(id,c=>c.KondStage==KondQuestStage.Referred&&KondQuest.MeetsMastery(c)
        ?c with {KondStage=KondQuestStage.AwaitingShield}:c);

    internal Character BeginOldSmithQuest(uint id)=>Update(id,c=>c.OldSmithProgress==OldSmithStage.None
        ?c with {OldSmithProgress=OldSmithStage.Water}:c);

    internal (Character Character,OldSmithTurnIn Result,ItemPlacement? Reward) AdvanceOldSmithQuest(uint id,OldSmithStage expected){
        var result=OldSmithTurnIn.NotReady;
        ItemPlacement? reward=null;
        var updated=Update(id,c=>{
            if(c.OldSmithProgress!=expected||expected is not (OldSmithStage.Water or OldSmithStage.Rings))return c;
            var remaining=OldSmithQuest.Exchange(c,expected);
            if(remaining==null){result=OldSmithTurnIn.MissingMaterials;return c;}
            if(expected==OldSmithStage.Water){
                result=OldSmithTurnIn.WaterAccepted;
                return c with {OldSmithProgress=OldSmithStage.Rings,Items=remaining};
            }
            byte slot=c.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0){result=OldSmithTurnIn.InventoryFull;return c;}
            reward=new(OldSmithQuest.RingKey,slot,Durability:EquipmentItems.FullDurability);
            result=OldSmithTurnIn.RingGranted;
            return c with {OldSmithProgress=OldSmithStage.Complete,Items=[..remaining,reward]};
        });
        return (updated,result,reward);
    }

    internal (Character Character,KondRewardResult Result,ItemPlacement? Rod) CompleteKondQuest(uint id){
        var result=KondRewardResult.NotReady;
        ItemPlacement? rod=null;
        var updated=Update(id,c=>{
            if(c.KondStage!=KondQuestStage.AwaitingShield||!KondQuest.MeetsMastery(c))return c;
            var shield=(c.Items??[]).FirstOrDefault(item=>item.Quantity>0&&KondQuest.IsShield(item));
            if(shield==null){result=KondRewardResult.MissingShield;return c;}
            var remaining=NpcMaterials.Consume(c,KondQuest.IsShield,1);
            bool replacesShield=shield.Quantity==1;
            byte slot=replacesShield?shield.InventorySlot:c.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0){result=KondRewardResult.InventoryFull;return c;}
            rod=new(KondQuest.RodKey,slot,InventoryWord1:replacesShield?shield.InventoryWord1:null,
                InventoryWord2:replacesShield?shield.InventoryWord2:null,Durability:EquipmentItems.FullDurability);
            result=KondRewardResult.Received;
            return c with {KondStage=KondQuestStage.Completed,Items=[..remaining,rod]};
        });
        return (updated,result,rod);
    }

    internal Character AcceptRaddHunt(uint id,RaddHunt hunt,DateTimeOffset now)=>Update(id,c=>
        ElanciaLocal.RaddHunt.Active(c,now)!=null?c:
            c with {RaddHunt=new(hunt.Kind,now.AddHours(2).ToUnixTimeMilliseconds())});

    internal (Character Character,RaddReward Result) CompleteRaddHunt(uint id,DateTimeOffset now){
        var result=RaddReward.Expired;
        var updated=Update(id,c=>{
            if(ElanciaLocal.RaddHunt.Active(c,now) is not {} progress||ElanciaLocal.RaddHunt.For(progress.Kind) is not {} hunt)return c;
            result=RaddReward.Incomplete;
            if(progress.Kills<ElanciaLocal.RaddHunt.Required)return c;
            result=RaddReward.WalletFull;
            if(!WorldSettings.CanCreditGold(c.Gold,hunt.Reward))return c;
            var paid=(c with {Gold=c.Gold+hunt.Reward,RaddHunt=null}).ReserveWalletSlot();
            if(paid.WalletSlot==0)return c;
            result=RaddReward.Paid;
            return paid;
        });
        return (updated,result);
    }

    internal (Character Character,NpcCraftingResult Result,ItemPlacement? Product) CraftNpcItem(uint id,NpcCraftingRecipe recipe){
        var result=NpcCraftingResult.MissingItems;
        ItemPlacement? product=null;
        var current=Update(id,c=>{
            if(recipe.Materials.Any(material=>!material.IsAvailable(c)))return c;
            var remaining=c.Items??[];
            foreach(var material in recipe.Materials)
                remaining=NpcMaterials.Consume(c with {Items=remaining},material.Matches,material.Quantity);
            byte slot=c.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0){result=NpcCraftingResult.InventoryFull;return c;}
            product=new(recipe.ProductKey,slot,Durability:EquipmentItems.FullDurability);
            result=NpcCraftingResult.Crafted;
            return c with {Items=[..remaining,product]};
        });
        return (current,result,product);
    }
    internal (Character Character,ButcherResult Result,ItemPlacement? Product) DressChicken(uint id){
        var result=ButcherResult.MissingChicken;
        ItemPlacement? product=null;
        var updated=Update(id,c=>{
            var source=(c.Items??[]).FirstOrDefault(item=>item.Quantity>0&&MutaiButcher.IsChicken(item));
            if(source==null)return c;
            var remaining=NpcMaterials.Consume(c,MutaiButcher.IsChicken,1);
            byte slot=source.Quantity==1?source.InventorySlot:c.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0){result=ButcherResult.InventoryFull;return c;}
            ushort x=(ushort)(25+(slot-1)%10*20),y=(ushort)(15+(slot-1)/10*16);
            if(slot==source.InventorySlot){x=source.InventoryWord1??x;y=source.InventoryWord2??y;}
            product=new(MutaiButcher.ProductKey,slot,1,x,y);
            result=ButcherResult.Dressed;
            return c with {Items=[..remaining,product]};
        });
        return (updated,result,product);
    }
    internal (Character Character,NeruaReward Result,ItemPlacement? Candy) GrantNeruaCandy(uint id){
        var result=NeruaReward.AlreadyCarrying;
        ItemPlacement? candy=null;
        var current=Update(id,c=>{
            var items=c.Items??[];
            if(items.Any(item=>item.Quantity>0&&NpcMaterials.Matches(item,NeruaDialog.CandySprite,NeruaDialog.CandyName)))return c;
            byte slot=c.FreeInventorySlots(items).FirstOrDefault();
            if(slot==0){result=NeruaReward.InventoryFull;return c;}
            candy=new(NeruaDialog.CandyKey,slot);
            result=NeruaReward.Granted;
            return c with {Items=[..items,candy]};
        });
        return (current,result,candy);
    }
    internal (Character Character,JewelryResult Result,ItemPlacement? Product) CraftJewelry(uint id,JewelryRecipe recipe){
        var result=JewelryResult.MissingMaterials;
        ItemPlacement? product=null;
        var current=Update(id,c=>{
            if(!recipe.HasMaterials(c))return c;
            result=JewelryResult.InsufficientGold;
            if(c.Gold<recipe.Fee)return c;
            var remaining=NpcMaterials.Consume(c,recipe.IsRing,JewelryRecipe.RingCount);
            if(recipe.StoneKey!=null)remaining=NpcMaterials.Consume(c with {Items=remaining},recipe.IsStone,JewelryRecipe.StoneCount);
            var paid=(c with {Gold=c.Gold-recipe.Fee,Items=remaining}).ReserveWalletSlot();
            result=JewelryResult.InventoryFull;
            byte slot=paid.FreeInventorySlots(remaining).FirstOrDefault();
            if(slot==0||paid.WalletSlot==0)return c;
            product=new(recipe.ProductKey,slot,1,(ushort)(25+(slot-1)%10*20),(ushort)(15+(slot-1)/10*16),EquipmentItems.FullDurability);
            result=JewelryResult.Crafted;
            return paid with {Items=[..remaining,product]};
        });
        return (current,result,product);
    }
    internal Character ChangeHair(uint id,HairPart part,byte value,uint price)=>Update(id,c=>{
        byte[] appearance=c.DisplayModel();
        if(appearance[(int)part]==value||c.Gold<price)return c;
        appearance[(int)part]=value;
        return c with {DisplayAppearance=Convert.ToHexString(appearance),Gold=c.Gold-price};
    });
    internal Character AwardOnlineFame(uint id)=>Update(id,c=>c with {
        Fame=(uint)Math.Min(uint.MaxValue,(ulong)CombatAbilities.Fame(c)+3)
    });
    internal Character ExchangeFame(uint id,uint spent,uint credit)=>Update(id,c=>{
        uint fame=CombatAbilities.Fame(c);
        if(spent==0||credit==0||fame<spent||!WorldSettings.CanCreditGold(c.Gold,credit))return c;
        var updated=(c with {Fame=fame-spent,Gold=c.Gold+credit,WalletX=55,WalletY=17}).ReserveWalletSlot();
        return updated.WalletSlot==0?c:updated;
    });
    internal (Character Character,MetiniResult Result) PayMetini(uint id){
        var result=MetiniResult.NotReady;
        var updated=Update(id,c=>{
            if(c.MetiniProgress!=MetiniStage.None){result=MetiniResult.AlreadyPaid;return c;}
            if(c.AwaitingRevival||!MetiniQuest.MeetsHealthRequirement(c))return c;
            if(c.Gold<MetiniQuest.Fee){result=MetiniResult.MissingGold;return c;}
            if(NpcMaterials.Count(c,MetiniQuest.IsElsaria)<MetiniQuest.Stones){result=MetiniResult.MissingStones;return c;}
            result=MetiniResult.Applied;
            return c with {MetiniProgress=MetiniStage.Paid,Gold=c.Gold-MetiniQuest.Fee,
                Items=NpcMaterials.Consume(c,MetiniQuest.IsElsaria,MetiniQuest.Stones)};
        });
        return (updated,result);
    }
    internal (Character Character,MetiniResult Result,ItemPlacement? Reward) CompleteMetini(uint id){
        var result=MetiniResult.NotReady;
        ItemPlacement? reward=null;
        var updated=Update(id,c=>{
            if(c.MetiniProgress!=MetiniStage.Paid||c.MetiniRoosters<MetiniQuest.Roosters||c.AwaitingRevival)return c;
            var items=c.Items??[];
            byte slot=c.FreeInventorySlots(items).FirstOrDefault();
            if(slot==0){result=MetiniResult.InventoryFull;return c;}
            reward=new(MetiniQuest.AratKey,slot);
            result=MetiniResult.Applied;
            return c with {MetiniProgress=MetiniStage.Completed,Items=[..items,reward]};
        });
        return (updated,result,reward);
    }
    internal (Character Character,SpellResult Result) LearnSpell(uint id,byte slot,LocalSpell spell){
        var result=SpellResult.Unavailable;
        var updated=Update(id,c=>{
            if(c.AwaitingRevival)return c;
            if(spell.IsKnown(c)){result=SpellResult.AlreadyLearned;return c;}
            var items=c.Items??[];
            var arat=items.FirstOrDefault(item=>item.InventorySlot==slot&&spell.Teaches(item.Key)&&item.Quantity>0);
            if(arat==null)return c;
            result=SpellResult.Applied;
            byte preferred=spell==LocalSpell.Bris?(byte)1:spell.Slot;
            var used=LocalSpell.All.Where(known=>known.IsKnown(c)).Select(known=>known.WireSlot(c)).ToHashSet();
            byte wireSlot=used.Contains(preferred)?Enumerable.Range(1,LocalSpell.All.Length).Select(i=>(byte)i)
                .First(candidate=>!used.Contains(candidate)):preferred;
            var learned=c with {Items=NpcMaterials.Consume(c,item=>item.InventorySlot==slot,1)};
            if(wireSlot!=spell.Slot){
                var slots=new Dictionary<byte,byte>(c.SpellSlots??[]){[spell.Slot]=wireSlot};
                learned=learned with {SpellSlots=slots};
            }
            return spell.Slot switch {
                DaraSpell.Slot=>learned with {DaraLearned=true},
                RimediumSpell.Slot=>learned with {RimediumLearned=true},
                BraemaSpell.Slot=>learned with {BraemaLearned=true},
                BrelSpell.Slot=>learned with {BrelLearned=true},
                PlayerDebuff.SlowSlot=>learned with {SlowLearned=true},
                PlayerDebuff.CurseSlot=>learned with {CurseLearned=true},
                BrisSpell.Id=>learned with {BrisLearned=true},
                _=>throw new InvalidDataException("Unknown spell")
            };
        });
        return (updated,result);
    }
    internal (Character Character,TorionResult Result,ItemPlacement? Reward) AdvanceTorion(uint id,TorionStage expected){
        var result=TorionResult.Unavailable;
        ItemPlacement? reward=null;
        var updated=Update(id,c=>{
            if(c.AwaitingRevival||c.TorionProgress!=expected||!TorionQuest.CanAdvance(c))return c;
            var items=c.Items??[];
            if(TorionQuest.Reward(expected) is {} key){
                byte slot=c.FreeInventorySlots(items).FirstOrDefault();
                if(slot==0){result=TorionResult.InventoryFull;return c;}
                reward=new(key,slot);
                items=[..items,reward];
            }
            result=TorionResult.Advanced;
            return c with {TorionProgress=expected+1,TorionKills=0,Items=items};
        });
        return (updated,result,reward);
    }
    internal (Character Character,SpellResult Result) RecordDamageSpell(uint id,LocalSpell spell,int targetLimit,FieldObject? defeated,
        ushort killFame,DateTimeOffset now,int manaGrowthRoll=0){
        var result=SpellResult.Unavailable;
        var updated=Update(id,c=>{
            result=SpendSpell(c,spell,out var paid,manaGrowthRoll,targetLimit);
            if(result!=SpellResult.Applied)return c;
            if(killFame>0)paid=AddKillFame(paid,killFame);
            return RecordQuestDefeat(paid,defeated,now);
        });
        return (updated,result);
    }
    internal (Character Character,SpellResult Result) RecordConversionCast(uint id,LocalSpell spell,int manaGrowthRoll=0){
        var result=SpellResult.Unavailable;
        var updated=Update(id,c=>{
            if(spell.TargetMode!=5||!spell.IsKnown(c)||c.AwaitingRevival)return c;
            bool toMana=spell.Slot==BrelSpell.Slot;
            uint maximum=c.MaxMp(EquipmentItems.EquippedEffects(c));
            uint missingMana=Math.Min(maximum,c.ManaSpent);
            uint deficit=toMana?missingMana:c.DamageTaken;
            if(deficit==0)return c;
            uint hp=c.CurrentHp();
            uint available=toMana?hp-Math.Min(hp,1):maximum-missingMana;
            if(available==0){result=toMana?SpellResult.Unavailable:SpellResult.MissingMana;return c;}
            result=SpendSpell(c,spell,out var paid,manaGrowthRoll);
            if(result!=SpellResult.Applied)return c;
            uint converted=Math.Min(BraemaSpell.MaxConversion,Math.Min(available,deficit));
            return toMana?paid with {ManaSpent=paid.ManaSpent-converted,DamageTaken=paid.DamageTaken+converted}:
                paid with {ManaSpent=paid.ManaSpent+converted,DamageTaken=paid.DamageTaken-converted};
        });
        return (updated,result);
    }
    internal (Character Caster,Character Target,SpellResult Result) RecordRimediumCast(uint casterId,uint targetId,uint healing,int manaGrowthRoll=0){
        lock(gate){
            int casterIndex=characters.FindIndex(c=>c.Id==casterId);
            int targetIndex=characters.FindIndex(c=>c.Id==targetId);
            if(casterIndex<0||targetIndex<0)throw new InvalidDataException("Character missing");
            var caster=characters[casterIndex];
            var target=characters[targetIndex];
            if(target.AwaitingRevival)return (caster,target,SpellResult.Unavailable);
            var result=SpendSpell(caster,RimediumSpell.Data,out var paid,manaGrowthRoll);
            if(result!=SpellResult.Applied)return (caster,target,result);
            if(casterIndex==targetIndex)target=paid;
            var restored=target with {DamageTaken=target.DamageTaken-Math.Min(target.DamageTaken,healing)};
            var next=characters.ToList();
            next[casterIndex]=paid;
            next[targetIndex]=restored;
            Save(next);
            return (next[casterIndex],restored,result);
        }
    }
    internal (Character Caster,Character Target,SpellResult Result,ushort Duration) RecordDebuffCast(
        uint casterId,uint targetId,PlayerDebuff debuff,long now,int manaGrowthRoll=0){
        lock(gate){
            int casterIndex=characters.FindIndex(c=>c.Id==casterId);
            int targetIndex=characters.FindIndex(c=>c.Id==targetId);
            if(casterIndex<0||targetIndex<0)throw new InvalidDataException("Character missing");
            var caster=characters[casterIndex];
            var target=characters[targetIndex];
            if(casterIndex==targetIndex||target.CurrentHp()==0||caster.CurrentHp()==0||
                !WorldSettings.AllowsDuel(caster.Map)||target.Map!=caster.Map||target.Floor!=caster.Floor)
                return (caster,target,SpellResult.Unavailable,0);
            var result=SpendSpell(caster,debuff.Spell,out var paid,manaGrowthRoll);
            if(result!=SpellResult.Applied)return (caster,target,result,0);
            ushort duration=debuff.Duration(caster);
            var affected=debuff.WithDeadline(target,now+duration*1000L);
            var next=characters.ToList();
            next[casterIndex]=paid;
            next[targetIndex]=affected;
            Save(next);
            return (paid,affected,result,duration);
        }
    }
    internal Character ExpireDebuffs(uint id,long now)=>Update(id,c=>{
        foreach(var debuff in PlayerDebuff.All){
            long deadline=debuff.Deadline(c);
            if(deadline>0&&deadline<=now)c=debuff.WithDeadline(c,0);
        }
        return c;
    });
    static SpellResult SpendSpell(Character caster,LocalSpell spell,out Character paid,int manaGrowthRoll,int targetLimit=10000){
        paid=caster;
        if(!spell.IsKnown(caster)||caster.AwaitingRevival)return SpellResult.Unavailable;
        var effects=EquipmentItems.EquippedEffects(caster).ToArray();
        uint maxMana=caster.MaxMp(effects);
        if(maxMana-Math.Min(maxMana,caster.ManaSpent)<spell.ManaCost)return SpellResult.MissingMana;
        var items=caster.Items??[];
        if(!spell.HasFocus(caster)){
            if(NpcMaterials.Count(caster,spell.IsStone)<spell.StoneCost)return SpellResult.MissingStone;
            items=NpcMaterials.Consume(caster,spell.IsStone,spell.StoneCost);
        }
        paid=caster with {Items=items,ManaSpent=caster.ManaSpent+spell.ManaCost};
        ushort counter=spell.Counter(caster);
        if(targetLimit>0&&counter<spell.Reference){
            // Count successful uses while the next level's requirements are still unobserved.
            paid=paid with {SpellCounters=new Dictionary<byte,ushort>(caster.SpellCounters??[]){[spell.Slot]=(ushort)(counter+1)}};
        }
        var element=spell.Element;
        uint points=CombatAbilities.Points(caster,element);
        uint cap=(uint)(Math.Min(targetLimit,ShrineJob.CombatLimit(caster.Job,element))*element.Reference);
        if(points<cap){
            // One local elemental count per successful cast until a retail cast delta is available.
            var abilities=new Dictionary<byte,uint>(caster.Abilities??[]);
            abilities[element.Slot]=points+1;
            paid=ShrineJob.AwardCombat(paid with {Abilities=abilities,CombatExperience=CombatAbilities.Experience(caster)},element,
                (int)(points/element.Reference),(int)((points+1)/element.Reference));
        }
        if(manaGrowthRoll>0&&maxMana<uint.MaxValue&&paid.TrainingMp<uint.MaxValue&&paid.ManaSpent<uint.MaxValue&&
            Random.Shared.Next(manaGrowthRoll)==0){
            var grown=paid with {TrainingMp=paid.TrainingMp+1};
            if(grown.MaxMp(effects)>maxMana)grown=grown with {ManaSpent=paid.ManaSpent+1};
            paid=grown;
        }
        return SpellResult.Applied;
    }
    internal (Character Hit,Character Combo,Character Updated) RecordHit(uint id,CombatAbility ability,int limit,ItemPlacement[]? placements,
        ushort comboCounter,(ushort Hit,ushort Combo,ushort KillFame) experience,ushort hitCounter=CombatAbilities.DefaultHitCounter,
        FieldObject? defeated=null,DateTimeOffset? killedAt=null,bool gainHealth=false){
        Character hit=null!;
        Character combo=null!;
        var updated=Update(id,c=>{
            hit=AddCounters(c,hitCounter,experience.Hit);
            if(gainHealth&&CombatAbilities.Points(hit,ability)>CombatAbilities.Points(c,ability)&&
                hit.DamageTaken<uint.MaxValue&&hit.MaxHp(EquipmentItems.EquippedEffects(hit))<uint.MaxValue)
                hit=hit with {TrainingHp=hit.TrainingHp+1,DamageTaken=hit.DamageTaken+1};
            if(placements!=null)hit=ApplyEquipment(hit,placements);
            combo=AddCounters(hit,comboCounter,experience.Combo);
            var result=experience.KillFame==0?combo:AddKillFame(combo,experience.KillFame);
            return RecordQuestDefeat(result,defeated,killedAt);
        });
        return (hit,combo,updated);

        Character AddCounters(Character c,ushort points,ushort experiencePoints){
            ushort reference=CombatAbilities.Reference(c,ability);
            ushort previous=CombatAbilities.Mastery(c,ability);
            int maximum=Math.Min(limit,ShrineJob.CombatLimit(c.Job,ability));
            const int boundary=CombatAbilities.UnarmedReferenceBoundary;
            const ushort nextReference=CombatAbilities.UnarmedReferenceAfterBoundary;
            bool usesObservedBoundary=ability==CombatAbilities.All[0]&&CombatAbilities.Grade(c,ability)==0&&reference==CombatAbilities.UnarmedReferenceBeforeBoundary&&
                previous<boundary&&maximum>=boundary;
            uint cap=(uint)(usesObservedBoundary?boundary*reference+(maximum-boundary)*nextReference:maximum*reference);
            uint current=CombatAbilities.Points(c,ability);
            if(points==0||current>=cap)return c;
            var abilities=c.Abilities is {} saved?new Dictionary<byte,uint>(saved):[];
            uint gain=Math.Min(cap-current,points);
            uint total=current+gain;
            bool changesReference=usesObservedBoundary&&total>=(uint)(boundary*reference);
            if(changesReference){
                // Counters past 20.00 count toward the new denominator.
                total+=(uint)(boundary*(nextReference-reference));
                reference=nextReference;
            }
            abilities[ability.Slot]=total;
            ushort mastery=(ushort)Math.Min(10000u,total/reference);
            uint? recorded=CombatAbilities.ObservedMasteryExperience(ability,previous,mastery);
            var trained=recorded switch {
                null=>AddProgress(c,Math.Min(gain,experiencePoints)),
                uint award=>AddCombatExperience(c,award)
            };
            trained=trained with {Abilities=abilities,AbilityCountsVersion=CombatAbilities.CounterVersion,
                UnarmedCounterReference=changesReference?reference:trained.UnarmedCounterReference};
            return ShrineJob.AwardCombat(trained,ability,previous,mastery);
        }
    }
    static Character RecordQuestDefeat(Character character,FieldObject? defeated,DateTimeOffset? killedAt){
        if(defeated==null)return character;
        if(character.MetiniRoosters<MetiniQuest.Roosters&&RaddHunt.Rooster.Counts(defeated))
            character=character with {MetiniRoosters=(byte)(character.MetiniRoosters+1)};
        if(killedAt is {} now&&RaddHunt.Active(character,now) is {} progress&&
            progress.Kills<RaddHunt.Required&&RaddHunt.For(progress.Kind)?.Counts(defeated)==true)
            character=character with {RaddHunt=progress with {Kills=progress.Kills+1}};
        return TorionQuest.RecordDefeat(character,defeated);
    }
    static Character AddKillFame(Character character,uint amount)=>character with {
        Fame=(uint)Math.Min(uint.MaxValue,(ulong)CombatAbilities.Fame(character)+amount)
    };
    static Character AddCombatExperience(Character character,uint amount)=>GuardianQuest.Accrue(character,character with {
        CombatExperience=(uint)Math.Min(uint.MaxValue,(ulong)CombatAbilities.Experience(character)+amount),
        Fame=CombatAbilities.Fame(character)
    });
    // Preserve the legacy training fallback where there is no observed EXP award.
    static Character AddProgress(Character character,uint amount)=>AddCombatExperience(character,amount) with {
        Fame=(uint)Math.Min(uint.MaxValue,(ulong)CombatAbilities.Fame(character)+amount)
    };
    internal static Character ApplyEquipment(Character character,ItemPlacement[] placements){
        var updated=Fishing.LearnEquipped(CombatAbilities.LearnEquipped(EquipmentItems.ApplyAppearance(character with {Items=placements})));
        var previousEffects=EquipmentItems.EquippedEffects(character).ToArray();
        var effects=EquipmentItems.EquippedEffects(updated).ToArray();
        uint maxHp=updated.MaxHp(effects),maxMp=updated.MaxMp(effects);
        // Equipment changes preserve current vitals, capped by the new maxima.
        uint damageTaken=character.AwaitingRevival?Math.Min(character.DamageTaken,maxHp-1):
            maxHp-Math.Min(maxHp,character.CurrentHp(character.MaxHp(previousEffects)));
        uint manaSpent=maxMp-Math.Min(maxMp,character.CurrentMp(character.MaxMp(previousEffects)));
        return updated with {DamageTaken=damageTaken,ManaSpent=manaSpent};
    }
    Character Update(uint id,Func<Character,Character> change){
        lock(gate){
            int index=characters.FindIndex(c=>c.Id==id);
            if(index<0)throw new InvalidDataException("Character missing");
            var next=characters.ToList();next[index]=change(next[index]);
            if(next[index]!=characters[index])Save(next);
            return next[index];
        }
    }
    internal (Character Character,string? Refusal) RaiseGrade(uint id,ShrineGrade skill){
        string? refusal=null;
        var updated=Update(id,current=>{
            var upgraded=CombatAbilities.UpgradeCounters(current);
            refusal=skill.Refusal(upgraded);
            return refusal==null?skill.Advance(upgraded):current;
        });
        return (updated,refusal);
    }
    void Save(List<Character> next) {
        if(database!=null){
            database.SaveCharacters(characters,next);
            characters=next;
            return;
        }
        string temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(next,new JsonSerializerOptions{WriteIndented=true}));
        for(int attempt=0;;attempt++){
            try{File.Move(temp,path,true);break;}
            catch(Exception ex) when(attempt<2&&OperatingSystem.IsWindows()&&
                (ex is IOException or UnauthorizedAccessException)&&(ex.HResult&0xffff) is 5 or 32 or 33){
                Thread.Sleep(10*(attempt+1));
            }
        }
        characters=next;
    }
}

public ref struct PacketReader(ReadOnlySpan<byte> data) {
    ReadOnlySpan<byte> remaining=data;
    public byte U8(){if(remaining.IsEmpty)throw new InvalidDataException("Truncated field");byte v=remaining[0];remaining=remaining[1..];return v;}
    public ushort U16()=>checked((ushort)((U8()<<8)|U8()));
    public uint U32()=>((uint)U16()<<16)|U16();
    public string Str8()=>ReadText(U8());
    public string Str16()=>ReadText(U16());
    string ReadText(int n){if(remaining.Length<n)throw new InvalidDataException("Truncated string");var s=Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(remaining[..n]);remaining=remaining[n..];return s;}
    public void End(){if(!remaining.IsEmpty)throw new InvalidDataException("Unexpected trailing fields");}
    public static (byte Domain,string Name) Selection(byte[] p){var r=new PacketReader(p.AsSpan(1));var domain=r.U8();var name=r.Str8();r.End();return(domain,name);}
    public static string Name(byte[] p){var r=new PacketReader(p.AsSpan(1));var name=r.Str8();r.End();return name;}
    public static (byte Mode,byte Color,string Text) Say(byte[] p){var r=new PacketReader(p.AsSpan(1));byte mode=r.U8();if(mode!=4)throw new InvalidDataException("Only local speech mode 4 is supported");byte color=r.U8();string text=r.Str8();r.End();return(mode,color,text);}
    public static (uint ObjectId,uint[] Choices) Script(byte[] p){var r=new PacketReader(p.AsSpan(1));uint oid=r.U32();int count=r.U8();if(count>16)throw new InvalidDataException("Too many script arguments");uint[] choices=new uint[count];for(int i=0;i<count;i++)choices[i]=r.U32();r.End();return(oid,choices);}
    public static (ShopEntry? Entry,byte Quantity)[] ShopOrder(byte[] p,LocalNpc merchant){
        var r=new PacketReader(p.AsSpan(8));
        r.U8(); // Native shop window ID, followed by the number of item lines.
        int count=r.U8();
        if(count>WorldSettings.InventorySlots)throw new InvalidDataException("Too many shop order lines");
        var order=new (ShopEntry? Entry,byte Quantity)[count];
        for(int i=0;i<count;i++){
            ShopEntry? entry;
            if(merchant.UsesExtendedShop){
                uint id=r.U32();
                entry=merchant.Shop!.SingleOrDefault(e=>e.Id==id);
            }else{
                string name=r.Str8();
                entry=merchant.Shop!.SingleOrDefault(e=>EquipmentItems.Get(e.Key).Name==name);
            }
            order[i]=(entry,r.U8());
        }
        r.End();
        return order;
    }
    internal static (byte Kind,byte Slot,byte Quantity)[] ShopServiceOrder(byte[] packet,bool repair){
        var reader=new PacketReader(packet.AsSpan(8));
        reader.U8();
        int count=reader.U8();
        if(count>100)throw new InvalidDataException("Too many shop service lines");
        var order=new (byte Kind,byte Slot,byte Quantity)[count];
        for(int i=0;i<count;i++){
            byte kind=repair?reader.U8():(byte)0;
            if(kind>1)throw new InvalidDataException("Unknown repair item kind");
            order[i]=(kind,reader.U8(),kind==0?reader.U8():(byte)1);
        }
        reader.End();
        return order;
    }
}
