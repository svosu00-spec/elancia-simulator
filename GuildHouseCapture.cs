namespace ElanciaLocal;

internal sealed record GuildHouseCapture(WorldSession Challenger,StartLocation Post,long Due){
    internal const uint Fee=1000, Refund=500;
    internal const int HoldSeconds=30*60;
    internal const ushort FlagSprite=0x4061;
    internal const string FlagName="길드기";
    internal const string MasterRequired="당신은 당신 길드의 {=f길드마스터{=a가 아니군요!! 길드마스터가 와야지만 이 길드집을 점령할 수 있답니다!!";

    internal static bool IsFlag(LocalNpc npc)=>WorldSettings.IsGuildHouse(npc.Map)&&
        npc.Sprite==FlagSprite&&npc.Name==FlagName;

    internal static bool AtFlag(Character character,LocalNpc flag)=>
        character.Map==flag.Map&&character.Floor==flag.Floor&&character.X==flag.X&&character.Y==flag.Y-1;

    internal static bool IsGuildMaster(Character character)=>character.GuildLeaderId==character.Id&&
        !string.IsNullOrEmpty(character.GuildName);

    internal static bool IsOpen(DateTimeOffset utc,int seconds=0){
        var local=utc.ToOffset(TimeSpan.FromHours(9));
        return local.DayOfWeek!=DayOfWeek.Wednesday&&local.Hour>=9&&
            local.TimeOfDay.Add(TimeSpan.FromSeconds(seconds))<TimeSpan.FromDays(1);
    }

    internal static string Duration(int seconds)=>seconds%60==0?$"{seconds/60}분":$"{seconds}초";
}
