namespace ElanciaLocal;

internal enum ErenoaPage : ushort { Menu=1, Offer, Space, Granted, Use, Cost }

internal static class ErenoaQuest {
    internal const string AratKey="arat-dara";
    internal const ushort AratSprite=0x82A8;
    internal static readonly NpcCraftingRecipe Exchange=new("날개 교환",AratKey,
        new CraftingMaterial("유익인의날개",0x84BC,100));

    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==219&&npc.Name=="에레노아"&&npc.Sprite==0x4074;

    internal static byte[] Packet(LocalNpc npc,ErenoaPage page){
        var (text,options,next)=page switch {
            ErenoaPage.Menu=>("",new[]{"날개 교환","에레노아 스토리"},false),
            ErenoaPage.Offer=>("유익인의날개 100개를 가져오시면 아라트(다라)로 바꿔 드릴게요. 교환하시겠어요?",new[]{"예","아니오"},false),
            ErenoaPage.Space=>("아라트(다라)의 무게는 1이에요. 소지품에 받을 자리가 있나요?",null,true),
            ErenoaPage.Granted=>("아라트(다라)를 받았습니다.",null,true),
            ErenoaPage.Use=>("날개를 가져와 주셔서 고마워요. 아라트(다라)를 사용하면 다라 마법을 배울 수 있어요.",null,true),
            _=>("다라는 다뉴 속성이에요. 다니움과 마력이 필요하고, 마법 레벨에 따라 소모량이 달라져요.",null,false)
        };
        return GamePackets.NpcPursuitPage(npc,text,options,(ushort)page,next);
    }
}
