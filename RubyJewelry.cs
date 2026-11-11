namespace ElanciaLocal;

internal sealed record JewelryRecipe(string ProductKey,string RingKey,uint Fee,string? StoneKey=null,string? Element=null){
    internal const uint RingCount=2, StoneCount=10;
    internal bool IsRing(ItemPlacement placement)=>Matches(placement,RingKey);
    internal bool IsStone(ItemPlacement placement)=>StoneKey!=null&&Matches(placement,StoneKey);
    internal bool HasMaterials(Character character)=>NpcMaterials.Count(character,IsRing)>=RingCount&&
        (StoneKey==null||NpcMaterials.Count(character,IsStone)>=StoneCount);
    internal string Requirements=>
        $"{EquipmentItems.Get(RingKey).Name} {RingCount}개"+
        (StoneKey==null?"":$", {EquipmentItems.Get(StoneKey).Name} {StoneCount}개");

    static bool Matches(ItemPlacement placement,string key){
        var material=EquipmentItems.Get(key);
        return NpcMaterials.Matches(placement,material.Sprite,material.Name);
    }
}

internal enum JewelryResult { MissingMaterials, InsufficientGold, InventoryFull, Crafted }

internal sealed class RubyJewelry{
    internal const string OldIron="old-iron-ring", OldSilver="old-silver-ring", OldGold="old-gold-ring";
    internal const string Bridium="recorded-8c4f-baeab8aeb5f0bff2", Danium="recorded-8c50-b4d9b4cfbff2",
        Mahadium="recorded-8c51-b8b6c7cfb5f0bff2", Brakidium="recorded-8c52-baeab6f3c5b0b5f0bff2",
        Elsaria="recorded-8c53-bfa4bbe7b8aebec6", Tessarat="recorded-8c54-c5d7bbe7b6f9c6ae";
    internal static readonly JewelryRecipe[] Recipes=[
        new("iron-ring",OldIron,5),new("silver-ring",OldSilver,5),new("gold-ring",OldGold,5),
        new("bridium-ring","iron-ring",500,Bridium,"불"),new("danium-ring","iron-ring",500,Danium,"물"),
        new("mahadium-ring","iron-ring",500,Mahadium,"전기"),new("brakidium-ring","iron-ring",500,Brakidium,"땅"),
        new("elsaria-ring","silver-ring",500,Elsaria,"신성"),new("tessarat-ring","gold-ring",500,Tessarat,"암흑")
    ];

    ushort page=1;
    JewelryRecipe? pending;
    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==215&&npc.Sprite==0x4069&&npc.Name=="루비";
    internal byte[] Open(LocalNpc npc)=>GamePackets.NpcPursuitPage(npc,"보석상에 오신 걸 환영해요. 무엇을 도와드릴까요?",["가공","설명"],page);

    internal (byte[]? Reply,JewelryRecipe? Craft,bool Finished) Advance(LocalNpc npc,byte[] request){
        if(request[1]!=1||request[6]!=0||request[7]!=0)return (null,null,false);
        ushort requested=(ushort)((request[8]<<8)|request[9]);
        if(request.Length==11&&requested==page)return (null,null,true);
        if(request.Length!=12||requested!=page+1)return (null,null,false);
        int choice=request[11];
        if(page==1&&choice==1){
            page=2;
            return (GamePackets.NpcPursuitPage(npc,"어떤 반지를 가공해 드릴까요?",
                Recipes.Select(recipe=>EquipmentItems.Get(recipe.ProductKey).Name).ToArray(),page),null,false);
        }
        if(page==1&&choice==2){
            page=3;
            return (GamePackets.NpcPursuitPage(npc,"정령석을 써서 각 속성의 반지를 만들 수 있어요. 어떤 정령석이 궁금하세요?",
                Recipes.Where(recipe=>recipe.StoneKey!=null).Select(recipe=>EquipmentItems.Get(recipe.StoneKey!).Name).ToArray(),page),null,false);
        }
        if(page==2&&choice>=1&&choice<=Recipes.Length){
            pending=Recipes[choice-1];
            page=4;
            return (GamePackets.NpcPursuitPage(npc,$"{pending.Requirements}와 가공비 {pending.Fee}갈리드가 필요해요. 가공할까요?",
                ["예","아니오"],page),null,false);
        }
        if(page==3&&choice is >=1 and <=6){
            var recipe=Recipes[choice+2];
            return (GamePackets.NpcPursuitPage(npc,$"{recipe.Requirements}와 {recipe.Fee}갈리드로 {EquipmentItems.Get(recipe.ProductKey).Name}을 가공해요. 이 반지는 {recipe.Element} 속성 반지예요."),null,true);
        }
        if(page==4&&choice is 1 or 2)return (null,choice==1?pending:null,true);
        return (null,null,false);
    }
}
