namespace ElanciaLocal;

public enum KondQuestStage { None, Referred, AwaitingShield, Completed }

internal enum KondRewardResult { Received, NotReady, MissingShield, InventoryFull }

internal static class KondQuest{
    internal const string RodKey="kond-fishing-rod";
    internal const string OfferPath="kond-offer",TurnInPath="kond-turn-in";
    internal const ushort RodSprite=0x8228;
    internal const ushort OfferPage=1,TurnInPage=3;
    internal const uint RequiredMastery=2000;
    internal static bool IsTrainer(LocalNpc npc)=>npc.Map%10000==202&&npc.Name=="수련장"&&npc.Sprite==0x4004;
    internal static bool IsOblo(LocalNpc npc)=>npc.Map%10000==214&&npc.Name=="오블로"&&npc.Sprite==0x4008;
    internal static NpcDialogData ObloMenu(LocalNpc npc)=>new(13,1,1,npc.Sprite,0,0x01400800,"",
        X:(ushort)npc.X,Y:(ushort)npc.Y,MenuCodes:[0,0,0,111]);
    // Fishing is the adventurer mastery currently tracked by the local server.
    internal static bool MeetsMastery(Character character)=>Fishing.Mastery(character)>=RequiredMastery;
    internal static bool IsShield(ItemPlacement item)=>NpcMaterials.Matches(item,SektiSmithing.ShieldSprite,"비늘방패");
}
