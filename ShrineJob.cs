namespace ElanciaLocal;

internal sealed record ShrineJob(string Name,ushort Required,CombatAbility[] CombatSkills,string[]? LifeSkills=null){
    internal const uint Offering=5000;
    internal const byte MasterJobPoints=100;
    internal const string CookingSkill="요리";
    internal const string DeliverySkill="배달";
    internal const string HagglingSkill="흥정";
    internal const string FishingSkill="낚시";
    internal const string SenseSkill="Sense";
    internal static readonly ShrineJob[] All=[
        new("무도가",1500,[CombatAbilities.All[0],CombatAbilities.Staff]),
        new("검사",1500,[CombatAbilities.All[4],CombatAbilities.Greatsword]),
        new("검객",1500,[CombatAbilities.All[5],CombatAbilities.GreaterBlade]),
        new("자객",1500,[CombatAbilities.All[3]]),
        new("투사",1500,[CombatAbilities.Spear,CombatAbilities.Staff]),
        new("전사",1500,[CombatAbilities.All[2]]),
        new("악사",1500,[CombatAbilities.Harp]),
        new("상인",2500,[],[CookingSkill,DeliverySkill,HagglingSkill]),
        new("궁사",1500,[CombatAbilities.All[1]]),
        new("모험가",2500,[],["지하탐색","발굴",FishingSkill])
    ];

    internal string Requirement=>$"{string.Join(" 또는 ",LifeSkills??CombatSkills.Select(ability=>ability.Name).ToArray())} {Required/100m:0.00} 이상";
    IEnumerable<(string Skill,int Mastery)> CurrentMasteries(Character character)=>
        LifeSkills is {} skills
            ?skills.Select(skill=>(skill,skill==FishingSkill?(int)Fishing.Mastery(character):
                character.LifeMastery?.GetValueOrDefault(skill)??0))
            :CombatSkills.Select(ability=>(ability.Name,(int)CombatAbilities.Mastery(character,ability)));

    internal bool Qualifies(Character character)=>CurrentMasteries(character).Any(entry=>entry.Mastery>=Required);

    int Points(Character character)=>character.JobPoints?.GetValueOrDefault(Name)??0;
    internal uint Fee(Character character)=>Points(character)>=MasterJobPoints?0:Offering;

    internal string Progress(Character character){
        var masteries=string.Join(", ",CurrentMasteries(character).Select(entry=>$"{entry.Skill} {entry.Mastery/100m:0.00}"));
        return $"현재 어빌리티 {masteries}|{Name} 잡포인트 {Points(character)}/{MasterJobPoints}";
    }

    internal static int CombatLimit(string? job,CombatAbility ability){
        if(string.IsNullOrWhiteSpace(job)||job is "평민" or "무직")
            return LocalSpell.Elements.Contains(ability)?5000:4000;
        var active=All.FirstOrDefault(entry=>entry.Name==job);
        if(active==null||active.CombatSkills.Contains(ability)||job=="악사"&&LocalSpell.Elements.Contains(ability))return 10000;
        return 5000;
    }

    internal static Character AwardCombat(Character character,CombatAbility ability,int before,int after){
        var job=All.FirstOrDefault(entry=>entry.Name==character.Job);
        return job!=null&&(job.CombatSkills.Contains(ability)||job.Name=="상인"&&ability==CombatAbilities.All[3]||
            job.Name=="악사"&&LocalSpell.Elements.Contains(ability))
            ?job.Award(character,before,after):character;
    }

    internal static Character AwardLife(Character character,string skill,int before,int after){
        var job=All.FirstOrDefault(entry=>entry.Name==character.Job);
        return job!=null&&job.TrainsLifeSkill(skill)?job.Award(character,before,after):character;
    }

    internal static int LifeLimit(Character character,string skill)=>
        All.Any(job=>job.Name==character.Job&&job.TrainsLifeSkill(skill))||
        character.Job=="모험가"&&skill==MeditationTraining.Name?10_000:5_000;

    bool TrainsLifeSkill(string skill)=>LifeSkills?.Contains(skill)==true||
        Name=="모험가"&&(skill==SenseSkill||skill==ConversationTraining.Name)||
        Name=="악사"&&skill==MeditationTraining.Name||
        GrepaCombatSkill.All.Any(ability=>ability.Name==skill&&ability.Job==Name);

    Character Award(Character character,int before,int after){
        int previous=Points(character);
        // Award only mastery milestones crossed while this job is active.
        int next=Math.Min(MasterJobPoints,previous+after/200-before/200);
        if(next<=previous)return character;
        var points=new Dictionary<string,byte>(character.JobPoints??[]);
        points[Name]=(byte)next;
        var stats=character.JobStats??new JobAttributes();
        int Raise(int value,int interval,int cap)=>value+Math.Min(next/interval-previous/interval,Math.Max(0,cap-value));
        int Lower(int value,int interval,int maxLoss)=>Math.Max(WorldSettings.BaseAttribute,
            value-(Math.Min(next/interval,maxLoss)-Math.Min(previous/interval,maxLoss)));
        var (str,intelligence,vit,agi)=stats;
        JobAttributes updated=Name switch {
            "무도가"=>new(Lower(str,5,20),Lower(intelligence,3,10),Raise(vit,3,30),agi),
            "검사"=>new(Raise(str,3,30),Raise(intelligence,10,10),vit,Lower(agi,8,10)),
            "검객"=>new(Raise(str,10,10),Lower(intelligence,10,10),vit,Raise(agi,4,30)),
            "자객"=>new(Raise(str,7,20),Lower(intelligence,5,10),vit,Raise(agi,6,20)),
            "투사"=>new(Raise(str,3,30),Lower(intelligence,8,10),Raise(vit,10,10),Lower(agi,10,10)),
            "전사"=>new(Raise(str,5,30),Lower(intelligence,9,10),Raise(vit,10,10),Lower(agi,10,10)),
            "악사"=>new(Lower(str,1,10),Raise(intelligence,4,30),Lower(vit,4,10),Raise(agi,10,10)),
            "상인"=>new(Lower(str,2,10),Raise(intelligence,10,10),Raise(vit,10,10),Raise(agi,4,30)),
            "궁사"=>new(Raise(str,10,10),Lower(intelligence,5,10),Lower(vit,8,10),Raise(agi,3,30)),
            "모험가"=>new(Raise(str,10,10),Raise(intelligence,10,10),Raise(vit,3,30),Raise(agi,10,10)),
            _=>throw new InvalidOperationException($"Unknown shrine job {Name}")
        };
        return character with {JobPoints=points,JobStats=updated};
    }
}

public sealed record JobAttributes(int Str=WorldSettings.BaseAttribute,int Int=WorldSettings.BaseAttribute,
    int Vit=WorldSettings.BaseAttribute,int Agi=WorldSettings.BaseAttribute);

internal enum JobChangeResult { Changed, AlreadySelected, SkillRequired, InsufficientGold }
