namespace ElanciaLocal;

internal static class MutaiButcher{
    internal const string MenuPath="mutai-butcher";
    internal const string ProductKey="raw-chicken-leg";
    internal const string ProductName="닭다리(날것)";
    internal const ushort ProductSprite=0x80D8;
    internal const uint ChickenSellPrice=6,ProductSellPrice=12;

    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==200&&npc.Name=="무타이"&&npc.Sprite==0x406B;
    internal static bool IsChicken(ItemPlacement placement)=>NpcMaterials.Matches(placement,0x8C30,"닭고기");
    internal static bool IsProduct(ItemPlacement placement)=>NpcMaterials.Matches(placement,ProductSprite,ProductName);
}

internal enum ButcherResult { Dressed, MissingChicken, InventoryFull }
