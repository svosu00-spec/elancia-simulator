namespace ElanciaLocal;

internal static class KayGatheringQuest
{
    internal const string MenuPath="390104E9";
    internal const string OfferPath=MenuPath+"/3A010000000200/3A010000000200";
    internal const string AcceptPath=OfferPath+"/3A01000000020101";
    internal const string DeclinePath=OfferPath+"/3A01000000020102";
    internal const string MaterialKey="blue-slime-fragment";
    internal const string MaterialName="슬라임조각(파란색)";
    internal const ushort MaterialSprite=0x884C;
}

internal sealed record GatheringQuest(string Key,string NpcName,int Map,ushort NpcSprite,
    string MaterialKey,string MaterialName,ushort MaterialSprite,uint Required,uint Reward,int Hours,string Region){
    internal const string OfferPath="gathering-offer",TurnInPath="gathering-turn-in";
    internal const string PiniAcceptPath="3901006F/3A010000000200/3A010000000200/3A01000000020101";
    internal const string RatTailKey="large-rat-tail",RatEyeKey="gray-rat-eye",ScaleKey="merman-scale";
    internal const string ZombieBoneKey="zombie-bone";
    internal static readonly GatheringQuest Kay=new("kay-slime","케이",206,0x4078,
        KayGatheringQuest.MaterialKey,KayGatheringQuest.MaterialName,KayGatheringQuest.MaterialSprite,10,1000,2,"로랜시아 해저동굴");
    internal static readonly GatheringQuest Shu=new("shu-tail","슈",204,0x4070,RatTailKey,"큰쥐꼬리",0x8514,20,500,12,"로랜시아 지하 하수도");
    internal static readonly GatheringQuest Kadilla=new("kadilla-eye","카딜라",204,0x4007,RatEyeKey,"회색쥐눈",0x8520,20,1500,12,"로랜시아 지하 하수도");
    internal static readonly GatheringQuest Pini=new("pini-scale","피니",207,0x407C,ScaleKey,"비늘",0x888C,20,1500,12,"로랜시아 해저동굴");
    internal static readonly GatheringQuest Silhouette=new("silhouette-zombie","실루엣",206,0x4072,
        ZombieBoneKey,"좀비의뼈",0x8888,5,1000,5,"로랜시아 광산 지하 3층");
    static readonly GatheringQuest[] Quests=[Kay,Shu,Kadilla,Pini,Silhouette];
    internal static GatheringQuest? ForNpc(LocalNpc npc)=>Quests.FirstOrDefault(q=>
        q.Map==npc.Map%10000&&q.NpcName==npc.Name&&q.NpcSprite==npc.Sprite);
    internal bool IsMaterial(ItemPlacement placement){
        return NpcMaterials.Matches(placement,MaterialSprite,MaterialName);
    }
    internal ulong Count(Character character)=>NpcMaterials.Count(character,IsMaterial);
}

internal enum GatheringTurnIn { Completed, Expired, MissingItems, WalletFull }

internal static class NpcMaterials{
    internal static bool Matches(ItemPlacement placement,ushort sprite,string name){
        if(placement.InventorySlot==0)return false;
        var item=EquipmentItems.Get(placement.Key);
        return item.Sprite==sprite&&item.Name==name;
    }
    internal static ulong Count(Character character,Func<ItemPlacement,bool> matches)=>(character.Items??[])
        .Where(matches).Aggregate(0UL,(sum,item)=>sum+item.Quantity);
    internal static ItemPlacement[] Consume(Character character,Func<ItemPlacement,bool> matches,uint quantity){
        var remaining=new List<ItemPlacement>();
        foreach(var item in character.Items??[]){
            uint used=quantity>0&&matches(item)?Math.Min(quantity,item.Quantity):0;
            quantity-=used;
            if(used==0)remaining.Add(item);
            else if(used<item.Quantity)remaining.Add(item with {Quantity=item.Quantity-used});
        }
        return remaining.ToArray();
    }
}
