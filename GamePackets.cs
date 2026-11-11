using System.IO.Compression;
using System.Text;

namespace ElanciaLocal;
public static class GamePackets
{
    // v611 ignores a new S30 while the previous pursuit window is being disposed.
    internal const int PursuitReplyDelayMs=50;
    public static byte[] MetadataList()=>new PacketWriter(0x6F).U8(1).U16(0).Build();
    public static byte[] Check(bool accepted,string message="")=>CheckCode(accepted?0:3,message);
    public static byte[] CheckCode(int code,string message="")=>new PacketWriter(2).U8(code).Str8(message).Build();
    public static byte[] CharacterList(Character[] characters) {
        // 004DC9D4: u16 count; repeated u8 domain + Str8 name.
        // Bootstrap record index 0 is the default domain for this local server.
        var p=new PacketWriter(0x8A).U16(characters.Length);
        foreach(var c in characters)p.U8(0).Str8(c.Name);return p.Build();
    }
    public static byte[] Human(Character c) {
        byte[] appearance=c.DisplayModel();
        if(c.AwaitingRevival)appearance[1]|=0x40;
        var p=new PacketWriter(0x33).U16(c.X).U16(c.Y).U8(c.Floor).U8(c.Direction).U32(c.Id).U8(0).U8(0).Bytes(appearance.AsSpan(0,6));
        if(c.Equipment!=null&&c.Equipment.Length!=10)throw new InvalidDataException("Invalid equipment appearance length");
        for(int i=0;i<10;i++)p.U16(c.Equipment?[i]??0);
        return p.Str8(c.Name).U8(0).Build();
    }
    public static byte[] Status(Character? character=null,byte mask=0x70,byte? state=null,uint? currentHp=null,
        uint? currentMp=null,uint vitalsExtra=100){
        // 004DE0C5: masks 0x40 (base/maxima), 0x20 (current values), 0x10 (gold).
        // Equipment deltas come from matched S37/S38 and S08 captures.
        byte status=state??(character?.AwaitingRevival==true?(byte)2:(byte)0);
        if(mask==0x10)return Gold(character?.Gold??1000,status);
        var effects=EquipmentItems.EquippedEffects(character).ToArray();
        uint hp=character?.MaxHp(effects)??EquipmentItems.MaxHp(effects);
        uint mp=character?.MaxMp(effects)??EquipmentItems.MaxMp(effects);
        uint experience=CombatAbilities.Experience(character);
        var p=new PacketWriter(8).U8(mask).U8(status)
            .U8(1).U8(0).U8(0).U8(CombatAbilities.Level(experience));
        p.U32(hp).U32(mp).U32(WorldSettings.MaxSatiety).U32(1000);
        // The native client uses agility to calculate animation timing (004B890B).
        var attributes=character?.JobStats??new JobAttributes();
        static int StatWord(int value)=>Math.Clamp(value,0,ushort.MaxValue);
        int strength=StatWord(GuardianQuest.Strength(character,effects.Sum(e=>e.Str)));
        int curse=PlayerDebuff.CursePenalty(character);
        p.U16(strength).U16(StatWord(attributes.Int+effects.Sum(e=>e.Int)-curse)).U16(character?.ThirdBaseStatusWord??10)
            .U16(StatWord(attributes.Vit+effects.Sum(e=>e.Vit)-curse)).U16(EquipmentItems.Agility(character,effects)).U16(0);
        p.U32(CombatAbilities.Fame(character)).U16(0).U16(0); // the S08 base DWord is Fame
        // The inventory panel shows (bag weight + equipped weight) / this capacity.
        p.U16(StatWord(15*strength)).U16(EquipmentItems.PhysicalDefense(effects)).U16(EquipmentItems.MagicDefense(effects));
        if((mask&0x20)!=0)WriteVitals(p,character,hp,mp,currentHp,vitalsExtra,currentMp);
        if((mask&0x10)!=0)p.U32(character?.Gold??1000);
        if((mask&8)!=0)p.U32(0).U16(0).U32(0).U16(100);
        return p.Build();
    }
    static PacketWriter WriteVitals(PacketWriter writer,Character? character,uint hp,uint mp,uint? currentHp,uint extra=100,uint? currentMp=null)=>
        writer.U32(currentHp??character?.CurrentHp(hp)??hp).U32(currentMp??character?.CurrentMp(mp)??mp)
            .U32(Math.Min(character?.Satiety??WorldSettings.MaxSatiety,WorldSettings.MaxSatiety)).U32(extra);
    public static byte[] CurrentVitals(Character character,byte? state=null,uint? currentHp=null,uint vitalsExtra=100){
        var effects=EquipmentItems.EquippedEffects(character).ToArray();
        return WriteVitals(new PacketWriter(8).U8(0x20).U8(state??0),character,
            character.MaxHp(effects),character.MaxMp(effects),currentHp,vitalsExtra).Build();
    }
    internal static byte[] EmptySatietyStatus(byte state)=>[8,0,state,0];
    internal static (byte[] Packet,byte[]? Following) PeriodicStatus(Character before,Character after,byte state,uint vitalsAux,
        uint? effectFlags=null,int dimension=0,bool famePending=false,bool onlineFameAwarded=false){
        bool sameHealth=after.DamageTaken==before.DamageTaken;
        bool healed=after.DamageTaken<before.DamageTaken;
        bool decayed=after.Satiety<before.Satiety;
        bool sameMana=after.ManaSpent==before.ManaSpent;
        bool onlySatiety=sameHealth&&decayed&&sameMana;
        bool onlyMana=sameHealth&&after.Satiety==before.Satiety&&after.ManaSpent<before.ManaSpent;
        bool onlyHealth=healed&&after.Satiety==before.Satiety&&sameMana;
        if(after.SpeedPotionUntil>0&&(onlySatiety||onlyMana||onlyHealth)){
            // 1:135309 updates the flags before the later recovery and satiety ticks.
            var vitals=CurrentVitals(after,state,vitalsExtra:vitalsAux);
            uint flags=StatusOptionFlags(after,effectFlags??0x823FU);
            return (AppendStatusEffects(vitals,flags,150,padded:onlySatiety),null);
        }
        // Retail 1:2949 and 1:129052 combine the online award and recovery.
        if(onlineFameAwarded)return (PaddedStatus(after,0x60,state,vitalsAux),null);
        // 1:1483–1484 sends the combined change before a state-only packet.
        if(dimension==0&&after.Map is >=0 and <10000&&after.SpeedPotionUntil==0&&after.HealthPotionUntil==0&&
            !famePending&&healed&&decayed&&sameMana)
            return (PaddedStatus(after,0x20,state,vitalsAux),EmptySatietyStatus(state));
        // Captured Alpha HP ticks and the health-only potion sequence use compact vitals.
        bool compactHealth=onlyHealth&&(after.Map is >=0 and <10000||after.HealthPotionUntil>0);
        bool compactResources=onlySatiety||(onlyMana&&after.Satiety==0);
        if(after.HealthPotionUntil==0&&dimension is 0 or 1)
            compactResources=dimension==0&&(onlySatiety||(onlyMana&&!famePending));
        byte mask=compactResources||compactHealth?(byte)0x20:(byte)0x60;
        return (PaddedStatus(after,mask,state,vitalsAux),null);
    }
    internal static byte[] PaddedStatus(Character character,byte mask,byte? state=null,uint vitalsExtra=100)=>
        VitalsStatus(character,mask,state,vitalsExtra,padded:true);
    internal static byte[] VitalsStatus(Character character,byte mask,byte? state=null,uint vitalsExtra=100,bool padded=false){
        var packet=mask==0x20?CurrentVitals(character,state,vitalsExtra:vitalsExtra):Status(character,mask,state,vitalsExtra:vitalsExtra);
        return padded?[..packet,0]:packet;
    }
    internal static byte[] PotionBuffStatus(Character character,byte baseMask=0,bool padded=true,byte? state=null,uint vitalsExtra=100,uint? effectFlags=null){
        byte mode=state??(character.AwaitingRevival?(byte)2:(byte)1);
        var body=baseMask==0?new byte[]{8,8,mode}:Status(character,baseMask,state:mode,vitalsExtra:vitalsExtra);
        bool speed=character.SpeedPotionUntil>0;
        return AppendStatusEffects(body,StatusOptionFlags(character,effectFlags??(speed?0x823FU:0x82BFU)),speed?150:100,padded);
    }
    internal static byte[] DoorBuffStatus(Character character,byte state)=>
        AppendStatusEffects([8,8,state],StatusOptionFlags(character,0x82BFU),150,padded:true);
    internal static byte[] DoorOptionStatus(Character character,byte state,uint auxiliary,RecordedDoorOptionStatus sample){
        var body=sample.Mask==0x28?CurrentVitals(character,state,vitalsExtra:auxiliary):new byte[]{8,0,state};
        return AppendStatusEffects(body,sample.Flags,100,padded:true);
    }
    static uint StatusOptionFlags(Character character,uint flags){
        if(character.StatusEffectBit0 is not bool enabled)return flags;
        // Both recorded C1B replies retain bit 17 and differ only at bit 0.
        flags|=0x20000U;
        return enabled?flags|1U:flags&~1U;
    }
    internal static byte[] StatusOptionReply(Character character,byte state){
        bool speed=character.SpeedPotionUntil>0;
        uint flags=!speed&&character.HealthPotionUntil>0?0x82BFU:0x823FU;
        return AppendStatusEffects([8,8,state],StatusOptionFlags(character,flags),speed?150:100,padded:true);
    }
    static byte[] AppendStatusEffects(byte[] body,uint flags,int speedPercent,bool padded){
        body[1]|=8;
        var packet=new PacketWriter(8).Bytes(body.AsSpan(1)).U32(0).U16(0).U32(flags).U16(speedPercent);
        if(padded)packet.U8(0);
        return packet.Build();
    }
    internal static byte[] StatusForDimension(byte[] packet,int dimension){
        if(packet.Length<6||packet[0]!=8||(packet[1]&0x40)==0||packet[3]>>4==dimension)return packet;
        var result=(byte[])packet.Clone();
        result[3]=(byte)((result[3]&0x0F)|(dimension<<4));
        return result;
    }
    internal static byte[] EntryArrivalStatus(Character character,byte state,int dimension){
        if(dimension!=0){
            byte mask=character.Gold==0?(byte)0x40:(byte)0x50;
            return [..StatusForDimension(Status(character,mask,state),dimension),0];
        }
        return character.Gold==0?[8,0,state,0]:[..Gold(character.Gold,state),0];
    }
    internal static byte[] InitialStatus(Character character,byte state,int dimension,uint? currentHp=null,uint? currentMp=null){
        bool speed=character.SpeedPotionUntil>0;
        bool equipped=(character.Items??[]).Any(item=>item.InventorySlot==0);
        uint flags=!speed&&(!equipped||character.HealthPotionUntil>0)?0x82BFU:0x823FU;
        var status=StatusForDimension(Status(character,state:state,currentHp:currentHp,currentMp:currentMp,vitalsExtra:0),dimension);
        return AppendStatusEffects(status,StatusOptionFlags(character,flags),speed?150:100,padded:true);
    }
    internal static byte[] ShopBalanceStatus(Character character,byte state,int dimension,bool padded){
        var packet=dimension==0?Gold(character.Gold,state):Status(character,mask:0x50,state:state);
        return padded?[..packet,0]:packet;
    }
    internal static byte[] InnRecoveryVitals(Character character,uint auxiliary)=>
        CurrentVitals(character,state:1,vitalsExtra:auxiliary);
    internal static byte[] SelfLook(Character character,bool initialLogin=false){
        var equipped=(character.Items??[]).Where(item=>item.InventorySlot==0)
            .OrderBy(item=>EquipmentItems.Get(item.Key).EquipmentSlot).ToArray();
        // 004AB360 draws the first Str8 in CLAN; 004AB3D0 draws the second in JOB.
        var packet=new PacketWriter(0x39).U8(0).Str8(character.GuildName??"").Str8(character.Job??"").U8(character.Age??4).U8(equipped.Length);
        foreach(var item in equipped)packet.U8(EquipmentItems.Get(item.Key).EquipmentSlot).U8(EquipmentItems.DurabilityPercent(item));
        uint experience=CombatAbilities.Experience(character);
        byte level=CombatAbilities.Level(experience);
        var fate=character.FatePoints??new();
        // 004AAB6C and 004AAC15 update LEVEL and its bar. 004AAC3D formats EXP.
        packet.U8(1).U32(character.BattlePoints).U32(6).U8(level).U8(CombatAbilities.LevelProgress(experience,level)).U32(experience)
            .U8(character.AptitudeOrder()).U8(level-1).U8(fate.Warrior).U8(fate.Adventurer).U8(fate.Merchant)
            .U8(character.AvailableFatePoints());
        if(initialLogin&&character.Age==1&&equipped.Length==0)packet.U8(0);
        return packet.Build();
    }
    public static byte[] Transfer(int port,byte[] token)=>new PacketWriter(3).Bytes([1,0,0,127]).U16(port).U8(token.Length).Bytes(token).Build();
    // Native 004D84D9 consumes two bytes after the three u16 values, then Str8.
    public static byte[] Map(int map,int width,int height,string name,byte flags=0,bool portalPadding=false){
        var packet=new PacketWriter(0x15).U16(map).U16(width).U16(height).U8(flags).U8(0).Str8(name);
        if(portalPadding)packet.U8(0);
        return packet.Build();
    }
    public static byte[] MapCheck(byte phase) => phase==2?[0x14,phase,0]:[0x14,phase];
    internal static byte[] CrcChallenge(ushort challenge)=>new PacketWriter(0x3B).U16(challenge).Build();
    public static byte[] OpenTeleportMenu() => [0x6B, 0xFF, 0];
    public static byte[] BackgroundMusic(ushort track,bool portalPadding=false){
        var packet=new PacketWriter(0x19).U16(0x8000|track);
        if(portalPadding)packet.U8(0xFF).U16(0);
        return packet.Build();
    }
    internal static byte[] SoundEffect(ushort sound,bool padded=true){
        var packet=new PacketWriter(0x19).U16(sound);
        if(padded)packet.U8(0);
        return packet.Build();
    }
    public static byte[] WorldDay(byte month, byte day) => [0x1E, month, day, 0];
    internal static byte[] SpellList(Character character){
        var known=LocalSpell.All.Where(spell=>spell.IsKnown(character)).OrderBy(spell=>spell.WireSlot(character)).ToArray();
        var packet=new PacketWriter(0x17).U8(3).U8(known.Length);
        foreach(var spell in known)WriteSpell(packet,spell,character).U16(spell.Counter(character)).U16(spell.Reference);
        return packet.U8(0).Build();
    }
    internal static byte[] LearnSpell(LocalSpell spell,Character? character=null)=>
        WriteSpell(new PacketWriter(0x17).U8(1),spell,character).Build();
    internal static byte[] SpellProgress(Character character,LocalSpell spell)=>
        new PacketWriter(0x17).U8(2).U8(spell.WireSlot(character)).U16(spell.Counter(character)).U16(spell.Reference).Build();
    static PacketWriter WriteSpell(PacketWriter packet,LocalSpell spell,Character? character){
        byte[] costs=new byte[8];
        costs[spell.StoneIndex]=spell.StoneCost;
        costs[6]=spell.ManaCost;
        return packet.U8(spell.WireSlot(character)).U16(spell.Icon).U8(spell.TargetMode).Str8(spell.Name).Str8("EMPTY")
            .U8(0).U16(spell.CastDelayMs).Bytes(costs);
    }
    internal static byte[] CastSpell(uint caster,bool active)=>
        new PacketWriter(0x52).U32(caster).U8(active?1:0).U8(0).Build();
    public static byte[] WorldHour(byte hour,byte minute) => [0x20, hour, minute];
    internal static (byte[] Hour,byte[]? Day) WorldClockPulse(RecordedClock clock,long pulse){
        var date=clock.Read(clock.PulseTime(pulse));
        return (WorldHour(date.Hour,date.Minute),clock.IsHourlyPulse(pulse)?[0x1E,date.Day]:null);
    }
    public static byte[] Weather(byte kind, byte intensity) => [0x1F, kind, intensity];
    // Native 0047793C computes viewport origin = position - these offsets.
    // Existing S04/S0B captures consistently use (16,9), centering a 32x19 view.
    public const int CameraX=16,CameraY=9;
    public static byte[] Position(int x,int y,byte floor=0)=>
        new PacketWriter(4).U16(x).U16(y).U8(floor).U16(CameraX).U16(CameraY).U8(0).Build();
    // Captured S0B includes a zero after the movement tag, including corrections.
    public static byte[] Move(byte direction,int x,int y,byte movementTag,byte floor=0)=>
        new PacketWriter(0x0B).U8(direction).U16(x).U16(y).U8(floor).U16(CameraX).U16(CameraY).U8(movementTag).U8(0).Build();
    public static byte[] Turn(Character c)=>Turn(c.Id,c.Direction);
    public static byte[] Turn(uint id,byte direction)=>new PacketWriter(0x11).U32(id).U8(direction).Build();
    // Native codecs 004D459C / 004D473A.
    static PacketWriter ItemDescription(PacketWriter packet,EquipmentItem item)=>
        item.StatDescription is {} description?packet.U8(1).Str16(description):packet.U8(0);
    // v611 004D7EB4 reads a direct inventory frame index in S45.
    internal static byte[] CashShopList(Character character){
        var deliveries=(character.CashShopItems??[]).Where(row=>row.IsAvailable).Take(100).ToArray();
        var packet=new PacketWriter(0x45).U8(0).U8(deliveries.Length);
        foreach(var delivery in deliveries){
            var item=EquipmentItems.Get(delivery.Key);
            packet.U32(delivery.OrderId).U32(delivery.EntryId).U16(item.Sprite&0x7fff)
                .Str8(item.Name).Str8(item.Name).U32(delivery.Quantity);
            ItemDescription(packet,item).Str8((delivery.PurchasedOn??DateOnly.FromDateTime(DateTime.Today))
                .ToString("yyyyMMdd",System.Globalization.CultureInfo.InvariantCulture)).U32(0).U8(0).U32(0);
        }
        return packet.Build();
    }
    public static byte[] EquipmentAdd(EquipmentItem item,int equippedWeight) => ItemDescription(new PacketWriter(0x37)
        .U8(item.EquipmentSlot).U16(item.Sprite).Str8(item.Name).Str8(item.Name),item)
        .U16(Math.Clamp(equippedWeight,0,ushort.MaxValue)).U16(item.EquipmentWord2).U32(uint.MaxValue).U8(item.Flag).Build();
    internal static byte[] ShopRepairedEquipment(EquipmentItem item,int equippedWeight)=>[..EquipmentAdd(item,equippedWeight),0];
    public static byte[] InventoryAdd(ItemPlacement placement,ushort? carriedWeight=null,long? now=null){
        var item=EquipmentItems.Get(placement.Key);
        byte? durability=item.EquipmentSlot!=0?EquipmentItems.DurabilityPercent(placement):null;
        return InventoryAdd(placement.InventorySlot,item,placement.Quantity,placement.InventoryWord1,placement.InventoryWord2,durability,
            ElBlessing.IsItem(item)?item.Name.TrimEnd()+" ":Fishing.InventoryName(placement,item.Name),carriedWeight,
            placement.RemainingMinutes(now??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
    internal static byte[] InventoryDrag(ItemPlacement placement,ushort carriedWeight){
        var item=EquipmentItems.Get(placement.Key);
        var packet=InventoryAdd(placement,carriedWeight);
        // C30 samples for axes with the same sprite use different packet lengths.
        bool padded=(item.Sprite,item.Name) is (0x8003,"롱소드") or (0x8005,"키아의우든보우") or (0x8007,"바스타드소드")
            or (0x8017,"우든하프" or "키아의우든하프") or (0x82D1,"드베폴쳔" or "트리폴쳔")
            or (0x82D3,"티스배틀액스") or (0x8377,"스켈레톤의실드") or (0x8D20,"수련인형(2500G)") or (0xA798,"벌투구");
        return padded?[..packet,0]:packet;
    }
    internal static byte[][] PositionedInventoryAdd(Character character,ItemPlacement placement){
        ushort weight=EquipmentItems.CarriedWeight(character);
        return [InventoryAdd(placement with {InventoryWord1=0,InventoryWord2=0},weight),InventoryAdd(placement,weight)];
    }
    internal static bool AddInitialInventory(List<byte[]> packets,Character character,byte state,int dimension){
        var placements=character.Items??[];
        var equipped=placements.Where(p=>p.InventorySlot==0).OrderBy(p=>EquipmentItems.Get(p.Key).EquipmentSlot);
        var registered=new List<ItemPlacement>();
        bool restoreVitals=false;
        int equippedWeight=0;
        foreach(var placement in equipped){
            var item=EquipmentItems.Get(placement.Key);
            equippedWeight+=item.EquipmentWord2;
            byte[] equipment=[..EquipmentAdd(item,equippedWeight),0];
            packets.Add(equipment);
            packets.Add(equipment);
            registered.Add(placement);
            var partial=character with {Items=registered.ToArray()};
            if(registered.Count==1){
                uint currentHp=character.CurrentHp();
                uint currentMp=character.CurrentMp(character.MaxMp(EquipmentItems.EquippedEffects(character)));
                var partialEffects=EquipmentItems.EquippedEffects(partial).ToArray();
                uint firstHp=Math.Min(currentHp,partial.MaxHp(partialEffects));
                uint firstMp=Math.Min(currentMp,partial.MaxMp(partialEffects));
                restoreVitals=firstHp!=currentHp||firstMp!=currentMp;
                packets.Add(InitialStatus(partial with {Gold=0},state,dimension,firstHp,firstMp));
            }else packets.Add([..StatusForDimension(Status(partial,mask:0x40,state:state),dimension),0]);
        }
        var carried=placements.Where(p=>p.InventorySlot!=0).ToDictionary(p=>p.InventorySlot);
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long carriedWeight=0;
        for(byte slot=1;slot<=WorldSettings.InventorySlots;slot++){
            if(carried.TryGetValue(slot,out var item)){
                carriedWeight+=EquipmentItems.InventoryWeight(item);
                ushort weight=EquipmentItems.WeightWord(carriedWeight);
                packets.Add([..InventoryAdd(item with {InventoryWord1=0,InventoryWord2=0},weight,now),0]);
                var positioned=InventoryAdd(item,weight,now);
                packets.Add(ElBlessing.IsItem(EquipmentItems.Get(item.Key))?[..positioned,0]:positioned);
            }else if(slot==character.WalletSlot&&character.Gold!=0){
                ushort weight=EquipmentItems.WeightWord(carriedWeight);
                packets.Add(Wallet(character with {WalletX=0,WalletY=0},weight));
                packets.Add(Wallet(character,weight));
            }
        }
        return restoreVitals;
    }
    public static byte[] InventoryAdd(byte slot,EquipmentItem item,uint quantity=1,ushort? word1=null,ushort? word2=null,
        byte? durability=null,string? inventoryName=null,ushort? carriedWeight=null,uint remainingMinutes=uint.MaxValue) => ItemDescription(new PacketWriter(0x0F)
        .U8(slot).U16(item.Sprite).Str8(inventoryName??item.Name).Str8(item.Name).U32(quantity)
        .U16(word1??item.InventoryWord1).U16(word2??item.InventoryWord2).U8(item.InventoryKind??item.EquipmentSlot).U8(2).U8(item.InventoryFlag1)
        .U16(carriedWeight??item.InventoryWord3).U16(item.EquipmentWord2),item).U8(durability??item.InventoryByte3)
        .U32(remainingMinutes).U8(item.Flag).Build();
    internal static List<byte[]> PurchasedItem(Character buyer,ItemPlacement placement,ushort carriedWeight,bool newSlot,byte state,long now,
        (ushort X,ushort Y) previousPosition=default){
        var item=EquipmentItems.Get(placement.Key);
        // A recorded bastard sword purchase included base stats as well as the balance.
        byte mask=item.Sprite==0x8007?(byte)0x50:(byte)0x10;
        var status=buyer.SpeedPotionUntil>now?PotionBuffStatus(buyer,mask,padded:false,state:state)
            :Status(buyer,mask,state:state);
        var positioned=InventoryAdd(placement,carriedWeight);
        if(!newSlot)
            return [status,placement.Key==EquipmentItems.DaggerPoisonKey?positioned:[..positioned,0],positioned];
        var initial=InventoryAdd(placement with {InventoryWord1=previousPosition.X,InventoryWord2=previousPosition.Y},carriedWeight);
        // Confirmed S0F pairs from recorded shop purchases. Some item sprites are shared.
        var (padInitial,padPositioned)=(item.Sprite,item.Name) switch {
            (0x8017,"우든하프") or (0x8013 or 0x8014 or >=0x805F and <=0x8062 or 0x8C7A or
                >=0x8CD4 and <=0x8CD6 or 0x8D24,_)=>(false,false),
            (0x8BD7,"싸움닭(알)") or (0x801F or 0x805E or 0x8C03 or >=0x8F81 and <=0x8F83,_)=>(false,true),
            (0x8010,"수선세트") or (0x801D or 0x8BC9 or 0x8C1C or 0x8C30 or 0x8C75 or 0x8D34 or
                0x8D44 or 0x8EF0 or 0x8F08 or 0x8F18,_)=>(true,true),
            _=>(true,item.EquipmentSlot==1)
        };
        return [status,padInitial?[..initial,0]:initial,padPositioned?[..positioned,0]:positioned];
    }
    internal static List<byte[]> EquipmentStow(Character before,Character after,byte slot,ItemPlacement initial,
        ItemPlacement positioned,byte combatMode,uint vitalsAux,bool padInventory=false,uint? effectFlags=null,bool candle=false){
        byte[] EquipmentStatus(Character owner,byte mask,bool padded=true){
            if(owner.SpeedPotionUntil>0||owner.HealthPotionUntil>0)
                return PotionBuffStatus(owner,mask,padded:padded,state:combatMode,vitalsExtra:vitalsAux,effectFlags:effectFlags);
            var status=mask==0?new byte[]{8,0,combatMode}:Status(owner,mask,state:combatMode,vitalsExtra:vitalsAux);
            return padded?[..status,0]:status;
        }
        uint beforeMaxMp=before.MaxMp(EquipmentItems.EquippedEffects(before));
        uint afterMaxMp=after.MaxMp(EquipmentItems.EquippedEffects(after));
        bool vitalsChanged=before.CurrentHp()!=after.CurrentHp()||before.CurrentMp(beforeMaxMp)!=after.CurrentMp(afterMaxMp);
        ushort carriedWeight=EquipmentItems.CarriedWeight(after);
        var first=InventoryAdd(initial,carriedWeight);
        var second=InventoryAdd(positioned,carriedWeight);
        bool compact=candle&&!vitalsChanged&&before.SpeedPotionUntil==0&&before.HealthPotionUntil==0;
        bool compactCombat=compact&&combatMode==2;
        byte baseMask=compact?(byte)0:(byte)0x40;
        var prelude=EquipmentRemovalPrelude(before,after,EquipmentStatus(after,baseMask));
        var settled=EquipmentStatus(after,vitalsChanged?(byte)0x60:baseMask,padded:!compactCombat);
        return [..prelude,[..EquipmentRemove(slot,EquipmentItems.EquippedWeight(after)),0],
            Human(after),padInventory||compact&&!compactCombat?[..first,0]:first,padInventory||compactCombat?[..second,0]:second,settled];
    }
    internal static byte[][] EquipmentRemovalPrelude(Character before,Character after,byte[] status){
        if((status[1]&0x40)==0)return [status];
        // AC/MR already exclude the removed item, while the other base fields still show the old outfit.
        Status(before,0x40).AsSpan(3,42).CopyTo(status.AsSpan(3));
        if(!EquipmentItems.HasDryadSet(before)||EquipmentItems.HasDryadSet(after))return [status];
        // Losing the full set emits the item defense change before removing the set defense bonus.
        var itemOnly=status.ToArray();
        var bonus=EquipmentItems.DryadSetBonus;
        var effects=EquipmentItems.EquippedEffects(after).Append(bonus);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(itemOnly.AsSpan(45),EquipmentItems.PhysicalDefense(effects));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(itemOnly.AsSpan(47),EquipmentItems.MagicDefense(effects));
        return [itemOnly,status];
    }
    public static byte[] EquipmentRemove(byte slot,int equippedWeight) => new PacketWriter(0x38)
        .U8(slot).U16(Math.Clamp(equippedWeight,0,ushort.MaxValue)).Build();
    public static byte[] InventoryRemove(byte slot,ushort carriedWeight=0,byte reason=0x0C) => new PacketWriter(0x10).U8(slot).U8(reason).U16(carriedWeight).Build();
    internal static byte[] RasFeatherRemove(byte slot,ushort carriedWeight)=>InventoryRemove(slot,carriedWeight,reason:9);
    // The bank has its own item codec at 004D6718.
    static PacketWriter BankItem(PacketWriter packet,ItemPlacement placement){
        var item=EquipmentItems.Get(placement.Key);
        return ItemDescription(packet.U8(placement.InventorySlot).U16(item.Sprite).Str8(Fishing.InventoryName(placement,item.Name)).Str8(item.Name)
            .U32(placement.Quantity).U16(placement.InventoryWord1??item.InventoryWord1).U16(placement.InventoryWord2??item.InventoryWord2)
            .U8(item.InventoryFlag1),item)
            .U8(item.EquipmentSlot==0?item.InventoryByte3:EquipmentItems.DurabilityPercent(placement))
            .U32(placement.RemainingMinutes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).U8(item.Flag);
    }
    internal static byte[] BankContents(uint bankerId,ItemPlacement[] items){
        var packet=new PacketWriter(0x61).U8(2).U8(0).U32(bankerId).U8(items.Length);
        foreach(var item in items.OrderBy(item=>item.InventorySlot))BankItem(packet,item);
        return packet.Build();
    }
    internal static byte[] BankAdd(ItemPlacement item)=>BankItem(new PacketWriter(0x61).U8(2).U8(1),item).Build();
    internal static byte[] BankRemove(byte slot)=>[0x61,2,2,slot];
    internal static byte[] BankQuote(byte slot,uint quantity)=>new PacketWriter(0x61).U8(2).U8(3).U8(slot).U32(quantity).U32(0).Build();
    internal static byte[] Wallet(Character c,ushort? carriedWeight=null,bool padded=true){
        var packet=new PacketWriter(0x0F).U8(c.WalletSlot).U16(0x8BB8)
            .Str8("GALRID").Str8("골드코인").U32(c.Gold).U16(c.WalletX).U16(c.WalletY).U8(0).U8(1).U8(1)
            .U16(carriedWeight??EquipmentItems.CarriedWeight(c)).U16(0).U8(0).U8(byte.MaxValue).U32(uint.MaxValue).U8(0).Build();
        return padded?[..packet,0]:packet;
    }
    internal static byte[] InventoryBurn(byte slot,ushort carriedWeight,bool padded=true){
        var packet=new PacketWriter(0x10).U8(slot).U8(0).U16(carriedWeight);
        return padded?packet.U8(0).Build():packet.Build();
    }
    internal static byte[] InventoryDrop(byte slot,ushort carriedWeight)=>new PacketWriter(0x10).U8(slot).U8(1).U16(carriedWeight).Build();
    // The native world handler uses the object ID and direction. The v611
    // codec ends before the four-byte suffix seen on the official wire.
    public static byte[] Self(Character character)=>
        new PacketWriter(5).U32(character.Id).U16(0).U8(0).U8(character.Direction).U32(0).Build();
    public static byte[] Remove(uint id)=>new PacketWriter(0x0E).U32(id).Build();
    public static byte[] Pickup(uint itemId,uint pickerId)=>new PacketWriter(0x53).U32(itemId).U32(pickerId).Build();
    internal static (bool World,bool Initial,bool Positioned) PickupPadding(EquipmentItem item)=>(item.EquipmentSlot,item.Sprite) switch {
        // The recorded 6 ml healing-potion pickups pad S53/S0E and leave both S0F packets unpadded.
        (_,0x8BC6)=>(true,false,false),
        (_,0x8BE1)=>(true,false,true),
        (15,0x806D)=>(false,false,false),
        (15,0x806E)=>(false,false,true),
        (15,0x8070 or 0x8071 or 0x8072 or 0x8074 or 0x8076)=>(false,true,true),
        (15,0x8075 or 0x8078)=>(false,true,false),
        _=>(true,true,true)
    };
    public static byte[] CombatMode(uint id,byte mode)=>new PacketWriter(0x27).U32(id).U8(mode).Build();
    internal static byte[] EmptyPetSlot(byte slot)=>slot==1?[0x82,4,slot,0]:[0x82,4,slot];
    // 004D608C / 004326AE: count, gained counter, skill level, level bonus,
    // monster level and the current/maximum seconds on the combo panel.
    internal static byte[] Combo(byte count,ushort bonus,uint skillLevel,uint monsterLevel,ushort secondsLeft)=>new PacketWriter(0x85)
        .U8(count).U16(bonus).U32(skillLevel).U8(0).U32(monsterLevel).U16(secondsLeft).U16(WorldSession.ComboSeconds).U8(0).Build();
    public static byte[] MeleeEffect(uint attacker,uint target,ushort damage,byte variant=0,ushort motion=4)=>
        AttackEffect(attacker,target,damage,motion,0,0,variant).U8(0).Build();
    internal static byte[] BowAttack(uint attacker,uint target,ushort damage,byte heading,ushort motion)=>
        AttackEffect(attacker,target,damage,motion,38,1,heading,animation:1).Build();
    // Captured shaman spell uses a projectile effect and its own motion and sounds.
    internal static byte[] OrcShamanSpell(uint attacker,uint target,ushort damage)=>new PacketWriter(0x13)
        .U32(attacker).U32(target).U8(2).U8(9).U16(19).U16(61).U16(0).U16(1004)
        .U8(0).U8(1).U16(6).U16(damage).U16(1).U8(0).Build();
    internal static byte[] DaraHit(uint attacker,uint target,ushort damage)=>DamageSpellHit(attacker,target,damage,DaraSpell.Icon);
    internal static byte[] DamageSpellHit(uint attacker,uint target,ushort damage,ushort icon)=>new PacketWriter(0x13)
        .U32(attacker).U32(target).U8(2).U8(9).U16(19).U16(0).U16(0).U16(icon)
        .U8(0).U8(1).U16(0).U16(damage).U16(0).U8(0).Build();
    internal static byte[] DebuffHit(uint attacker,uint target,PlayerDebuff debuff)=>new PacketWriter(0x13)
        .U32(attacker).U32(target).U8(0).U8(9).U16(debuff.Motion).U16(58).U16(0).U16(0)
        .U8(0).U8(1).U16(debuff.Spell.Icon).U16(0).U16(ushort.MaxValue).U8(0).Build();
    public static byte[] PlayerAttack(uint attacker,uint target,ushort damage,ushort motion,ushort swingSound=9,ushort impactSound=1)=>
        AttackEffect(attacker,target,damage,motion,swingSound,1,0,impactSound:impactSound).Build();
    internal static byte[] BodyBlowMotion(uint caster,ushort motion)=>
        AttackEffect(caster,caster,0,motion,9,1,0,auxiliary:7).Build();
    internal static byte[] HarpAttack(uint player,uint target,ushort damage){
        // Retail 1:157432 and 1:157487 use the note cue and direct zero-damage motion at the player.
        var packet=AttackEffect(player,damage==0?player:target,damage,9,86,1,0,impactSound:0);
        return damage==0?packet.Build():packet.U8(0).Build();
    }
    // 0047C35D–0047C371 and 0043B1EC: the optional words are an auxiliary number,
    // the floating damage number, and a weapon-dependent impact sound.
    static PacketWriter AttackEffect(uint attacker,uint target,ushort damage,ushort motion,ushort swingSound,byte effect,byte variant,
        byte animation=0,ushort impactSound=1,ushort auxiliary=0)=>
        new PacketWriter(0x13).U32(attacker).U32(target).U8(animation).U8(effect)
        .U16(motion).U16(swingSound).U16(0).U16(0).U8(variant).U8(1)
        .U16(auxiliary).U16(damage).U16(damage>0?impactSound:ushort.MaxValue);
    internal static byte[] AbilityProgress(Character character,CombatAbility ability,ushort? counter=null,ushort? reference=null)=>
        new PacketWriter(0x2C).U8(2).U8(ability.Slot).U16(counter??CombatAbilities.Progress(character,ability))
            .U16(reference??CombatAbilities.Reference(character,ability)).Build();
    // 0047CA77 -> 0040103F -> 004B9AF0 selects the icon from the word after kindId.
    static PacketWriter WriteAbility(PacketWriter packet,Character character,CombatAbility ability)=>
        packet.U8(ability.Slot).U8(0).U16(ability.HeaderWord).U16(CombatAbilities.Mastery(character,ability))
            .U8(CombatAbilities.Grade(character,ability)).Str8(ability.Name).U8(0).U16(ability.IconIndex);
    internal static byte[] AbilityMastery(Character character,CombatAbility ability)=>
        WriteAbility(new PacketWriter(0x2C).U8(1),character,ability).Build();
    internal static byte[][] GreatswordLearned(Character character){
        var ability=CombatAbilities.Greatsword;
        var mastery=AbilityMastery(character,ability);
        var progress=AbilityProgress(character,ability);
        var notice=Message("대검 어빌리티를 습득했습니다");
        return CombatAbilities.Reference(character,ability)==CombatAbilities.BastardSwordReference
            ?[notice,mastery,[..progress,0],mastery,progress]
            :[notice,[..mastery,0],[..progress,0],mastery,[..progress,0]];
    }
    static PacketWriter WriteFishingAbility(PacketWriter packet,Character character)=>packet
        .U8(Fishing.Slot).U8(0).U16(35).U16(Fishing.Mastery(character))
        .U8(Fishing.Grade(character)).Str8("낚시").U8(5).U16(10);
    internal static byte[] FishingMastery(Character character,bool padded=true){
        var packet=WriteFishingAbility(new PacketWriter(0x2C).U8(1),character);
        return padded?packet.U8(0).Build():packet.Build();
    }
    internal static byte[] FishingProgress(Character character){
        ushort reference=Fishing.CounterReference(character);
        return new PacketWriter(0x2C)
            .U8(2).U8(Fishing.Slot).U16((ushort)(character.FishingPoints%reference)).U16(reference).U8(0).Build();
    }
    internal static byte[][] FishingLearned(Character character){
        var progress=FishingProgress(character);
        return [Message("낚시 어빌리티를 습득했습니다"),FishingMastery(character),progress,
            FishingMastery(character,padded:false),progress];
    }
    internal static byte[] FishingMotion(uint player)=>AttackEffect(player,player,0,Fishing.CastFrames,0,1,0).U8(0).Build();
    internal static byte[] FishingStatus()=>[8,0,2,0];
    internal static List<byte[]> FishingCastResult(Character before,Character after,byte[] baitUpdate,byte[] motion,
        IReadOnlyList<ItemPlacement> catches,Func<byte,(ushort X,ushort Y)> previousPosition){
        var packets=new List<byte[]>{FishingStatus(),baitUpdate};
        if(catches.Count==0)packets.Add(Message("낚시에 실패했어요."));
        else{
            string key=catches[0].Key;
            string name=EquipmentItems.Get(key).Name;
            int quantity=Fishing.CatchQuantity(before,key);
            string particle=(name[^1]-'가')%28==0?"를":"을";
            packets.Add(Message(quantity==1?$"{name}{particle} 낚았습니다.":$"{name} {quantity}마리를 낚았습니다."));
            ushort weight=EquipmentItems.CarriedWeight(after);
            foreach(var caught in catches){
                var (x,y)=previousPosition(caught.InventorySlot);
                var initial=caught with {InventoryWord1=x,InventoryWord2=y};
                var initialPacket=InventoryAdd(initial,weight);
                // 1:84804–84805: the minnow's first refresh is unpadded, its placement is padded.
                packets.Add(key==Fishing.MinnowKey?initialPacket:[..initialPacket,0]);
                packets.Add([..InventoryAdd(caught,weight),0]);
            }
        }
        var progress=FishingProgress(after);
        packets.Add(motion);
        packets.Add(progress);
        if(Fishing.Mastery(after)!=Fishing.Mastery(before)){
            packets.Add([8,0,2]);
            packets.Add(FishingMastery(after));
            packets.Add(progress);
        }
        return packets;
    }
    static PacketWriter WriteSenseAbility(PacketWriter packet,ushort mastery)=>packet
        .U8(SenseTraining.Slot).U8(SenseTraining.Category).U16(0).U16(mastery)
        .U8(0).Str8("Sense").U8(SenseTraining.TargetKind).U16(SenseTraining.Icon);
    internal static byte[] SenseAbility(ushort mastery)=>WriteSenseAbility(new PacketWriter(0x2C).U8(1),mastery).Build();
    internal static byte[] SenseProgress()=>new PacketWriter(0x2C).U8(2).U8(SenseTraining.Slot).U16(0).U16(1).Build();
    internal static byte[] SenseResult(string text)=>new PacketWriter(0x0A).U8(4).Str16(text).Build();
    static PacketWriter WriteMeditation(PacketWriter packet,uint counters)=>packet
        .U8(MeditationTraining.Slot).U8(MeditationTraining.Category).U16(MeditationTraining.Header)
        .U16(MeditationTraining.Mastery(counters)).U8(0).Str8(MeditationTraining.Name)
        .U8(MeditationTraining.ActionKind).U16(MeditationTraining.Icon);
    internal static byte[] MeditationAbility(uint counters)=>WriteMeditation(new PacketWriter(0x2C).U8(1),counters).Build();
    internal static byte[] MeditationProgress(uint counters)=>new PacketWriter(0x2C).U8(2).U8(MeditationTraining.Slot)
        .U16((ushort)(counters%MeditationTraining.Reference)).U16(MeditationTraining.Reference).Build();
    static PacketWriter WriteGrepaSkill(PacketWriter packet,GrepaCombatSkill skill,uint counters)=>packet
        .U8(skill.Slot).U8(3).U16(30).U16(GrepaCombatSkill.Mastery(counters)).U8(0).Str8(skill.Name).U8(5).U16(skill.Icon);
    internal static byte[] GrepaSkillAbility(GrepaCombatSkill skill,uint counters)=>
        WriteGrepaSkill(new PacketWriter(0x2C).U8(1),skill,counters).Build();
    internal static byte[] GrepaSkillProgress(GrepaCombatSkill skill,uint counters)=>new PacketWriter(0x2C).U8(2).U8(skill.Slot)
        .U16((ushort)(counters%GrepaCombatSkill.Reference)).U16(GrepaCombatSkill.Reference).Build();
    static PacketWriter WriteConversation(PacketWriter packet,uint counters,byte rank)=>packet.U8(ConversationTraining.Slot).U8(3)
        .U16(30).U16(ConversationTraining.Mastery(counters,rank)).U8(rank).Str8(ConversationTraining.Name).U8(5).U16(ConversationTraining.Icon);
    internal static byte[] ConversationAbility(uint counters,byte rank=0)=>
        WriteConversation(new PacketWriter(0x2C).U8(1),counters,rank).Build();
    internal static byte[] ConversationProgress(uint counters,byte rank=0){
        ushort reference=ConversationTraining.CounterReference(rank);
        return new PacketWriter(0x2C).U8(2).U8(ConversationTraining.Slot).U16((ushort)(counters%reference)).U16(reference).Build();
    }
    internal static byte[] AbilityList(Character character){
        CombatAbility[] abilities=[..CombatAbilities.All.Concat(CombatAbilities.Optional).Where(ability=>CombatAbilities.Has(character,ability)),
            ..LocalSpell.Elements.Where(element=>CombatAbilities.Points(character,element)>0)];
        var learned=GrepaCombatSkill.All.Where(skill=>skill.Counters(character).HasValue).ToArray();
        var packet=new PacketWriter(0x2C).U8(3).U8(abilities.Length+(character.SenseMastery.HasValue?1:0)+
            (character.MeditationCounters.HasValue?1:0)+(character.ConversationCounters.HasValue?1:0)+
            (character.FishingLearned?1:0)+learned.Length);
        foreach(var ability in abilities)
            WriteAbility(packet,character,ability).U16(CombatAbilities.Progress(character,ability)).U16(CombatAbilities.Reference(character,ability));
        if(character.FishingLearned){
            ushort reference=Fishing.CounterReference(character);
            WriteFishingAbility(packet,character).U16((ushort)(character.FishingPoints%reference)).U16(reference);
        }
        if(character.SenseMastery is ushort sense)WriteSenseAbility(packet,sense).U16(0).U16(1);
        if(character.MeditationCounters is uint meditation)WriteMeditation(packet,meditation)
            .U16((ushort)(meditation%MeditationTraining.Reference)).U16(MeditationTraining.Reference);
        if(character.ConversationCounters is uint conversation){
            byte rank=ShrineGrade.Conversation.Rank(character);
            ushort reference=ConversationTraining.CounterReference(rank);
            WriteConversation(packet,conversation,rank).U16((ushort)(conversation%reference)).U16(reference);
        }
        foreach(var skill in learned){
            uint counters=skill.Counters(character)!.Value;
            WriteGrepaSkill(packet,skill,counters).U16((ushort)(counters%GrepaCombatSkill.Reference)).U16(GrepaCombatSkill.Reference);
        }
        return packet.Build();
    }
    public static byte[] PartyMember(byte slot,string name,byte hp,bool leader)=>new PacketWriter(0x5D)
        .U8(0).U8(slot).Str8(name).U8(hp).U8(leader?1:0).Build();
    public static byte[] PartyVitals(byte slot,byte hp)=>[0x5D,1,slot,hp];
    internal static byte[] PartyRemove(byte slot)=>[0x5D,2,slot];
    public static byte[] PartyClear()=>[0x5D,3];
    internal static byte[] PartyAllowed(bool allowed)=>[0x5D,4,allowed?(byte)1:(byte)0];
    internal static byte[] FriendList((string Name,bool Online)[] friends,bool padded=false){
        var packet=new PacketWriter(0x6A).U8(0).U8(friends.Length);
        foreach(var friend in friends)packet.Str8(friend.Name).U8(friend.Online?1:0);
        return padded?packet.U8(0).Build():packet.Build();
    }
    public static byte[] Tell(string name,string text,bool outgoing=false)=>new PacketWriter(0x0A).U8(0)
        .Str16(outgoing?$"{name}> {text}":$"{name}: {text}").Build();
    internal static byte[] PrivateChat(string name,string text)=>new PacketWriter(0x0D).U8(5)
        .U8(0xF0).Str8(name).Str8(text).U8(0).Build();
    public static byte[] Whisper(string name,string text)=>new PacketWriter(0x57).U8(3).Str8(name).Str16(text).Build();
    public static byte[] WhisperOpen(string name)=>new PacketWriter(0x57).U8(0).Str8(name).Build();
    internal static byte[] TimedSpell(TimedSpellData spell,bool padded=true){
        var packet=new PacketWriter(0x3A).U16(spell.Id).Str8(spell.Name).U16(spell.Duration);
        if(padded)packet.U8(0);
        return packet.Build();
    }
    internal static byte[] RevivalStun(ushort seconds)=>TimedSpell(new(22,"Stun",seconds));
    internal static byte[] RevivalVitals(Character character,byte state,uint? currentHp=null,bool padded=true,bool includeMaxima=false){
        var packet=includeMaxima?Status(character,0x60,state,currentHp,vitalsExtra:1000)
            :CurrentVitals(character,state,currentHp,vitalsExtra:1000);
        return padded?[..packet,0]:packet;
    }
    // S0A mode 8 creates a popup.
    internal static byte[] RevivalNotice()=>new PacketWriter(0x0A).U8(8).Str16("성당에서 부활했습니다.").Build();
    // The S08 status transition to 0x20 invokes the native blue map filter at 004B8898.
    internal static byte[] RevivalScreen()=>[8,0,0x20,0];
    internal static byte[] ObjectEffect(uint player,ushort effect,ushort parameter=0)=>
        new PacketWriter(0x29).U32(player).U16(effect).U16(parameter).Build();
    internal static byte[] RevivalStunEffect(uint player,bool padded=false){
        var effect=ObjectEffect(player,22,100);
        return padded?[..effect,0]:effect;
    }
    internal static List<byte[]> RevivalExit(Character downed){
        bool blessed=downed.ElBlessingUntil>0;
        bool recordedGuildClear=blessed&&downed.Map is 2221 or 2222;
        List<byte[]> packets=[Human(downed with {AwaitingRevival=false})];
        if(!recordedGuildClear||downed.Map==2221)packets.Add([8,0,1]);
        if(!recordedGuildClear||downed.Map==2222)packets.Add([8,0,1,0]);
        if(blessed){
            var clear=ElBlessing.Timer(0);
            packets.Add(TimedSpell(clear,padded:!recordedGuildClear||downed.Map==2222));
            packets.Add(TimedSpell(clear));
        }
        packets.Add(RevivalNotice());
        return packets;
    }
    internal static List<byte[]> GuildNpcRetaliation(Character victim,byte state,int dimension,uint auxiliary){
        var effect=ObjectEffect(victim.Id,8,100);
        return [PaddedStatus(victim,dimension==0?(byte)0x20:(byte)0x60,state,auxiliary),
            dimension==0?effect:[..effect,0],SoundEffect(58,padded:false)];
    }
    internal static byte[] InnRecoveryMotion(uint npc,uint player)=>new PacketWriter(0x13)
        .U32(npc).U32(player).Bytes(Convert.FromHexString("00090018001B000003EA0001000E0000FFFF00")).Build();
    internal static byte[] FieldStrikeMotion(uint monster,bool padded=true){
        var packet=new PacketWriter(0x1A).U32(monster).U8(1).U16(20).U16(4).U16(9);
        return (padded?packet.U8(0):packet).Build();
    }
    internal static byte[] Emotion(uint player,byte motion)=>new PacketWriter(0x1A).U32(player).U8(motion)
        .U16(0).U16(0).U16(ushort.MaxValue).Build();
    public static byte[] DaggerPoisonEffect(uint player)=>ObjectEffect(player,11);
    public static byte[] DaggerPoisonMotion(uint player)=>new PacketWriter(0x13)
        .U32(player).U32(player).U8(0).U8(9).U16(20).U16(44).U16(0).U16(0).U8(0).U8(1)
        .U16(11).U16(0).U16(ushort.MaxValue).Build();
    public static byte[] ConsumableRemove(byte slot,ushort word,byte mode=6,bool padded=false){
        var packet=new PacketWriter(0x10).U8(slot).U8(mode).U16(word).Build();
        return padded?[..packet,0]:packet;
    }
    internal static byte[] PotionUseSound()=>SoundEffect(64,padded:false);
    public static byte[] Death(uint id)=>new PacketWriter(0x5F).U32(id).Build();
    public static byte[] ObjectMove(Character previous,byte direction,byte motion=2)=>
        ObjectMove(previous.Id,previous.X,previous.Y,previous.Floor,direction,motion);
    public static byte[] ObjectMove(uint id,int x,int y,byte floor,byte direction,byte motion)=>
        new PacketWriter(0x0C).U32(id).U16(x).U16(y).U8(floor).U8(direction).U8(motion).Build();
    public static byte[] Say(Character c,byte color,string text){
        // The native speech handler logs this string verbatim and skips ": "
        // after the sender name when it builds the overhead bubble.
        Span<byte> encoded=stackalloc byte[255];
        Encoding.GetEncoding(949).GetEncoder().Convert($"{c.Name} : {text}",encoded,true,out _,out int length,out _);
        return new PacketWriter(0x0D).U8(4).U8(color).U32(c.Id).U8(length).Bytes(encoded[..length]).U8(0).Build();
    }
    public static byte[] Message(string text)=>new PacketWriter(0x0A).U8(3).Str16(text).Build();
    internal static byte[] ShopRepairReceipt(IEnumerable<(string Name,ulong Price)> repairs,ulong total){
        var lines=repairs.Select(row=>FormattableString.Invariant($"{row.Name}: {row.Price}"));
        return Message(string.Join('\n',lines)+FormattableString.Invariant($"\n합계 {total} 갈리드\n"));
    }
    internal static byte[] ShopNotice(byte windowId,bool completed,string text){
        // The codec compares the low byte of the first decoded character with the active shop window.
        char route=(char)(windowId<0x80?windowId:0xAC00+windowId);
        return new PacketWriter(0x0A).U8(0x0E).Str16($"{route}{(completed?'1':'0')}{text}").Build();
    }
    internal static byte[] WeaponBrokenNotice()=>Message("무기가 망가졌습니다.");
    public static byte[] Objects(LocalNpc[] npcs,FieldObject[]? field=null){
        var p=new PacketWriter(7).U16(npcs.Length+(field?.Length??0));
        foreach(var n in npcs){
            WriteObject(p,n.X,n.Y,n.Floor,n.Id,n.Sprite,n.Flags,n.Type,n.Name,n.Category);
            if(n.Category==2)p.Str8(n.Text);
        }
        foreach(var o in field??[]){
            WriteObject(p,o.X,o.Y,o.Floor,o.Id,o.Sprite,o.Flags,o.Type,o.Name,o.Category);
            if(o.Category==3)p.U32(o.Quantity);
            else if(o.Category==6)p.Str8(o.Invitation??"");
        }
        return p.Build();
    }
    // SMercPacket mode 3, from C43 inspection of the placed level 3 sign.
    internal static byte[] RoomSignDialog(uint id,ushort minutes,bool owner,bool open)=>
        new PacketWriter(0x4F).U8(3).U32(id).U8(owner?1:0).U8(open?1:0).U16(minutes).Build();
    internal static byte[] PersonalRoomControls(bool owner)=>[0x64,3,0,owner?(byte)1:(byte)0,0];
    internal static byte[] PersonalRoomExitList(int map)=>new PacketWriter(0x64).U8(3).U8(1).U8(1).U16(map).U8(0).Build();
    internal static byte[] ClosePersonalRoomControls()=>[0x64,3,2];
    // Native codecs read only the selectors. The capture includes trailing bytes.
    internal static byte[] BlockClientForHandoff()=>[0x51,1,0,0,0];
    static void WriteObject(PacketWriter p,int x,int y,byte floor,uint id,ushort sprite,byte flags,byte type,string name,byte category)=>
        p.U16(x).U16(y).U8(floor).U32(id).U16(sprite).U8(flags).U8(type).Str8(name).U8(category);
    public static byte[] NpcDialog(LocalNpc npc,NpcDialogData? observed=null){
        var dialog=observed??new(0,1,1,npc.Sprite,0,0x01000000U|((uint)npc.Sprite<<8),npc.Text);
        var packet=new PacketWriter(0x2F).U8(dialog.Mode).U8(dialog.Flags).U32(npc.Id)
            .U8(dialog.PortraitKind).U16(dialog.Sprite).U8(dialog.Color).U32(dialog.HeaderData).Str16(dialog.Text);
        switch(dialog.Mode){
            case 1:
                packet.Str8(dialog.Prompt??"");
                goto case 0;
            case 0:
                var options=dialog.Options??[];
                packet.U8(options.Length);
                foreach(var option in options)packet.Str8(option.Text).U16(option.Id);
                break;
            case 12:
                packet.U16(dialog.ActionCode);
                if(dialog.ActionCode is 0x83 or 0x85 or 0x86)packet.Str8(dialog.ActionText??"");
                break;
            case 13:
                packet.U16(dialog.X).U16(dialog.Y);
                foreach(var code in dialog.MenuCodes??[])packet.U16(code);
                break;
        }
        return packet.Build();
    }
    internal static byte[]? NpcPursuit(LocalNpc npc,string key){
        if(npc.PursuitReplies is not {Length:>0} replies)return null;
        var observed=replies.FirstOrDefault(r=>r.Request==key);
        if(observed==null)return null;
        var packet=observed.Packet.ToArray();
        if(packet.Length<21||packet[0]!=0x30||packet[1]>5)return null;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(3),npc.Id);
        return packet;
    }
    internal static byte[] Pursuit(uint speakerId,string text,string[]? options,bool back,bool next){
        var packet=PursuitHeader(options==null?0:2,speakerId,text,back,next);
        if(options!=null){
            packet.U8(options.Length);
            foreach(string option in options)packet.Str8(option);
        }
        return packet.Build();
    }
    internal static byte[] NpcPursuitPage(LocalNpc npc,string text,string[]? options=null,ushort page=1,bool next=false,ushort script=0,bool back=false){
        var packet=NpcPursuitHeader(npc,options==null?0:2,text,page,next,script,back);
        if(options!=null){
            packet.U8(options.Length);
            foreach(var option in options)packet.Str8(option);
        }
        return packet.Build();
    }
    internal static byte[] NpcPursuitInput(LocalNpc npc,string text,ushort page,string prompt,byte limit,ushort script=0)=>
        NpcPursuitHeader(npc,4,text,page,true,script).Str8(prompt).U8(limit).Str8("").Build();
    static PacketWriter NpcPursuitHeader(LocalNpc npc,int mode,string text,ushort page,bool next,ushort script,bool back=false)=>
        new PacketWriter(0x30).U8(mode).U8(1).U32(npc.Id).U8(1).U16(npc.Sprite).U8(0).U32(0)
            .U16(script).U16(page).U8(back?1:0).U8(next?1:0).Str16(text);
    internal static byte[] PursuitInput(uint speakerId,string text,string prompt,byte limit,string hint)=>
        PursuitHeader(4,speakerId,text,true,true).Str8(prompt).U8(limit).Str8(hint).Build();
    static PacketWriter PursuitHeader(int mode,uint speakerId,string text,bool back,bool next)=>
        new PacketWriter(0x30).U8(mode).U8(4).U32(speakerId)
            .U8(0).U16(0).U8(0).U32(0).U16(0).U16(1).U8(back?1:0).U8(next?1:0).Str16(text);
    internal static byte[] JobList(Character character,bool portalPadding=false){
        var jobs=new Dictionary<string,byte>(character.JobPoints??[]);
        if(character.Job is {} job)jobs.TryAdd(job,0);
        // Captures 1:7344 and 1:122432 list commoner followed by each job and its points.
        var packet=new PacketWriter(0x55).U8(jobs.Count+1).U8(0).Str8("평민");
        foreach(var (name,points) in jobs)packet.U8(points).Str8(name);
        if(portalPadding)packet.U8(0);
        return packet.Build();
    }
    static PacketWriter ShopHeader(LocalNpc npc,byte mode,string text)=>new PacketWriter(0x2F).U8(mode).U8(npc.ShopFlags)
        .U32(npc.Id).U8(1).U16(npc.Sprite).U8(0).U8(1).U16(npc.Sprite).U8(0).Str16(text);
    public const ushort ShopBuyAction=0x40, ShopSellAction=0x41, ShopRepairAction=0x47,
        ShopBuyOrder=0x4B, ShopSellOrder=0x4E, ShopRepairOrder=0x5B;
    public static byte[] ShopMenu(LocalNpc npc)=>ShopHeader(npc,13,"").U16(npc.X).U16(npc.Y).U16(ShopBuyAction)
        .U16(ShopSellAction).U16(0).U16(npc.Category==2?0:0x6F).Build();
    public static byte[] ShopInventory(LocalNpc npc){
        var entries=npc.Shop??[];
        var packet=ShopHeader(npc,10,npc.Name).U16(ShopBuyOrder).U16(entries.Length);
        foreach(var entry in entries){
            var item=EquipmentItems.Get(entry.Key);
            if(npc.UsesExtendedShop)packet.U32(entry.Id);
            packet.U16(item.Sprite).U32(entry.Price).U8(entry.Flags).Str8(item.Name);
            string? description=entry.Description??item.StatDescription;
            if(npc.UsesExtendedShop){
                packet.U8(description is null?0:1);
                if(description!=null)packet.Str16(description);
                byte durability=npc.Category==2&&entry.Durability is ushort remaining
                    ?EquipmentItems.DurabilityPercent(remaining):byte.MaxValue;
                packet.U8(durability).U32(uint.MaxValue);
            }else{
                if(description!=null&&Encoding.GetEncoding(949).GetByteCount(description)>byte.MaxValue)
                    description=null;
                packet.Str8(entry.DisplayName??item.Name).Str8(description??" ");
            }
        }
        return packet.U8(0).Build();
    }
    internal static byte[] ShopSellList(LocalNpc npc,ShopOffer[] offers){
        var packet=ShopHeader(npc,11,npc.Name).U16(ShopSellOrder).U8(offers.Length);
        foreach(var offer in offers)packet.U8(offer.Slot).U32(offer.Price);
        return packet.U8(0).Build();
    }
    internal static byte[] ShopRepairList(LocalNpc npc,ShopOffer[] offers){
        var packet=ShopHeader(npc,14,npc.Name).U16(ShopRepairOrder).U8(offers.Length);
        foreach(var offer in offers){
            packet.U8(offer.Kind).U8(offer.Slot).U32(offer.Price);
            if(offer.Kind==1)packet.Str8(EquipmentItems.Get(offer.Item.Key).Name);
        }
        return packet.U8(0).Build();
    }
    public static byte[] Gold(uint gold,byte state=0)=>new PacketWriter(8).U8(0x10).U8(state).U32(gold).Build();
    internal static byte[] CitizenshipStatus(Character character,bool includeBase)=>includeBase
        ? [..Status(character,mask:0x50,state:1),0]
        : new PacketWriter(8).U8(0x10).U8(1).U32(character.Gold).U8(0).Build();
    internal static byte[] CitizenshipSound()=>new PacketWriter(0x19).U16(70).U16(0).Build();
    public static byte[] ExchangeOpen(Character partner)=>new PacketWriter(0x42).U8(0)
        .U32(partner.Id).Str8(partner.Name).U16(0).Build();
    public static byte[] ExchangeQuantity(byte slot)=>new PacketWriter(0x42).U8(1).U8(slot).Build();
    public static byte[] ExchangeItem(bool partner,byte index,ItemPlacement placement){
        var item=EquipmentItems.Get(placement.Key);
        return new PacketWriter(0x42).U8(2).U8(partner?1:0).U8(index).U16(item.Sprite)
            .Str8(Fishing.InventoryName(placement,item.Name)).Str8(item.Name).U32(placement.Quantity).U8(0)
            .U8(byte.MaxValue).U32(uint.MaxValue).Build();
    }
    public static byte[] ExchangeClose(string text)=>new PacketWriter(0x42).U8(4).U8(0).Str8(text).Build();
    public static byte[] ExchangeConfirm(bool partner,string text="")=>new PacketWriter(0x42).U8(5)
        .U8(partner?1:0).Str8(text).Build();
    // The v611 window builds its crafting controls only for kinds 2 and 5.
    public const byte CraftKind=5,CraftCode=1;
    static PacketWriter CraftHeader(byte mode)=>new PacketWriter(0x6E).U8(CraftKind).U8(CraftCode).U8(mode);
    public static byte[] CraftOpen(uint player,uint station)=>CraftHeader(0).U32(player).U16(0).U32(station).U8(1).Build();
    public static byte[] CraftRecipe()=>CraftRecipe(CookingRecipe.Egg);
    internal static byte[] CraftRecipe(CookingRecipe recipe)=>CraftHeader(1).U8(0)
        .Str8(recipe.Name).U16(EquipmentItems.Get(recipe.Key).Sprite).Str16($"재료: {recipe.Ingredients}")
        .Str16(recipe==CookingRecipe.Egg?"달걀을 삶습니다.":$"{recipe.Name}을 만듭니다.").U8(0).U8(0).Build();
    public static byte[] CraftResultPreview()=>CraftResultPreview(CookingRecipe.Egg);
    internal static byte[] CraftResultPreview(CookingRecipe recipe)=>CraftHeader(2).Str8(recipe.Name)
        .U16(EquipmentItems.Get(recipe.Key).Sprite).Build();
    public static byte[] CraftClose()=>CraftHeader(7).Build();
    public static byte[] NpcInfo(LocalNpc n){
        var p=new PacketWriter(0x34).U32(n.Id).U8(1).Str8(n.Name).Str8("로컬 테스트 안내 NPC").U16(0).U8(0);
        for(int i=0;i<15;i++)p.U16(0);
        return p.U8(0).U8(0).Build();
    }
    // 0045F909 maps the 15 S34 words to these equipment slots. The eighth
    // word is consumed by the codec and skipped by the character panel.
    static readonly byte[] InspectedEquipmentSlots=[1,2,3,4,6,7,8,0,13,14,15,16,20,23,24];
    internal static byte[] InspectCharacter(Character c){
        var equipped=(c.Items??[]).Where(item=>item.InventorySlot==0).Select(item=>EquipmentItems.Get(item.Key))
            .ToLookup(item=>item.EquipmentSlot);
        var packet=new PacketWriter(0x34).U32(c.Id).U8(0).Str8(c.GuildName??"").Str8(c.Job??"").U16(0).U8(1);
        foreach(byte slot in InspectedEquipmentSlots)packet.U16(slot==0?0:equipped[slot].FirstOrDefault()?.Sprite??0);
        return packet.U8(1).U8(CombatAbilities.Level(CombatAbilities.Experience(c))).U8(0).Build();
    }
    // The native relationship pane takes mode 1 for self and 2 for another character.
    internal static byte[] RelationshipInfo(uint id,byte mode)=>new PacketWriter(0x69).U8(mode).U32(id).U8(0).Build();
    // 004D5821 reads the summary. Mode 1 adds the roster read by 004D5867.
    internal static byte[] ClanStatus(string name,string master,(string Name,bool Online)[] members,bool includeRoster){
        var packet=new PacketWriter(0x3D).U8(includeRoster?1:0).Str8(name).Str8(master)
            .U8(0).U16(name.Length==0?0:1).U32(0);
        if(includeRoster){
            packet.U16(members.Length);
            foreach(var member in members)packet.Str8(member.Name).U8(member.Online?1:0);
        }
        return packet.Build();
    }
    public static byte[] ServerList(int port,bool secure=false){
        // Native bootstrap parser 004C81A8: zlib data, count, index,
        // reversed IPv4, BE port, NUL-terminated CP949 name;description.
        using var payload=new MemoryStream();payload.WriteByte(1);
        for(int index=0;index<1;index++) {
            payload.Write(new byte[]{(byte)index,1,0,0,127,(byte)(port>>8),(byte)port});
            payload.Write(Encoding.GetEncoding(949).GetBytes(secure?"일랜시아;보안 접속":"로컬;test / test"));payload.WriteByte(0);
        }
        using var compressed=new MemoryStream();
        using(var z=new ZLibStream(compressed,CompressionLevel.SmallestSize,true))z.Write(payload.ToArray());
        return new PacketWriter(0x56).U16((int)compressed.Length).Bytes(compressed.ToArray()).Build();
    }
}
