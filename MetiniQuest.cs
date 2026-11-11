namespace ElanciaLocal;

public enum MetiniStage : byte { None, Paid, Completed }

internal enum MetiniPage : ushort {
    Explanation=119, Recital, Payment, Hunt, Ready, Review, Verse1, Verse2, Verse3, Verse4, Verse5, Completed
}

internal enum MetiniResult { Applied, AlreadyPaid, MissingGold, MissingStones, NotReady, InventoryFull }

internal static class MetiniQuest {
    internal const ushort Script=246, AratSprite=ErenoaQuest.AratSprite;
    internal const uint Fee=2500, Stones=20, RequiredHp=100;
    internal const byte Roosters=50, InputLimit=64;
    internal const string AratKey="arat-rimedium";
    internal const string HealthCheckPath="3901006F/3A0100F6005A00/3A0100F6005B00/3A0100F6005C00/3A0100F6005D00/3A0100F6005E0101/3A0100F6006900/3A0100F6006A00/3A0100F6006B00/3A0100F6006C0101/3A0100F6007400/3A0100F6007500";
    internal static readonly string[] Chant=[
        "빛 질서의 여신의 이름으로 명하노니",
        "빛의 정령들이여",
        "상처를 치유하는 빛이 되어",
        "어둠에서 빛으로 인도 하여라",
        "알티멧 리메듐!!"
    ];

    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==218&&npc.Name=="메티니"&&npc.Sprite==0x47D6;
    internal static bool IsElsaria(ItemPlacement item)=>NpcMaterials.Matches(item,0x8C53,"엘사리아");
    internal static bool MeetsHealthRequirement(Character character)=>character.BaseHp([])>=RequiredHp;
    internal static bool IsVerse(MetiniPage page)=>page is >=MetiniPage.Verse1 and <=MetiniPage.Verse5;
    internal static MetiniPage Resume(Character character)=>character.MetiniProgress==MetiniStage.Completed?MetiniPage.Completed:
        character.MetiniRoosters>=Roosters?MetiniPage.Ready:MetiniPage.Hunt;

    internal static byte[] Packet(LocalNpc npc,MetiniPage page,Character character){
        if(IsVerse(page))
            return GamePackets.NpcPursuitInput(npc,$"주문의 {(ushort)page-(ushort)MetiniPage.Verse1+1}번째 구절을 적어 주세요.",
                (ushort)page,"주문",InputLimit,Script);
        (string Text,string[]? Options,bool Next) dialog=page switch{
            MetiniPage.Explanation=>("리메듐은 엘의 힘을 빌려 상처를 치유하는 마법이에요. 먼저 주문을 알려 드릴게요.",null,true),
            MetiniPage.Recital=>(string.Join("|",Chant),null,true),
            MetiniPage.Payment=>("마법석을 마련하려면 엘사리아 20개와 2,500갈리드가 필요해요. 건네주시겠어요?",["예","아니오"],false),
            MetiniPage.Hunt=>($"저를 괴롭히는 수탉을 50마리 잡아 주세요.|현재 {character.MetiniRoosters}/50마리",null,false),
            MetiniPage.Ready=>("수탉 50마리를 잡으셨군요. 주문을 외울 준비가 되셨나요?",["예","아니오"],false),
            MetiniPage.Review=>(string.Join("|",Chant),null,true),
            _=>("아라트(리메듐)를 드렸어요. 상처 입은 사람들을 도와주세요.",null,false)
        };
        return GamePackets.NpcPursuitPage(npc,dialog.Text,dialog.Options,(ushort)page,dialog.Next,Script);
    }
}
