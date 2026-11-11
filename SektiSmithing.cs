namespace ElanciaLocal;

internal static class SektiSmithing{
    internal const string ShieldKey="scale-shield",ShellKey="snail-shell",BoneKey="gold-skeleton-bone",ClothKey="cloth";
    internal const string RecordedAcceptPath="3901006F/3A01010700070101/3A010107000C00/3A010107000D00/3A010107000E00/3A010107000F0101";
    internal const ushort ShieldSprite=0x8227,ShellSprite=0x8884,BoneSprite=0x88A8,ClothSprite=0x85E4;
    internal static readonly NpcCraftingRecipe Recipe=new("비늘방패 제작",ShieldKey,
        new("비늘",0x888C,50),new("달팽이껍질",ShellSprite,20),new("황금해골의뼈",BoneSprite,20),new("천",ClothSprite,5));
    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==203&&npc.Name=="세크티"&&npc.Sprite==0x406F;
    internal static string[] MaterialLoot(string monster)=>monster switch{
        "달팽이" or "무서운달팽이"=>[ShellKey],
        "황금해골" or "황금해골(가드)"=>[BoneKey],
        "오리"=>[ClothKey],
        _=>[]
    };
}
