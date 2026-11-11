namespace ElanciaLocal;

internal sealed record CookingRecipe(string Name,string Key,ushort MasteryRequired,string? Manual,params CraftingMaterial[] Materials){
    internal const string KitKey="recorded-800c-bfe4b8aec5b0c6ae", BookKey="recorded-8cd7-bfe4b8aec3a5";
    internal const string OmeletKey="raniche-omelet", PieKey="raniche-chocolate-pie";
    internal const string CakeKey="raniche-strawberry-cake";
    internal const ushort OmeletSprite=0x8358, PieSprite=0x81D0;
    internal const ushort CakeSprite=0x81C4;
    internal const ushort Script=0x1A3;
    internal static readonly CookingRecipe Egg=new("삶은달걀","boiled-egg",0,null,new CraftingMaterial("달걀",0x8BD7,1));
    internal static readonly CookingRecipe[] All=[
        Egg,
        new("오믈렛",OmeletKey,1000,"요리지침서(2-2)",new("감자",0x8BDF,2),new("소금",0x8BE5,2),
            new("후추",0x8C77,2),new("달걀",0x8BD7,4)),
        new("초컬릿 파이",PieKey,1000,"요리지침서(2-3)",new("달걀",0x8BD7,2),new("버터",0x8D0F,2),
            new("설탕",0x8BE4,2),new("밀가루 반죽",0x8C16,1),new("초컬릿 반죽",0x8C19,1)),
        new("딸기 케익",CakeKey,1000,"요리지침서(2-4)",new("달걀",0x8BD7,2),new("버터",0x8D0F,2),
            new("설탕",0x8BE4,2),new("밀가루 반죽",0x8C16,1),new("딸기",0x8CA1,1))
    ];
    internal static bool IsKitchen(LocalNpc npc)=>npc.Map%10000==204&&npc.Name=="카딜라"&&npc.Sprite==0x4007;
    internal static ushort Mastery(Character c)=>c.LifeMastery?.GetValueOrDefault(ShrineJob.CookingSkill)??0;
    internal string Ingredients=>string.Join(", ",Materials.AsEnumerable());
    internal bool IsMaterial(ItemPlacement item)=>Materials.Any(material=>material.Matches(item));
    internal string? Requirement(Character c,bool useKit){
        if(useKit&&!(c.Items??[]).Any(i=>i.InventorySlot>0&&i.Key==KitKey&&i.Quantity>0))
            return "요리키트가 필요합니다.";
        if(useKit&&!(c.Items??[]).Any(i=>i.InventorySlot>0&&i.Key==BookKey&&i.Quantity>0))
            return "요리책이 필요합니다.";
        if(Manual!=null&&NpcMaterials.Count(c,i=>NpcMaterials.Matches(i,0x8C56,Manual))==0)
            return $"{Manual}가 필요합니다.";
        return Mastery(c)<MasteryRequired?$"요리 어빌리티 {MasteryRequired/100m:0.00} 이상이 필요합니다.":null;
    }
}

internal enum CookingResult { Cooked, Requirements, MissingItems, InventoryFull }
