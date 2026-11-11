using System.Text;
using System.Text.RegularExpressions;

namespace ElanciaLocal;

public sealed record ItemPlacement(string Key, byte InventorySlot, uint Quantity = 1,
    ushort? InventoryWord1 = null, ushort? InventoryWord2 = null, ushort? Durability = null,
    ushort? BaitRemaining = null){
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? NativeDurability { get; init; }
    internal bool IsExpired(long now)=>ExpiresAt is long deadline&&deadline<=now;
    internal uint RemainingMinutes(long now)=>ExpiresAt is long deadline
        ?(uint)Math.Clamp(Math.Ceiling(((double)deadline-now)/60000),0,uint.MaxValue-1):uint.MaxValue;
}

public sealed record EquipmentEffect(int Hp = 0, int Mp = 0, int Str = 0, int Int = 0,
    int Vit = 0, int Agi = 0, int Ac = 0, int Mr = 0, int HealthRecovery = 0, int ManaRecovery = 0);
public sealed record TimedSpellData(ushort Id,string Name,ushort Duration);

public sealed record EquipmentItem(
    string Name, ushort Sprite, byte EquipmentSlot, int AppearanceLayer, ushort AppearanceWord,
    ushort EquipmentWord1, ushort EquipmentWord2, ushort InventoryWord1, ushort InventoryWord2,
    ushort InventoryWord3, byte InventoryByte3, byte Flag = 0, EquipmentEffect? Effect = null,
    byte? InventoryKind = null, byte InventoryFlag1 = 0, uint RestoreHp = 0,
    string? StatDescription = null, uint RestoreSatiety = 0, TimedSpellData? Timer = null, uint RestoreMp = 0,
    ushort? LocalAppearanceWord = null);

public static class EquipmentItems
{
    // Save files and shop listings use a per-thousand ratio. Native maxima vary by item.
    internal const ushort FullDurability=1000;
    static readonly Lazy<IReadOnlyDictionary<string,MetadataCache.ItemDefaults>> NativeDefaults=new(MetadataCache.NativeItemDefaults);
    static volatile IReadOnlyDictionary<string,MetadataCache.ItemDefaults>? configuredDefaults;
    static IReadOnlyDictionary<string,MetadataCache.ItemDefaults> NativeItems=>configuredDefaults??NativeDefaults.Value;
    internal static void UseNativeMetadata(MetadataCache metadata)=>configuredDefaults=metadata.ItemData();
    internal const string DaggerPoisonKey="recorded-8f07-b4dcb0cbbfebb5b6284c763129";
    internal static TimedSpellData DaggerPoisonTimer=>Get(DaggerPoisonKey).Timer??new(18,"단검독(Lv1)",600);

    internal static ItemPlacement? EquippedWeapon(Character character)=>
        (character.Items??[]).FirstOrDefault(p=>p.InventorySlot==0&&Get(p.Key).EquipmentSlot==1);

    internal static bool UsesBothHands(EquipmentItem item)=>
        item.EquipmentSlot==1&&(item.AppearanceWord>>12) is 2 or 4 or 5 or 6 or 8 or 10 or 11;

    internal const string TisBattleAxeKey="recorded-82d3-c6bcbdbab9e8c6b2bed7bdba";

    internal static ushort Durability(ItemPlacement placement)=>
        Math.Min(placement.Durability??FullDurability,FullDurability);
    internal static ushort MaximumDurability(EquipmentItem item)=>
        item.EquipmentSlot==0?FullDurability:NativeItems.GetValueOrDefault(item.Name)?.MaximumDurability??FullDurability;
    internal static ushort CurrentDurability(ItemPlacement placement,ushort maximum){
        ushort normalized=Durability(placement);
        if(placement.NativeDurability is ushort native&&native<=maximum&&NormalizeDurability(native,maximum)==normalized)
            return native;
        if(normalized==0)return 0;
        return (ushort)Math.Max(1,normalized*maximum/FullDurability);
    }
    static ushort NormalizeDurability(ushort native,ushort maximum)=>
        (ushort)(((uint)native*FullDurability+maximum-1)/maximum);
    internal static ItemPlacement WithCurrentDurability(ItemPlacement placement,ushort native,ushort maximum)=>
        placement with {Durability=NormalizeDurability(native,maximum),NativeDurability=maximum==FullDurability?null:native};
    internal static ItemPlacement RestoreDurability(ItemPlacement placement)=>
        placement with {Durability=FullDurability,NativeDurability=null};
    internal static ushort DurabilityAfterWear(ushort current,ushort maximum,int calibratedWear){
        if(current==0||calibratedWear==0)return current;
        int targetRatio=Math.Max(0,NormalizeDurability(current,maximum)-calibratedWear);
        // The captures give percentages, so use the existing wear calibration at the item's scale.
        return (ushort)Math.Min(current-1,targetRatio*maximum/FullDurability);
    }

    // Retail 1:169675–173156 loses 24 displayed points over 116 merchant hits and one other hit.
    internal const ushort BastardSwordMerchantWear=2;

