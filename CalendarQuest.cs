namespace ElanciaLocal;

public sealed record CalendarQuestProgress(int LastVisit=0,byte Visits=0,bool Claimed=false);
internal enum CalendarQuestResult { Visited, Rewarded, AlreadyVisited, NotReady, MissingWater, InventoryFull, Completed }

internal sealed record CalendarQuest(string Npc,int Map,string Name,string RingKey,string RingName,ushort Sprite,bool NeedsWater=false){
    internal const ushort Visit=1377, Claim=1422;
    internal const byte RequiredVisits=10;
    internal static readonly CalendarQuest Tree=new("나무보로",213,"나무이벤트","calendar-elsarion","특별한엘사리온링",0x802B,true);
    internal static readonly CalendarQuest Elder=new("할아버지",208,"경로효친","calendar-bridion","특별한브리디온링",0x8027);
    internal static readonly CalendarQuest Manners=new("예절보로",208,"예절교육","calendar-danion","특별한다니온링",0x8028);
    internal static readonly CalendarQuest[] All=[Tree,Elder,Manners];
    internal static CalendarQuest? For(LocalNpc npc)=>All.FirstOrDefault(quest=>npc.Map%10000==quest.Map&&npc.Name==quest.Npc);
    internal CalendarQuestProgress Progress(Character c)=>c.CalendarQuests?.GetValueOrDefault(RingKey)??new();
    internal byte[] Menu(LocalNpc npc)=>GamePackets.NpcDialog(npc,(npc.OpenDialog??throw new InvalidDataException("Missing event NPC dialog")) with {
        Mode=0,Options=[new(Name,Visit),new(Name+" 보상",Claim)]
    });
    internal static bool IsWater(ItemPlacement item)=>NpcMaterials.Matches(item,0x8CDD,"깨끗한물");
}
