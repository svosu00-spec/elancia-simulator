using System.Text.Json;

namespace ElanciaLocal;

internal sealed record RecordedMap(int Id,string Name,ushort? Track,byte Flags);
internal sealed record RecordedPortal(int Map,int X,int Y,byte Floor,StartLocation Destination);
internal sealed record RecordedDoorArrival(int Source,int Destination,bool MapPadding,ushort? Track,string MapPacket,bool OmitEmptyObjects=false,string? BuffStatusPacket=null,RecordedDoorOptionStatus[]? OptionStatuses=null);
internal sealed record RecordedDoorOptionStatus(string Packet,byte Mask,uint Flags,byte Direction);
internal sealed record DoorArrivalProfile(bool? MapPadding,ushort? Track,bool OmitEmptyObjects,bool RefreshBuffStatus,
    RecordedDoorOptionStatus[] OptionStatuses);
internal sealed record RecordedItem(string Key,string Name,ushort Sprite,byte EquipmentSlot=0,int AppearanceLayer=-1,ushort AppearanceWord=0,string? StatDescription=null,ushort InventoryWord3=56,ushort InventoryWord4=0,byte InventoryFlag1=0,TimedSpellData? Timer=null,
    ushort? EquipmentWord1=null,ushort? EquipmentWord2=null,string? AppearancePacket=null,ushort? LocalAppearanceWord=null,byte? Flag=null,
    string? VariantOf=null);
internal sealed record RecordedMonster(string Name,ushort Sprite,int Map,int X,int Y,byte Floor,byte Flags,byte Direction);
internal sealed record RecordedTrainingTarget(int Map,int X,int Y,byte Floor,byte Direction);
internal sealed record RecordedLootDrop(string Key,uint Quantity);
internal sealed record RecordedDropPool(string Name,ushort Sprite,string[] Items);
internal sealed record RecordedLootRule(int Map,string Name,ushort Sprite,RecordedLootDrop[] Drops,string[] Samples,
    string[]? ConfirmedSamples=null);
internal sealed record RecordedMeleeRule(string Name,ushort Sprite,ushort Armor,ushort Damage,byte Variant,ushort Motion,byte StatusMask,string[] Samples,
    bool? MotionPadded=null,bool? StatusPadded=null);
internal sealed record RecordedSpellDamage(string Name,ushort Sprite,ushort MagicDefense,ushort Damage,string[] Samples);
internal sealed record RecordedKillExperience(string Name,ushort Sprite,ushort Experience,string[] Samples);
internal readonly record struct MonsterMelee(ushort Damage,byte Variant=0,ushort Motion=4,byte StatusMask=0x20,
    bool MotionPadded=true,bool StatusPadded=true);
internal sealed record RecordedMonsterHealth(int Hp,string Source,int MinLevel=0,int MaxLevel=0,ushort? KillExperience=null,ushort Defense=0);
internal sealed record LiveWorldData(RecordedMap[] Maps,RecordedPortal[] Portals,RecordedItem[] Items,LocalNpc[] Npcs,RecordedMonster[]? Monsters=null,RecordedClock? Clock=null,
    RecordedLootRule[]? MonsterLoot=null,RecordedMeleeRule[]? MonsterMelee=null,RecordedKillExperience[]? MonsterKillExperience=null,
    RecordedSpellDamage[]? MonsterSpellDamage=null,RecordedDoorArrival[]? DoorArrivals=null,
    RecordedTrainingTarget[]? TrainingTargets=null);

