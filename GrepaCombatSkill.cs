namespace ElanciaLocal;

internal sealed record GrepaCombatSkill(byte Slot,string Name,string Job,byte RequiredJobPoints,
    CombatAbility RequiredAbility,uint Price,ushort Icon,ushort OfferPage,string CheckPath){
    internal const ushort Reference=6;
    internal const int UseIntervalMs=1200;
    internal const int BodyBlowIntervalMs=1000;
    const string SkillMenu="3901006F/3A0100DC00310101";
    internal const string MartialJobCheck=SkillMenu+"/3A0100DC00A70103/3A0100DC016A00/3A0100DC016E00";
    // Icon and action type come from S2C 1:122318. Slots are allocated by the local server.
    internal static readonly GrepaCombatSkill BodyBlow=new(14,"몸통지르기","무도가",75,
        CombatAbilities.All[0],20_000,33,371,MartialJobCheck+"/3A0100DC017300");
    internal static readonly GrepaCombatSkill RemoveArmor=new(15,"RemoveArmor","자객",50,
        CombatAbilities.All[3],25_000,34,402,SkillMenu+"/3A0100DC00A70102/3A0100DC019200");
    internal static readonly GrepaCombatSkill[] All=[BodyBlow,RemoveArmor];

    internal string OfferPath=>$"grepa-skill-{Slot}";
    internal int IntervalAfter(GrepaCombatSkill previous)=>
        this==BodyBlow&&previous==BodyBlow?BodyBlowIntervalMs:UseIntervalMs;
    internal uint? Counters(Character character)=>character.GrepaSkillCounters?.TryGetValue(Slot,out uint points)==true?points:null;
    internal static ushort Mastery(uint counters)=>(ushort)Math.Min(10_000u,counters/Reference);
    internal string? Refusal(Character character){
        if(Counters(character).HasValue)return $"이미 {Name} 기술을 배웠네.";
        if(character.Job!=Job)return $"{Job} 직업을 가진 뒤 다시 오게나.";
        if(CombatAbilities.Mastery(character,RequiredAbility)<7500)return $"{RequiredAbility.Name} 75.00 이상이어야 배울 수 있네.";
        if((character.JobPoints?.GetValueOrDefault(Job)??0)<RequiredJobPoints)
            return $"{Job} 잡포인트가 {RequiredJobPoints} 이상이어야 배울 수 있네.";
        var stats=character.JobStats??new JobAttributes();
        if(this==RemoveArmor&&(stats.Int<10||stats.Agi<20))return "순수 지능 10과 순수 민첩 20 이상이 필요하네.";
        return character.Gold<Price?$"{Price:N0}갈리드가 필요하네.":null;
    }

    internal static byte[] RemovableSlots(ushort mastery)=>mastery switch{
        <1000=>[7],
        <2000=>[8],
        _=>new byte[]{7,8,6,4,16,15,2,13,3,1}[..Math.Min(10,mastery/1000+1)]
    };
}
