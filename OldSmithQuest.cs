namespace ElanciaLocal;

public enum OldSmithStage { None, Water, Rings, Complete }

internal enum OldSmithPage : ushort { Menu=1, Flower, Water, Rings, Golem, Complete }
internal enum OldSmithTurnIn { NotReady, MissingMaterials, InventoryFull, WaterAccepted, RingGranted }

internal static class OldSmithQuest {
    internal const string WaterKey="holy-water", RingKey="old-smith-ring";
    internal const ushort WaterSprite=0x8CDD, RingSprite=0x80AD;
    internal const uint WaterCount=5;
    internal const string RingsText="브리디움링, 다니움링, 마하디움링, 브라키디움링, 엘사리아링, 테사랏트링";
    static readonly string[] RingKeys=["bridium-ring","danium-ring","mahadium-ring","brakidium-ring","elsaria-ring","tessarat-ring"];

    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==256&&npc.Name=="올드스미스"&&npc.Sprite==0x4008;
    internal static bool IsWater(ItemPlacement item)=>NpcMaterials.Matches(item,WaterSprite,"성스러운물");
    internal static bool IsIngredient(ItemPlacement item)=>IsWater(item)||RingKeys.Any(key=>IsRing(item,key));
    static bool IsRing(ItemPlacement item,string key){
        var ring=EquipmentItems.Get(key);
        return NpcMaterials.Matches(item,ring.Sprite,ring.Name);
    }

    internal static ItemPlacement[]? Exchange(Character character,OldSmithStage stage){
        if(stage==OldSmithStage.Water)
            return NpcMaterials.Count(character,IsWater)<WaterCount?null:NpcMaterials.Consume(character,IsWater,WaterCount);
        var remaining=character.Items??[];
        foreach(string key in RingKeys){
            var inventory=character with {Items=remaining};
            if(NpcMaterials.Count(inventory,item=>IsRing(item,key))<1)return null;
            remaining=NpcMaterials.Consume(inventory,item=>IsRing(item,key),1);
        }
        return remaining;
    }

    internal static OldSmithPage WeaponPage(OldSmithStage stage)=>stage switch {
        OldSmithStage.None=>OldSmithPage.Flower,
        OldSmithStage.Water=>OldSmithPage.Water,
        OldSmithStage.Rings=>OldSmithPage.Rings,
        _=>OldSmithPage.Complete
    };

    internal static byte[] Packet(LocalNpc npc,OldSmithPage page)=>page switch {
        OldSmithPage.Menu=>GamePackets.NpcPursuitPage(npc,"무엇이 궁금한가?",["골든고렘","무기제작"],(ushort)page),
        OldSmithPage.Flower=>GamePackets.NpcPursuitInput(npc,"이 늙은 대장장이가 돌보는 것은 무엇인지 아는가?",(ushort)page,"대답",12),
        OldSmithPage.Water=>GamePackets.NpcPursuitPage(npc,"꽃을 살리려면 성스러운물 5개가 필요하네. 가져왔는가?",
            ["예","아니오"],(ushort)page),
        OldSmithPage.Rings=>GamePackets.NpcPursuitPage(npc,$"{RingsText}을 하나씩 가져오면 스미스의링을 만들어 주겠네.",
            ["재료 건네기","나중에"],(ushort)page),
        OldSmithPage.Golem=>GamePackets.NpcPursuitPage(npc,"이 섬에는 골든고렘이 산다네.",page:(ushort)page),
        _=>GamePackets.NpcPursuitPage(npc,"전에 만들어 준 스미스의링을 잘 간직하게.",page:(ushort)page)
    };
}