internal sealed record RecordedClock(DateTimeOffset ObservedAt,byte Month,byte Day,byte Hour,byte Minute,DateTimeOffset? HourlyPulseAt=null){
    // 1:222411–222447. Official ticks advance 30 game minutes every five real minutes.
    internal static readonly RecordedClock Reference=new(new(2026,11,8,12,20,52,500,TimeSpan.Zero),2,23,17,3);
    const int SecondsPerMinute=10;
    const int PulseMinutes=5;
    int CalendarYear=>Month==2&&Day==29?2000:2001;
    internal bool Valid=>Month is >=1 and <=12&&Day>=1&&Day<=DateTime.DaysInMonth(CalendarYear,Month)&&Hour<24&&Minute<60;
    DateTimeOffset PulseAnchor=>HourlyPulseAt??Reference.ObservedAt;
    internal long Pulse(DateTimeOffset now)=>(long)Math.Floor((now-PulseAnchor).TotalMinutes/PulseMinutes);
    internal DateTimeOffset PulseTime(long pulse)=>PulseAnchor.AddMinutes(pulse*PulseMinutes);
    internal bool IsHourlyPulse(long pulse)=>HourlyPulseAt.HasValue?pulse%(60/PulseMinutes)==0:PulseTime(pulse).Minute==0;
    internal (byte Month,byte Day,byte Hour,byte Minute) Read(DateTimeOffset now){
        // Login packets can arrive partway into a game minute. Align them to the observed minute boundary.
        double anchorMinute=Math.Floor((ObservedAt-PulseAnchor).TotalSeconds/SecondsPerMinute);
        double nowMinute=Math.Floor((now-PulseAnchor).TotalSeconds/SecondsPerMinute);
        var gameTime=new DateTime(CalendarYear,Month,Day,Hour,Minute,0).AddMinutes(nowMinute-anchorMinute);
        return ((byte)gameTime.Month,(byte)gameTime.Day,(byte)gameTime.Hour,(byte)gameTime.Minute);
    }
}

internal static class RecordedWorld
{
    sealed record WorldDataFile(RecordedMap[] Maps,RecordedPortal[] Portals,RecordedItem[] Items,RecordedMonster[] Monsters,
        Dictionary<string,RecordedMonsterHealth> MonsterHealth,RecordedDropPool[]? RolanciaDropPools=null,
        RecordedTrainingTarget[]? TrainingTargets=null);

    static WorldDataFile Load(){
        using var stream=typeof(RecordedWorld).Assembly.GetManifestResourceStream("ElanciaLocalServer.world-recorded.json")
            ??throw new InvalidDataException("Recorded world data is missing");
        return JsonSerializer.Deserialize<WorldDataFile>(stream)
            ??throw new InvalidDataException("Recorded world data is invalid");
    }

    static readonly WorldDataFile Data=Load();
    public static readonly IReadOnlyDictionary<int,RecordedMap> Maps=Data.Maps.ToDictionary(m=>m.Id);
    public static readonly IReadOnlyDictionary<(int,int,int,byte),StartLocation> Portals=
        Data.Portals.ToDictionary(p=>(p.Map,p.X,p.Y,p.Floor),p=>p.Destination);
    public static IEnumerable<RecordedItem> Items=>Data.Items;
    public static IEnumerable<RecordedMonster> Monsters=>Data.Monsters;
    internal static IEnumerable<RecordedTrainingTarget> TrainingTargets=>Data.TrainingTargets??[];
    internal static readonly IReadOnlyDictionary<(string Name,ushort Sprite),string[]> RolanciaDropPools=
        (Data.RolanciaDropPools??[]).ToDictionary(r=>(r.Name,r.Sprite),r=>r.Items);
    public static int MonsterHp(string name,int fallback)=>Data.MonsterHealth.TryGetValue(name,out var profile)?profile.Hp:fallback;
    internal static ushort? MonsterExperience(string name)=>Data.MonsterHealth.GetValueOrDefault(name)?.KillExperience;
    internal static ushort MonsterDefense(string name)=>Data.MonsterHealth.GetValueOrDefault(name)?.Defense??0;
    public static int MonsterLevel(string name)=>Data.MonsterHealth.TryGetValue(name,out var profile)?(profile.MinLevel+profile.MaxLevel)/2:0;
    internal static int RollMonsterLevel(string name,int fallback)=>
        Data.MonsterHealth.TryGetValue(name,out var profile)&&profile.MinLevel>0&&profile.MaxLevel>=profile.MinLevel
            ?Random.Shared.Next(profile.MinLevel,profile.MaxLevel+1):fallback;
}
