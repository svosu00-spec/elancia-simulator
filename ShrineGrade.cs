namespace ElanciaLocal;

internal sealed record ShrineGrade(string Name,CombatAbility? Combat=null){
    internal const uint Offering=1_000_000, GemCount=10;
    internal const byte MaxRank=30,InputLimit=32;
    internal const ushort GemSprite=0x8EE8;
    internal const string GemName="정령의보석",GemKey="spirit-gem";
    internal static readonly ShrineGrade Conversation=new(ConversationTraining.Name);
    internal static readonly ShrineGrade Angling=new(ShrineJob.FishingSkill);
    internal static readonly ShrineGrade[] Skills=[
        ..CombatAbilities.All.Concat(CombatAbilities.Optional).Select(ability=>new ShrineGrade(ability.Name,ability)),
        Conversation,Angling
    ];

    internal static ShrineGrade? Find(string name)=>Skills.FirstOrDefault(skill=>
        skill.Name==name||skill.Combat==CombatAbilities.Spear&&name is "창" or "특수창"||
        skill.Combat==CombatAbilities.Staff&&name is "봉" or "해머");

    internal byte Rank(Character c)=>SavedRank(c,Name,Combat==CombatAbilities.Harp?c.HarpGrade:(byte)0);
    internal static byte SavedRank(Character c,string name,byte fallback=0)=>
        Math.Min(MaxRank,c.SkillGrades?.GetValueOrDefault(name)??fallback);
    internal static ushort GradedReference(ushort reference,int ranks)=>
        ranks==0?reference:(ushort)Math.Min(ushort.MaxValue,Math.Ceiling(reference*Math.Pow(1.2,ranks)-1e-8));
    internal static bool IsGem(ItemPlacement item)=>NpcMaterials.Matches(item,GemSprite,GemName);

    internal string? Refusal(Character c){
        if(c.AwaitingRevival||!ShrineDialog.InRange(c))return "성당에서 다시 시도해 주세요.";
        if(Rank(c)>=MaxRank)return "이미 최대 그레이드에 도달했습니다.";
        ushort mastery=Combat is {} ability?CombatAbilities.Mastery(c,ability):
            this==Angling?Fishing.Mastery(c):ConversationTraining.Mastery(c.ConversationCounters??0,Rank(c));
        if(mastery<10000)return "어빌리티가 100.00에 도달해야 그레이드를 올릴 수 있습니다.";
        if(c.Gold<Offering||NpcMaterials.Count(c,IsGem)<GemCount)
            return "100만 갈리드와 정령의보석 10개가 필요합니다.";
        return null;
    }

    internal Character Advance(Character c){
        var ranks=new Dictionary<string,byte>(c.SkillGrades??[],StringComparer.Ordinal){[Name]=(byte)(Rank(c)+1)};
        var updated=c with {
            SkillGrades=ranks, Gold=c.Gold-Offering, Items=NpcMaterials.Consume(c,IsGem,GemCount),
            CombatExperience=CombatAbilities.Experience(c), Fame=CombatAbilities.Fame(c)
        };
        if(this==Angling)return updated with {FishingPoints=0,FishingLearned=true};
        if(Combat is not {} ability)return updated with {ConversationCounters=0};
        var points=new Dictionary<byte,uint>(c.Abilities??[]){[ability.Slot]=0};
        return updated with {Abilities=points,AbilityCountsVersion=CombatAbilities.CounterVersion};
    }
}
