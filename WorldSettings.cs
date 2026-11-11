using System.Text.Json;
namespace ElanciaLocal;

public static class WorldSettings {
    internal const int FieldUpdateMs=250;
    public const int BaseAttribute=5;
    public const uint MaxSatiety=20;
    internal const int InventorySlots=50;
    internal const byte DefaultWalletSlot=32;
    internal const string CurrencyKey="galrid";
    internal const uint MaxCarriedGold=10_000_000;
    internal const string GoldLimitMessage="갈리드는 최대 10,000,000까지 소지할 수 있습니다.";
    internal const string UnreleasedRegionMessage="미개발지구입니다";
    internal const int PersonalRoomMap=9801;
    // Each owner gets a separate server map, backed by the same client map file.
    internal static int PersonalRoomInstance(uint owner)=>checked(-1-(int)owner);
    internal static int ClientMap(int map)=>map<0?PersonalRoomMap:map;
    internal static int KoreanDay(DateTimeOffset now)=>(int)(now.ToOffset(TimeSpan.FromHours(9)).Date.Ticks/TimeSpan.TicksPerDay);
    internal static bool CanCreditGold(uint balance,ulong amount)=>amount==0||
        balance<=MaxCarriedGold&&amount<=MaxCarriedGold-balance;
    internal static bool IsUnreleasedRegion(int map)=>map is >=0 and <30000&&map%10000 is >=1000 and <5000;
    internal const ushort RevivalStunSeconds=11;
    internal const int RevivalPulseMs=1100;
    internal const int RevivalUnconsciousMs=12_100;
    internal const int RevivalDelayMs=RevivalUnconsciousMs+RevivalPulseMs;
    internal const uint RolanciaCitizenshipCost=500;
    internal const uint GuildFoundingCost=10000;
    internal const uint GuildDepartureCost=100000;

    internal static bool IsArcherTrainingRoom(int map)=>map is >=0 and <30000&&map%10000 is >=297 and <=299;
    internal static bool AllowsDuel(int map)=>IsArcherTrainingRoom(map)||
        map is >=0 and <30000&&(map%10000==202||IsGuildHouse(map));

    internal static bool IsGuildHouse(int map)=>map is >=0 and <30000&&
        (map%10000 is >=221 and <=228 or 1221 or
            >=2221 and <=2228 or >=3221 and <=3228 or >=4221 and <=4228);

    internal static bool IsGuildHouseDoor(int source,int destination)=>
        IsGuildHouse(source)&&destination==source/1000*1000+2||
        IsGuildHouse(destination)&&source==destination/1000*1000+2;

    internal static int HomeTown(int map)=>map is >=0 and <30000&&(map%10000 is 2 or 1002 or 2002 or 3002 or 4002)?map:2;
    internal static StartLocation RevivalShrine(int home)=>(home%10000) switch {
        2=>new(home+227,39,29,0),
        2002=>new(home+227,17,15,1),
        _=>new(home+227,0,0,0)
    };
    internal static string? ShrineName(int map)=>(map%10000) switch {
        229=>"Rolancia-Shrine",1229=>"Epiloria-Shrine",2229=>"Sernnica-Shrine",
        3229=>"Cronosis-Shrine",4229=>"Popresne-Shrine",_=>null
    };

    internal static StartLocation OranDestination(int? citizenTown,int originMap){
        int city=HomeTown(citizenTown??1002)%10000;
        int domain=originMap is >=0 and <30000?originMap/10000*10000:0;
        if(city==1002)return new(domain+city,62,69,0);
        // Use the known town arrival tile until a direct Oran capture supplies another one.
        return RasDestination((uint)(domain+city),originMap)!;
    }

    static readonly (int X,int Y)[] AlphaRasArrivals=[(159,88),(159,236),(223,125),(127,155)];
    static readonly (int X,int Y)[] BetaRasArrivals=[(159,236),(223,125),(127,155)];

    internal static bool IsRecordedRolanciaRasArrival(StartLocation position)=>
        position.Floor==1&&(position.Map switch {
            2=>AlphaRasArrivals.Contains((position.X,position.Y)),
            10002=>BetaRasArrivals.Contains((position.X,position.Y)),
            _=>false
        });

    internal static StartLocation? PickRasArrival(StartLocation destination,int originMap,Func<StartLocation,bool> available){
        if(originMap<0||destination.Map is not (2 or 10002))
            return available(destination)?destination:null;
        var points=destination.Map==2?AlphaRasArrivals:BetaRasArrivals;
        var candidates=points.Select(point=>new StartLocation(destination.Map,point.X,point.Y,1)).Where(available).ToArray();
        return candidates.Length==0?null:candidates[Random.Shared.Next(candidates.Length)];
    }

    public static StartLocation? RasDestination(uint map,int originMap){
        if(map>=30000)return null;
        // A-domain Rolancia and Epiloria use observed arrival coordinates.
        // The other city anchors come from the client's minimap or a recorded city entrance.
        return (map%10000) switch {
            2 => StartLocation.Rolancia with {Map=(int)map},
            1002 when map==1002 => new(1002,62,76,0),
            1002 => new((int)map,map==21002?71:62,69,0),
            2002 when map==2002 => new(2002,88,93,0),
            2002 when map==12002 && originMap==12002 => new(12002,88,113,0),
            2002 when map==12002 => new(12002,111,93,0),
            2002 => new((int)map,99,95,0),
            3002 => new((int)map,100,53,0),
            4002 when map is 4002 or 14002 => new((int)map,129,49,1),
            4002 => new((int)map,135,120,1),
            _ => null
        };
    }
}

public sealed record StartLocation(int Map,int X,int Y,byte Floor){
    // Observed entry into Rolancia, not an assertion about new-character spawn rules.
    public static StartLocation Rolancia=>new(2,159,88,1);
    public static StartLocation Load(string path)=>File.Exists(path)?JsonSerializer.Deserialize<StartLocation>(File.ReadAllText(path))??throw new InvalidDataException("Invalid start location"):Rolancia;
}
public sealed record AppearanceProfile(string Appearance,ushort[] Equipment){
    public void Validate(){
        if(Appearance.Length!=12||!Appearance.All(Uri.IsHexDigit)||Equipment is not {Length:10})throw new InvalidDataException("Invalid appearance profile");
    }
    public static Dictionary<string,AppearanceProfile> Load(string path){
        var profiles=File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,AppearanceProfile>>(File.ReadAllText(path))??[]:[];
        foreach(var profile in profiles.Values)profile.Validate();return profiles;
    }
}