    // 1:152409–152910 and 1:146899–152306 show 100% to 99% over four and five hits.
    internal static ushort FieldWeaponWear(EquipmentItem? weapon)=>(weapon?.Sprite,weapon?.Name) switch {
        (0x8003,"롱소드")=>3,
        (0xA7A7,"나무꾼도끼")=>2,
        _=>1
    };

    internal static byte DurabilityPercent(ItemPlacement placement){
        ushort maximum=MaximumDurability(Get(placement.Key));
        return (byte)((CurrentDurability(placement,maximum)*100+maximum-1)/maximum);
    }
    internal static byte DurabilityPercent(ushort durability)=>
        (byte)((durability*100+FullDurability-1)/FullDurability);

    static bool WornArmor(ItemPlacement item)=>item.InventorySlot==0&&Get(item.Key) is {EquipmentSlot:>1} gear&&
        CurrentDurability(item,MaximumDurability(gear))>0;
    static ItemPlacement WearArmor(ItemPlacement item,int wear){
        ushort maximum=MaximumDurability(Get(item.Key));
        ushort remaining=DurabilityAfterWear(CurrentDurability(item,maximum),maximum,wear);
        return WithCurrentDurability(item,remaining,maximum);
    }

    internal static Character WearArmorFromMonsterHit(Character character,ushort damage){
        if(character.Items is not {} items||!items.Any(WornArmor))return character;
        // Fit the observed 52 and 82 damage intervals. The retail raw wear is unknown.
        const int damagePerWear=5, maxDamagePerHit=20;
        int total=character.ArmorWearCarry+Math.Min((int)damage,maxDamagePerHit);
        int wear=total/damagePerWear;
        return character with {ArmorWearCarry=(byte)(total%damagePerWear),Items=wear==0?items:
            items.Select(item=>WornArmor(item)?WearArmor(item,wear):item).ToArray()};
    }

    internal static ItemPlacement[]? WearArmorOnDuelDefeat(ItemPlacement[]? items)=>items?.Select(item=>{
        if(!WornArmor(item))return item;
        ushort maximum=MaximumDurability(Get(item.Key));
        ushort durability=CurrentDurability(item,maximum);
        // One recorded defeat showed 100% becoming 99% while already-worn armor still showed 99%.
        int next=Math.Min(DurabilityAfterWear(durability,maximum,1),maximum*99/100);
        return WithCurrentDurability(item,(ushort)next,maximum);
    }).ToArray();

