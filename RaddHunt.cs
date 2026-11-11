namespace ElanciaLocal;

public enum HuntKind : byte { Rooster=1, Spider=2, Hen=3, Pig=4, RedSpider=5, WhiteSpider=6, PoisonSpider=7 }
public sealed record RaddHuntProgress(HuntKind Kind, long Deadline, int Kills=0);

internal sealed record RaddHunt(HuntKind Kind,string Monster,ushort Sprite,int Map,uint Reward)
{
    internal const string OfferPath="radd-hunt-offer",SelectPath="radd-hunt-select",TurnInPath="radd-hunt-turn-in";
    internal const int Required=50;
    internal static readonly RaddHunt Rooster=new(HuntKind.Rooster,"수탉",0x43F2,5,300);
    internal static readonly RaddHunt Spider=new(HuntKind.Spider,"거미",0x43FD,232,900);
    // Only the rooster and spider payouts are documented per species.
    // Keep the other assignments within the observed tier ranges.
    internal static readonly RaddHunt Hen=new(HuntKind.Hen,"암탉",0x43F1,5,300);
    internal static readonly RaddHunt Pig=new(HuntKind.Pig,"돼지",0x43F3,5,400);
    internal static readonly RaddHunt RedSpider=new(HuntKind.RedSpider,"빨간거미",0x43FC,232,900);
    internal static readonly RaddHunt WhiteSpider=new(HuntKind.WhiteSpider,"하얀거미",0x43FE,232,950);
    internal static readonly RaddHunt PoisonSpider=new(HuntKind.PoisonSpider,"독거미",0x43FF,232,1000);
    internal static readonly RaddHunt[] Beginner=[Rooster,Hen,Pig];
    internal static readonly RaddHunt[] Intermediate=[Spider,RedSpider,WhiteSpider,PoisonSpider];
    // The official sewer list includes these variants. Use reachable local tiles
    // around the recorded spider population until their retail spawns are captured.
    internal static readonly RecordedMonster[] SewerSpawns=[
        new(RedSpider.Monster,RedSpider.Sprite,232,128,78,2,6,0),
        new(WhiteSpider.Monster,WhiteSpider.Sprite,232,129,80,2,6,0),
        new(WhiteSpider.Monster,WhiteSpider.Sprite,232,132,84,2,6,0),
        new(PoisonSpider.Monster,PoisonSpider.Sprite,232,128,82,2,6,0),
        new(PoisonSpider.Monster,PoisonSpider.Sprite,232,131,85,2,6,0)
    ];
    internal static RaddHunt? For(HuntKind kind)=>
        Beginner.FirstOrDefault(hunt=>hunt.Kind==kind)??Intermediate.FirstOrDefault(hunt=>hunt.Kind==kind);
    internal static bool IsNpc(LocalNpc npc)=>npc.Map is >=0 and <30000&&npc.Map%10000==208&&npc.Name=="라드"&&npc.Sprite==0x406A;
    internal bool Counts(FieldObject monster)=>monster.Map is >=0 and <30000&&
        (monster.Map%10000==Map||Map==5&&monster.Map%10000==4)&&
        monster.Name==Monster&&monster.Sprite==Sprite;
    internal static RaddHuntProgress? Active(Character character,DateTimeOffset now)=>
        character.RaddHunt is {} progress&&progress.Deadline>now.ToUnixTimeMilliseconds()&&For(progress.Kind)!=null?progress:null;
}

internal enum RaddReward { Paid, Expired, Incomplete, WalletFull }
