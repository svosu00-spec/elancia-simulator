namespace ElanciaLocal;

internal sealed record CraftingMaterial(string Name,ushort Sprite,uint Quantity){
    internal bool Matches(ItemPlacement item)=>NpcMaterials.Matches(item,Sprite,Name);
    internal bool IsAvailable(Character character)=>NpcMaterials.Count(character,Matches)>=Quantity;
    public override string ToString()=>$"{Name} {Quantity}개";
}

internal sealed record NpcCraftingRecipe(string Menu,string ProductKey,params CraftingMaterial[] Materials){
    internal bool IsMaterial(ItemPlacement item)=>Materials.Any(material=>material.Matches(item));
    internal string Ingredients=>string.Join(", ",Materials.AsEnumerable());
}

internal static class TeddyTailoring{
    internal const string MenuPath="teddy-tailoring";
    internal const string RecordedShoesPath="3901006F/3A0100F000010102";
    internal const string RecordedPantsMaterialsPath="3901006F/3A0100F000010101/3A0100F0000600";
    internal const string WingKey="uyikin-wing",OldShoeKey="old-shoes";
    internal const string PantsKey="teddy-pants",ShoesKey="teddy-shoes";
    internal static readonly NpcCraftingRecipe[] Recipes=[
        new("바지제작",PantsKey,new CraftingMaterial("유익인의날개",0x84BC,3)),
        new("슈즈제작",ShoesKey,new CraftingMaterial("낡은신발",0x804E,10))
    ];
    internal static bool IsTeddy(LocalNpc npc)=>npc.Map%10000==207&&npc.Name=="테디"&&npc.Sprite==0x4079;
}

internal enum NpcCraftingResult { Crafted, MissingItems, InventoryFull }