    // Slot and appearance-layer associations measured in the two Rolancia recordings.
    // Inventory and equipment packets use different field layouts and sprite IDs
    // are separate from the packed SPutHumanObject appearance words.
    static readonly Dictionary<string, EquipmentItem> Catalog = new(StringComparer.Ordinal)
    {
        [ShrineGrade.GemKey] = new(ShrineGrade.GemName,ShrineGrade.GemSprite,0,-1,0,0,0,0,0,0,255,InventoryFlag1:1),
        [ElBlessing.ItemKey] = new(ElBlessing.ItemName,ElBlessing.ItemSprite,0,-1,0,0,0,0,0,0,255),
        [ElBlessing.WrappedItemKey] = new(ElBlessing.WrappedItemName,ElBlessing.WrappedItemSprite,0,-1,0,0,0,0,0,0,255),
        ["kiah-dagger"] = new("키아의대거", 0x8000, 1, 0, 12289, 6, 1, 0, 0, 65, 100,
            StatDescription:"{=h공격력 {=f1-2{=h{=a"),
        ["scimitar"] = new("시미터", 0x82C0, 1, 0, 4100, 17, 3, 42, 45, 50, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-8003-b7d5bcd2b5e5"] = new("롱소드", 0x8003, 1, 0, 4097, 6, 3, 0, 0, 13, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-8007-b9d9bdbac5b8b5e5bcd2b5e5"] = new("바스타드소드", 0x8007, 1, 0, 4097, 11, 5, 0, 0, 66, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-800b-b1d7b7b9c0ccc6aebcd2b5e5"] = new("그레이트소드", 0x800B, 1, 0, 8193, 9, 6, 0, 0, 6, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-8000-b4ebb0c5"] = new("대거", 0x8000, 1, 0, 12289, 6, 1, 0, 0, 14, 100,
            StatDescription:"{=h공격력 {=f1-2{=h{=a"),
        ["recorded-8001-bed7bdba"] = new("액스", 0x8001, 1, 0, 28673, 9, 4, 0, 0, 9, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        [TorionQuest.AxeKey] = new("낡은액스",0x8001,1,0,28673,9,4,0,0,9,100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-8001-c5b0bec6c0c7bed7bdba"] = new("키아의액스", 0x8001, 1, 0, 28673, 9, 4, 0, 0, 68, 100,
            StatDescription:"{=h공격력 {=f2-3{=h{=a"),
        ["recorded-8005-bfecb5e7bab8bfec"] = new("우든보우", 0x8005, 1, 0, 45057, 6, 1, 0, 0, 10, 100,
            StatDescription:"{=h공격력 {=f1-5{=h{=a"),
        ["recorded-8005-c5b0bec6c0c7bfecb5e7bab8bfec"] = new("키아의우든보우", 0x8005, 1, 0, 45057, 6, 1, 0, 0, 68, 100,
            StatDescription:"{=h공격력 {=f1-5{=h{=a"),
        // The paired self S33 in the harp-to-long-sword swap supplies the harp layer.
        [CalasMusicLesson.HarpKey] = new("우든하프",0x8017,1,0,0xC001,6,1,0,0,56,100,
            StatDescription:"{=h공격력 {=f1-2{=h{=a"),
        ["recorded-8017-c5b0bec6c0c7bfecb5e7c7cfc7c1"] = new("키아의우든하프",0x8017,1,0,0xC001,4,1,0,0,155,100,
            StatDescription:"{=h공격력 {=f1-2{=h{=a"),
        // Native ItemPreview supplies the hand models, CharicInfo the weights and attack ranges.
        ["recorded-8009-b7d5bdbac7c7beee"] = new("롱스피어",0x8009,1,0,0x6001,0,6,0,0,56,100,
            StatDescription:"{=h공격력 {=f1-4{=h{=a"),
        ["recorded-8004-b9ddbff9b5b5"] = new("반월도",0x8004,1,0,0x6002,0,7,0,0,56,100,
            StatDescription:"{=h공격력 {=f1-4{=h{=a"),
        ["recorded-8002-c7dcb8d3"] = new("햄머",0x8002,1,0,0xA001,0,10,0,0,56,100,
            StatDescription:"{=h공격력 {=f1-4{=h{=a"),
        ["recorded-8016-b0edc5c2b5b5"] = new("고태도",0x8016,1,0,0x5001,0,7,0,0,56,100,
            StatDescription:"{=h공격력 {=f3-4{=h{=a"),
        [Fishing.RodKey] = new("낚싯대", 0x8006, 1, 0, Fishing.RodAppearance, 1, 1, 0, 0, 7, 100),
        [KondQuest.RodKey] = new("컨드의낚싯대",KondQuest.RodSprite,1,0,Fishing.RodAppearance,1,1,0,0,7,100),
        [Fishing.BaitKey] = new("지렁이병(대)", 0x8D44, 0, -1, 0, 0, 0, 0, 0, 6, 255),
        [Fishing.AnchovyKey] = new("멸치", 0x8D4D, 0, -1, 0, 0, 0, 0, 0, 6, 255, InventoryFlag1:1),
        [Fishing.MinnowKey] = new("금강모치", 0x8C1F, 0, -1, 0, 0, 0, 0, 0, 6, 255),
        [Fishing.TroutKey] = new("산천어", 0x8C20, 0, -1, 0, 0, 0, 0, 0, 6, 255),
        [Fishing.TropicalFishKey] = new("열대어", 0x8C1D, 0, -1, 0, 0, 0, 0, 0, 6, 255),
        [Fishing.SandLanceKey] = new("까나리", 0x8D4E, 0, -1, 0, 0, 0, 0, 0, 6, 255, InventoryFlag1:1),
        [Fishing.TunaKey] = new("참치", 0x8C21, 0, -1, 0, 0, 0, 0, 0, 6, 255),
        ["fairy-armor"] = new("페어리아머", 0xA7C1, 2, 2, 24596, 14, 3, 168, 51, 72, 100, 1,
            new(Hp: 1000, Str: 14, Int: 7, Vit: 14, Ac: 23, Mr: 20)),
        ["bone-shield"] = new("해골의실드", 0x8377, 3, 1, 2, 13, 10, 169, 20, 68, 100,
            Effect: new(Ac: 7, Mr: 7)),
        ["ice-shield"] = new("아이스실드", 0x8371, 3, 1, 1, 11, 10, 127, 54, 26, 100,
            Effect: new(Ac: 6, Mr: 5)),
        [SektiSmithing.ShieldKey] = new("비늘방패",SektiSmithing.ShieldSprite,3,1,4,0,0,0,0,0,100,
            Effect:new(Ac:6,Mr:6),StatDescription:"{=hAC {=f6{=h 증가{=a\n{=hMR {=f6{=h 증가{=a"),
        [SektiSmithing.ShellKey] = new("달팽이껍질",SektiSmithing.ShellSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1),
        [SektiSmithing.BoneKey] = new("황금해골의뼈",SektiSmithing.BoneSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1),
        [SektiSmithing.ClothKey] = new("천",SektiSmithing.ClothSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1),
        ["wool-shoes"] = new("울슈즈(하얀색)", 0x8234, 13, 5, 61442, 16, 0, 220, 77, 72, 100,
            Effect: new(Hp: 2, Str: 25, Agi: 30, Ac: 21, Mr: 2)),
        // Appearance model IDs also match ItemPreview entries 5083, 7003, 8002.
        // For pieces seen only in the equipment list, unset inventory counters are local defaults.
        ["swim-cap"] = new("수영모(군청색)", 0x82F6, 4, 6, 41961, 14, 1, 0, 0, 0, 100,
            Effect: new(Str: 18, Agi: 30, Ac: 6)),
        ["iron-necklace"] = new("아이언네클리스", 0x801D, 6, -1, 0, 14, 0, 0, 0, 0, 100,
            Effect: new(Mp: 1, Str: 80, Int: 6, Vit: 5, Agi: 60, Ac: 5, Mr: 18)),
        ["iron-necklace-basic"] = new("아이언네클리스", 0x801D, 6, -1, 0, 0, 0, 0, 0, 0, 100),
        ["madeia-ring-left"] = new("마데이아의링", 0x8118, 7, -1, 0, 15, 1, 179, 32, 58, 100,
            Effect: new(Str: 40, Vit: 3, Agi: 50, Ac: 20, Mr: 10)),
        ["madeia-ring-right"] = new("마데이아의링", 0x8118, 8, -1, 0, 16, 1, 70, 83, 69, 100,
            Effect: new(Str: 40, Vit: 3, Agi: 50, Ac: 20, Mr: 10), InventoryKind: 7),
        ["sky-shorts"] = new("반바지(하늘색)", 0x806D, 15, 4, 4097, 16, 0, 0, 0, 0, 100,
            Effect: new(Str: 45, Vit: 2, Agi: 60, Ac: 6, Mr: 13)),
        ["ochre-garnish"] = new("가니쉬(황토색)", 0x807E, 16, 3, 12290, 16, 0, 0, 0, 0, 100,
            Effect: new(Mp: 2, Str: 32, Int: 3, Vit: 2, Agi: 58, Ac: 7, Mr: 4)),
        ["oran-feather"] = new("오란의깃", 0x8BC7, 0, -1, 0, 0, 0, 246, 45, 1, 255, InventoryFlag1: 1),
        ["ras-feather"] = new("라스의깃", 0x8F24, 0, -1, 0, 0, 0, 47, 78, 55, 255, InventoryFlag1: 1),
        ["egg"] = new("달걀", 0x8BD7, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1, RestoreSatiety:1),
        ["boiled-egg"] = new("삶은달걀", 0x8BD7, 0, -1, 0, 0, 0, 0, 0, 56, 255, RestoreHp:10, RestoreSatiety:1),
        [CookingRecipe.OmeletKey] = new("오믈렛",CookingRecipe.OmeletSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1,RestoreSatiety:1),
        [CookingRecipe.PieKey] = new("초컬릿 파이",CookingRecipe.PieSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1,RestoreSatiety:1),
        [CookingRecipe.CakeKey] = new("딸기 케익",CookingRecipe.CakeSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1,RestoreSatiety:1),
        [RanicheQuest.WaterKey] = new("깨끗한물",0x8CDD,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1),
        [RanicheQuest.RingKey] = new("라니체의반지",0x8115,7,-1,0,0,0,0,0,0,100,Effect:new(Ac:2,Mr:2),
            StatDescription:"{=hAC {=f2{=h 증가|MAC {=f2{=h 증가{=a"),
        [RanicheQuest.StaffKey] = new("오블로의지팡이",0x8778,0,-1,0,0,0,0,0,56,255),
        [RanicheQuest.BrelKey] = new("사라트(브렐)",0x82A8,0,-1,0,0,0,0,0,56,255,
            StatDescription:"브렐을 배우는 데 쓰는 마법석입니다."),
        [GuardianQuest.Badge] = new("수호천사의증표",0x8033,0,-1,0,0,0,0,0,56,255),
        // ItemPreview 10024 uses both RA02003 and LA02003. CharicInfo11 lists zero weight and 1–2 attack.
        [GuardianQuest.Pencil] = new("커다란연필",0x84CA,1,0,0x2003,0,0,0,0,56,100,
            StatDescription:"{=h공격력 {=f1-2{=h{=a"),
        [CalendarQuest.Tree.RingKey] = EventRing(CalendarQuest.Tree),
        [CalendarQuest.Elder.RingKey] = EventRing(CalendarQuest.Elder),
        [CalendarQuest.Manners.RingKey] = EventRing(CalendarQuest.Manners),
        ["healing-potion-1ml"] = new("힐링포션(1ml)", 0x8BC1, 0, -1, 0, 0, 0, 0, 0, 56, 255, RestoreHp: 40),
        ["recorded-8c78-bdc4bba7"] = new("식빵", 0x8C78, 0, -1, 0, 0, 0, 0, 0, 66, 255, InventoryFlag1:1, RestoreSatiety:1),
        ["chicken-meat"] = new("닭고기", 0x8C30, 0, -1, 0, 0, 0, 0, 0, 56, 255, RestoreSatiety:1),
        [MutaiButcher.ProductKey] = new(MutaiButcher.ProductName, MutaiButcher.ProductSprite,
            0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [KayGatheringQuest.MaterialKey] = new(KayGatheringQuest.MaterialName, KayGatheringQuest.MaterialSprite,
            0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [GatheringQuest.RatTailKey] = new("큰쥐꼬리", 0x8514, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [GatheringQuest.RatEyeKey] = new("회색쥐눈", 0x8520, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [GatheringQuest.ScaleKey] = new("비늘", 0x888C, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [GatheringQuest.ZombieBoneKey] = new("좀비의뼈", 0x8888, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [NeruaDialog.CandyKey] = new(NeruaDialog.CandyName, NeruaDialog.CandySprite, 0, -1, 0, 0, 0, 0, 0, 0, 255),
        [TeddyTailoring.WingKey] = new("유익인의날개", 0x84BC, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [ErenoaQuest.AratKey] = new("아라트(다라)",ErenoaQuest.AratSprite,0,-1,0,0,0,0,0,56,255,
            StatDescription:"다라를 배우는 데 쓰는 1단계 마법석입니다."),
        [BrisSpell.AratKey] = new("아라트(브리스)",ErenoaQuest.AratSprite,0,-1,0,0,0,0,0,56,255,
            StatDescription:"브리스를 배우는 데 쓰는 1단계 마법석입니다."),
        [MetiniQuest.AratKey] = new("아라트(리메듐)",MetiniQuest.AratSprite,0,-1,0,0,0,0,0,56,255,
            StatDescription:"리메듐을 배우는 데 쓰는 1단계 마법석입니다."),
        [TorionQuest.BraemaKey] = new("사라트(브레마)",0x82AA,0,-1,0,0,0,0,0,56,255,
            StatDescription:"브레마를 배우는 데 쓰는 마법석입니다."),
        ["mishat-slow"] = SpellStone("미샤트(감속)"),
        ["lumit-slow"] = SpellStone("루미트(감속)"),
        ["mishat-curse"] = SpellStone("미샤트(저주)"),
        ["lumit-curse"] = SpellStone("루미트(저주)"),
        [TorionQuest.LenaRingKey] = new("레나의링",0x802B,7,-1,0,0,0,0,0,0,100,
            StatDescription:"엘 속성 마법의 정령석을 대신합니다."),
        [TeddyTailoring.OldShoeKey] = new("낡은신발", 0x804E, 0, -1, 0, 0, 0, 0, 0, 56, 255, InventoryFlag1:1),
        [TeddyTailoring.PantsKey] = new("테디의바지", 0x8099, 15, 4, 0xF002, 16, 0, 0, 0, 0, 100,
            Effect:new(Ac:2,Mr:2)),
        [TeddyTailoring.ShoesKey] = new("테디의슈즈", 0x8055, 13, 5, 0xB001, 16, 0, 0, 0, 0, 100,
            Effect:new(Agi:1,Ac:1,Mr:1)),
        ["iron-ring"] = RubyRing("아이언링",0x801A,1),
        ["silver-ring"] = RubyRing("실버링",0x801B,1),
        ["gold-ring"] = RubyRing("골드링",0x801C,1),
        [RubyJewelry.OldIron] = new("낡은아이언링",0x801A,0,-1,0,0,0,0,0,0,255),
        [RubyJewelry.OldSilver] = new("낡은실버링",0x801B,0,-1,0,0,0,0,0,0,255),
        [RubyJewelry.OldGold] = new("낡은골드링",0x801C,0,-1,0,0,0,0,0,0,255),
        [RubyJewelry.Bridium] = SpiritStone("브리디움",0x8C4F),
        [RubyJewelry.Danium] = SpiritStone("다니움",0x8C50),
        [RubyJewelry.Mahadium] = SpiritStone("마하디움",0x8C51),
        [RubyJewelry.Brakidium] = SpiritStone("브라키디움",0x8C52),
        [RubyJewelry.Elsaria] = SpiritStone("엘사리아",0x8C53),
        [RubyJewelry.Tessarat] = SpiritStone("테사랏트",0x8C54),
        ["bridium-ring"] = RubyRing("브리디움링",0x8021,1),
        ["danium-ring"] = RubyRing("다니움링",0x8022,1),
        ["mahadium-ring"] = RubyRing("마하디움링",0x8023,1),
        ["brakidium-ring"] = RubyRing("브라키디움링",0x8024,2),
        ["elsaria-ring"] = RubyRing("엘사리아링",0x8025,2),
        ["tessarat-ring"] = RubyRing("테사랏트링",0x8026,2),
        [OldSmithQuest.RingKey] = RubyRing("스미스의링",OldSmithQuest.RingSprite,1),
        [OldSmithQuest.WaterKey] = new("성스러운물",OldSmithQuest.WaterSprite,0,-1,0,0,0,0,0,56,255,InventoryFlag1:1),
        ["silver-necklace"] = new("실버네클리스", 0x801E, 6, -1, 0, 0, 0, 0, 0, 0, 100),
        [MercenaryHiring.LeonDeed] = new("고용서(레온)",0x8D15,0,-1,0,0,0,0,0,0,255),
        [MercenaryHiring.SummonScroll] = new("소환서(용병)",MercenaryHiring.SummonScrollSprite,0,-1,0,0,0,0,0,0,255),
        ["gold-necklace"] = new("골드네클리스", 0x801F, 6, -1, 0, 0, 0, 0, 0, 0, 100,
            Effect:new(ManaRecovery:2),StatDescription:"{=h마력회복량 {=f2+0{=h 증가{=a"),
        ["ruby-necklace"] = new("루비네클리스", 0x8020, 6, -1, 0, 0, 0, 0, 0, 0, 100),
    };
    static EquipmentItem EventRing(CalendarQuest quest)=>new(quest.RingName,quest.Sprite,7,-1,0,0,0,0,0,0,100);
    static EquipmentItem RubyRing(string name,ushort sprite,int intelligence)=>new(name,sprite,7,-1,0,0,0,0,0,0,100,
        Effect:new(Int:intelligence),StatDescription:$"{{=hINT {{=f{intelligence}{{=h 증가{{=a");
    static EquipmentItem SpiritStone(string name,ushort sprite)=>new(name,sprite,0,-1,0,0,0,0,0,0,255,InventoryFlag1:1);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,EquipmentItem> LiveCatalog=new(StringComparer.Ordinal);
    static readonly HashSet<(ushort Sprite,string Name)> Food=[
        (0x8BD7,"달걀"),(0x8C30,"닭고기"),(0x8BE1,"돼지고기(조각)"),(0x8C1C,"돼지고기"),(0x8C03,"쇠고기(조각)"),
        (0x8BC8,"사과"),(0x8BC9,"바나나"),(0x8BCA,"복숭아"),(0x8BCB,"레몬"),(0x8BCC,"밤"),(0x8BCD,"감"),
        (0x8BCE,"수박"),(0x8BCF,"메론"),(0x8BD0,"배"),(0x8BD1,"체리"),(0x8BD2,"포도"),(0x8BD3,"토마토"),
        (0x8BDC,"땅콩"),(0x8CA1,"딸기"),(0x8C6E,"치즈"),(0x8C75,"햄"),(0x8CB0,"프랑크 소시지"),
        (0x8C7A,"햄버그 햄"),(0x8CB1,"딸기 쨈"),(0x8C78,"식빵"),(0x8BD7,"삶은달걀")
    ];
    static readonly Regex EffectLine=new(@"\{=h(최대체력|최대마력|체력회복량|마력회복량|HP|MP|STR|INT|VIT|AGI|AC|MR|MAC)\s*\{=f([+-]?[0-9]{1,5})([+-][0-9]{1,5})?\{=h\s*(증가|감소)\s*\{=a",RegexOptions.CultureInvariant);
    static readonly Regex AttackLine=new(@"\{=h공격력\s*\{=f([0-9]{1,5})(?:\s*[-~]\s*([0-9]{1,5}))?\{=h",RegexOptions.CultureInvariant);

    internal static int RollWeaponAttack(EquipmentItem? weapon){
        if(weapon==null)return 0;
        var match=AttackLine.Match(weapon.StatDescription??"");
        int minimum,maximum;
        if(match.Success){
            minimum=int.Parse(match.Groups[1].Value);
            maximum=match.Groups[2].Success?int.Parse(match.Groups[2].Value):minimum;
        }else if(NativeItems.TryGetValue(weapon.Name,out var defaults)){
            minimum=defaults.AttackMinimum;maximum=defaults.AttackMaximum;
        }else return 0;
        return Random.Shared.Next(minimum,Math.Max(minimum,maximum)+1);
    }

    static EquipmentItems(){
        for(int ml=2;ml<=7;ml++)RegisterPotion(false,ml);
        for(int ml=1;ml<=6;ml++)RegisterPotion(true,ml);
        foreach(var item in RecordedWorld.Items)Catalog.TryAdd(item.Key,FromRecorded(item));
    }
    static string PotionName(bool mana,int ml)=>$"{(mana?"마나":"힐링")}포션({ml}ml)";
    static ushort PotionSprite(bool mana,int ml)=>!mana&&ml==7?(ushort)0x8FDC:(ushort)((mana?0x8D22:0x8BC0)+ml);
    internal static string PotionKey(bool mana,int ml)=>!mana&&ml==1?"healing-potion-1ml":
        $"recorded-{PotionSprite(mana,ml):x4}-{Convert.ToHexString(Encoding.GetEncoding(949).GetBytes(PotionName(mana,ml))).ToLowerInvariant()}";
    static void RegisterPotion(bool mana,int ml){
        // Preserve the catalog identity from the recorded sprite and name.
        uint restore=(uint)ml*40;
        ushort inventoryWord=(mana,ml) switch {
            (false,6)=>0x40,
            (false,3) or (true,2)=>0xAA,
            _=>56
        };
        Catalog.Add(PotionKey(mana,ml),new(PotionName(mana,ml),PotionSprite(mana,ml),0,-1,0,0,0,0,0,inventoryWord,255,
            RestoreHp:mana?0:restore,RestoreMp:mana?restore:0));
    }
    static EquipmentItem FromRecorded(RecordedItem item)=>WithRecordedStats(new(item.Name,item.Sprite,item.EquipmentSlot,
        item.AppearanceLayer,item.LocalAppearanceWord??item.AppearanceWord,item.EquipmentWord1??0,item.EquipmentWord2??item.InventoryWord4,0,0,item.InventoryWord3,item.EquipmentSlot==0?byte.MaxValue:(byte)100,
        Flag:item.Flag??0,
        InventoryFlag1:(item.Sprite,item.Name) is (0x8BC9,"바나나") or (0x8BE1,"돼지고기(조각)")?(byte)1:item.InventoryFlag1,
        RestoreSatiety:Food.Contains((item.Sprite,item.Name))?1u:0u,Timer:item.Timer,LocalAppearanceWord:item.LocalAppearanceWord),
        item.StatDescription??BasicClothingDescription(item));

    // The basic color sets share their AC and MR. Individually recorded options take precedence.
    static string? BasicClothingDescription(RecordedItem item){
        int defense=(item.EquipmentSlot,item.Sprite) switch {
            (16,>=0x805E and <=0x806C) when item.Name.StartsWith("튜닉(",StringComparison.Ordinal)=>1,
            (15,>=0x806D and <=0x807B) when item.Name.StartsWith("반바지(",StringComparison.Ordinal)=>1,
            (16,>=0x807C and <=0x808A) when item.Name.StartsWith("가니쉬(",StringComparison.Ordinal)=>2,
            (15,>=0x808B and <=0x8099) when item.Name.StartsWith("바지(",StringComparison.Ordinal)=>2,
            _=>0
        };
        return defense==0?null:$"{{=hAC {{=f{defense}+0{{=h 증가{{=a|{{=hMR {{=f{defense}+0{{=h 증가{{=a";
    }

    static EquipmentItem WithRecordedStats(EquipmentItem item,string? description){
        if(description==null)return item;
        var effect=item.Effect;
        foreach(Match row in EffectLine.Matches(description)){
            int value=int.Parse(row.Groups[2].Value)+(row.Groups[3].Success?int.Parse(row.Groups[3].Value):0);
            if(row.Groups[4].Value=="감소")value=-Math.Abs(value);
            effect??=new EquipmentEffect();
            effect=row.Groups[1].Value switch {
                "최대체력" or "HP"=>effect with {Hp=value},
                "최대마력" or "MP"=>effect with {Mp=value},
                "STR"=>effect with {Str=value},"INT"=>effect with {Int=value},
                "VIT"=>effect with {Vit=value},"AGI"=>effect with {Agi=value},
                "AC"=>effect with {Ac=value},"MR" or "MAC"=>effect with {Mr=value},
                "체력회복량"=>effect with {HealthRecovery=value},
                "마력회복량"=>effect with {ManaRecovery=value},
                _=>effect
            };
        }
        return item with {StatDescription=description,Effect=effect};
    }
    internal static bool HasKey(string key)=>Catalog.ContainsKey(key)||LiveCatalog.ContainsKey(key);
    internal static Dictionary<string,EquipmentItem> ManagedCatalog()=>Catalog.Keys.Concat(LiveCatalog.Keys).Distinct()
        .ToDictionary(key=>key,Get);
    internal static void RegisterLive(IEnumerable<RecordedItem> items){
        var variants=new List<RecordedItem>();
        foreach(var item in items){
            if(item.VariantOf!=null){variants.Add(item);continue;}
            if(Catalog.TryGetValue(item.Key,out var existing)){
                if(existing.Name!=item.Name||existing.Sprite!=item.Sprite)continue;
                bool recordedWeapon=item.EquipmentSlot==1&&item.AppearanceLayer==0&&item.AppearanceWord!=0;
                int expectedLayer=item.EquipmentSlot switch {2=>2,3=>1,4=>6,13=>5,15=>4,16=>3,_=>-1};
                bool recordedArmor=item.AppearancePacket!=null&&expectedLayer>=0&&item.AppearanceLayer==expectedLayer&&item.AppearanceWord!=0;
                bool recordedAppearance=recordedWeapon||recordedArmor;
                if(item.StatDescription!=null||item.Timer!=null||recordedAppearance||item.Flag.HasValue)
                    LiveCatalog[item.Key]=WithRecordedStats(existing with {
                        Timer=item.Timer??existing.Timer,
                        EquipmentSlot=recordedAppearance?item.EquipmentSlot:existing.EquipmentSlot,
                        AppearanceLayer=recordedAppearance?item.AppearanceLayer:existing.AppearanceLayer,
                        AppearanceWord=existing.LocalAppearanceWord??(recordedAppearance?item.AppearanceWord:existing.AppearanceWord),
                        EquipmentWord1=item.EquipmentWord1??existing.EquipmentWord1,
                        EquipmentWord2=item.EquipmentWord2??existing.EquipmentWord2,
                        Flag=item.Flag??existing.Flag
                    },item.StatDescription);
            }else{
                LiveCatalog[item.Key]=FromRecorded(item);
            }
        }
        foreach(var variant in variants){
            string parent=variant.VariantOf!;
            if(variant.StatDescription is not {} description||variant.Key==parent||Catalog.ContainsKey(variant.Key))continue;
            if(!LiveCatalog.TryGetValue(parent,out var basis)&&!Catalog.TryGetValue(parent,out basis))continue;
            if(basis.Name!=variant.Name||basis.Sprite!=variant.Sprite||basis.EquipmentSlot==0||basis.EquipmentSlot!=variant.EquipmentSlot)continue;
            LiveCatalog[variant.Key]=WithRecordedStats(basis with {Effect=null},description);
        }
    }
    public static EquipmentItem Get(string key) => LiveCatalog.TryGetValue(key,out var item)||Catalog.TryGetValue(key,out item)
        ? item : throw new InvalidDataException($"Unknown local item: {key}");

    internal static readonly EquipmentEffect DryadSetBonus=new(Str:3,Int:12,Vit:4,Agi:8,Ac:5,Mr:7);

    public static IEnumerable<EquipmentEffect> EquippedEffects(Character? character){
        int dryadParts=0;
        foreach(var placement in character?.Items??[]){
            if(placement.InventorySlot!=0)continue;
            var item=Get(placement.Key);
            if(item.Effect is {} effect)yield return effect;
            dryadParts|=(item.Sprite,item.EquipmentSlot,item.Name) switch {
                (0xA769,4,"드라이어드모자(1/6)")=>1,
                (0x805B,6,"드라이어드목걸이(2/6)")=>2,
                (0x8029,7,"드라이어드반지(3/6)")=>4,
                (0x802A,8,"드라이어드반지(4/6)")=>8,
                (0x83CB,16,"드라이어드상의(5/6)")=>16,
                (0x8225,15,"드라이어드하의(6/6)")=>32,
                _=>0
            };
        }
        if(dryadParts==0x3F)yield return DryadSetBonus;
    }
    internal static bool HasDryadSet(Character character)=>
        EquippedEffects(character).Any(effect=>ReferenceEquals(effect,DryadSetBonus));
    internal static int EquippedWeight(Character character,byte excludedSlot=0)=>
        (character.Items??[]).Where(p=>p.InventorySlot==0).Select(p=>Get(p.Key))
            .Where(item=>item.EquipmentSlot!=excludedSlot&&item.EquipmentSlot!=0).Sum(item=>(int)item.EquipmentWord2);
    internal static ushort CarriedWeight(Character character)=>WeightWord(CarriedWeightTotal(character.Items??[]));
    internal static long CarriedWeightTotal(IEnumerable<ItemPlacement> placements)=>
        placements.Where(p=>p.InventorySlot!=0).Sum(InventoryWeight);
    internal static ushort WeightWord(long weight)=>(ushort)Math.Clamp(weight,0,ushort.MaxValue);
    internal static long InventoryWeight(ItemPlacement placement)=>(long)Get(placement.Key).EquipmentWord2*placement.Quantity;
    static EquipmentItem SpellStone(string name)=>new(name,0x82B4,0,-1,0,0,0,0,0,56,255);

    internal static ushort Agility(Character? character,IEnumerable<EquipmentEffect> effects){
        int agility=(character?.JobStats?.Agi??WorldSettings.BaseAttribute)+effects.Sum(e=>e.Agi);
        if(PlayerDebuff.SlowActive(character))agility=(agility+1)/2;
        return (ushort)Math.Clamp(agility-PlayerDebuff.CursePenalty(character),0,ushort.MaxValue);
    }
    internal static ushort PhysicalDefense(IEnumerable<EquipmentEffect> effects)=>(ushort)Math.Clamp(effects.Sum(e=>e.Ac),0,ushort.MaxValue);
    internal static ushort MagicDefense(IEnumerable<EquipmentEffect> effects)=>(ushort)Math.Clamp(effects.Sum(e=>e.Mr),0,ushort.MaxValue);
    internal static uint NaturalHealthRecovery(Character character)=>
        (uint)Math.Max(0,3+EquippedEffects(character).Sum(effect=>effect.HealthRecovery));
    internal static uint NaturalManaRecovery(Character character)=>
        (uint)Math.Max(1,EquippedEffects(character).Sum(effect=>effect.ManaRecovery));
    internal static bool IsRecoveryPotion(EquipmentItem item)=>
        item.Sprite is (>=0x8BC1 and <=0x8BC6) or 0x8FDC && item.RestoreHp>0 ||
        item.Sprite is >=0x8D23 and <=0x8D28 && item.RestoreMp>0;
    internal static uint PotionCrossCost(EquipmentItem item){
        int ml=item.Sprite switch {
            >=0x8BC1 and <=0x8BC6=>item.Sprite-0x8BC0,
            >=0x8D23 and <=0x8D28=>item.Sprite-0x8D22,
            0x8FDC=>7,
            _=>0
        };
        // Use measured costs where available and extrapolate the other sizes.
        return item.Sprite switch {0x8BC3=>2,0x8BC6=>3,0x8D24=>1,_=>(uint)(ml+1)/2};
    }

    public static uint MaxHp(IEnumerable<EquipmentEffect> effects,int innate=100) => (uint)Math.Max(1,innate+effects.Sum(e=>e.Hp));
    public static uint MaxMp(IEnumerable<EquipmentEffect> effects,int innate=100) => (uint)Math.Max(0,innate+effects.Sum(e=>e.Mp));

    public static Character ApplyAppearance(Character character)
    {
        if (character.Items is not { Length: > 0 } placements) return character;

        var appearance = character.Equipment?.ToArray() ?? new ushort[10];
        foreach (var placement in placements)
        {
            var item = Get(placement.Key);
            if (item.AppearanceLayer >= 0) appearance[item.AppearanceLayer] = 0;
        }
        foreach (var placement in placements)
        {
            var item = Get(placement.Key);
            if (placement.InventorySlot == 0 && item.AppearanceLayer >= 0)
                appearance[item.AppearanceLayer] = item.AppearanceWord;
        }
        return character with { Equipment = appearance };
    }
}
