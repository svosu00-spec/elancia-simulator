namespace ElanciaLocal;

public enum RanicheStage : byte { None, Water, Omelets, Pies, SeekRex, Cake, ShowStaff, Supplies, Epiloria }
internal enum RanicheResult { Advanced, Unavailable, InventoryFull }

internal static class RanicheQuest {
    internal const ushort Script=0x1A2;
    internal const string WaterKey="recorded-8cdd-b1fab2fdc7d1b9b0", RingKey="raniche-wedding-ring";
    internal const string StaffKey="raniche-oblo-staff", BrelKey="raniche-sahtt-brel";
    internal const uint TravelOffering=100_000;
    internal const int CourtMap=270;
    internal const ushort RexSprite=0x401E;
    internal static ushort Page(RanicheStage stage)=>(ushort)(10+(byte)stage*2);
    internal static bool IsRex(LocalNpc npc)=>npc.Map%10000==CourtMap&&npc.Name=="렉스"&&npc.Sprite==RexSprite;
    internal static bool CanSpeak(LocalNpc npc,RanicheStage stage)=>stage switch {
        RanicheStage.SeekRex or RanicheStage.ShowStaff or RanicheStage.Supplies=>IsRex(npc),
        RanicheStage.Epiloria=>false,
        _=>KondQuest.IsOblo(npc)
    };

    internal static CraftingMaterial? Required(RanicheStage stage)=>stage switch {
        RanicheStage.Water=>new("깨끗한물",0x8CDD,30),
        RanicheStage.Omelets=>new("오믈렛",CookingRecipe.OmeletSprite,30),
        RanicheStage.Pies=>new("초컬릿 파이",CookingRecipe.PieSprite,30),
        RanicheStage.Cake=>new("딸기 케익",CookingRecipe.CakeSprite,30),
        _=>null
    };
    internal static string? RewardKey(RanicheStage stage)=>stage switch {
        RanicheStage.Pies=>RingKey,
        RanicheStage.Cake=>StaffKey,
        RanicheStage.Supplies=>BrelKey,
        _=>null
    };
    static bool HasProof(Character c,string key)=>(c.Items??[]).Any(i=>i.Key==key&&i.Quantity>0);
    internal static bool CanAdvance(Character c)=>c.RanicheProgress switch {
        RanicheStage.None=>c.AptitudeOrder()>>4==2&&CookingRecipe.Mastery(c)>=1000,
        RanicheStage.SeekRex=>CookingRecipe.Mastery(c)>=1500&&HasProof(c,RingKey),
        RanicheStage.ShowStaff=>HasProof(c,StaffKey),
        RanicheStage.Supplies=>c.Gold>=TravelOffering,
        RanicheStage.Epiloria=>false,
        _=>Required(c.RanicheProgress)?.IsAvailable(c)==true
    };
    internal static byte[] Offer(LocalNpc npc,Character c){
        var stage=c.RanicheProgress;
        bool correctNpc=CanSpeak(npc,stage);
        bool advance=correctNpc&&CanAdvance(c);
        string text=!correctNpc?stage switch {
            RanicheStage.Epiloria=>"라니체의 마지막 소식은 에필로리아에서 들려왔네.",
            RanicheStage.SeekRex or RanicheStage.ShowStaff or RanicheStage.Supplies=>"로랜시아 성의 렉스를 찾아가 보게.",
            _=>"퍼브의 오블로에게 먼저 이야기를 들어 보게."
        }:stage switch {
            RanicheStage.None when !advance=>"첫 번째 적성이 상인이고 요리 어빌리티가 10.00 이상이면 라니체의 이야기를 들려주겠네.",
            RanicheStage.None=>"라니체를 닮은 요리 솜씨로군. 어머니 이야기를 듣겠나?",
            RanicheStage.SeekRex when CookingRecipe.Mastery(c)<1500=>"라니체를 닮았다면 요리 어빌리티가 15.00은 되어야 할 걸세.",
            RanicheStage.SeekRex when !advance=>"오블로가 준 라니체의반지를 가지고 돌아오게.",
            RanicheStage.SeekRex=>"그 반지는 라니체와 내가 혼인할 때 쓰던 것이네. 오블로에게 받았다고 했나?",
            RanicheStage.ShowStaff when !advance=>"오블로가 보낸 지팡이를 가져오게.",
            RanicheStage.ShowStaff=>"오블로의 지팡이가 맞군. 자네가 정말 라니체의 자식이었구나.",
            RanicheStage.Supplies=>$"에필로리아로 떠날 준비에는 {TravelOffering:N0}갈리드가 필요하네. 지금은 {c.Gold:N0}갈리드를 가지고 있군.",
            _=>Required(stage) is {} material
                ?$"{material.Name} {NpcMaterials.Count(c,material.Matches)}/{material.Quantity}개. "+
                    (advance?"가져온 재료를 내게 주겠나?":$"{material.Quantity}개를 모아서 다시 와 주게.")
                :""
        };
        return GamePackets.NpcPursuitPage(npc,text,advance?["예","아니오"]:null,Page(stage),script:Script);
    }
    internal static byte[] Advanced(LocalNpc npc,RanicheStage stage)=>GamePackets.NpcPursuitPage(npc,stage switch {
        RanicheStage.Water=>"이야기를 하기 전에 목이 마르군. 돼지에게서 깨끗한물 30개를 구해다 주겠나?",
        RanicheStage.Omelets=>"라니체는 성 앞에서 마법을 수련하다가 당시 왕자였던 렉스를 만났네. 다음 이야기는 오믈렛 30개를 만들어 오면 들려주겠네.",
        RanicheStage.Pies=>"두 사람은 비밀리에 혼인했지만, 라니체는 아이와 함께 성을 떠나야 했네. 초컬릿 파이 30개를 만들어 오면 맡겨 둔 반지를 주겠네.",
        RanicheStage.SeekRex=>"라니체의반지를 받았네. 이 반지는 두 사람이 혼인할 때 끼던 것이야. 로랜시아 성의 렉스에게 가서 라니체의 이름을 말해 보게.",
        RanicheStage.Cake=>"오블로에게 가서 반지를 자네에게 맡겼다는 증거를 얻어 오게.",
        RanicheStage.ShowStaff=>"딸기 케익 잘 먹었네. 렉스가 알아볼 수 있게 내 지팡이를 가져가게.",
        RanicheStage.Supplies=>"내가 자네 아버지일세. 마지막 소식이 있던 에필로리아에서 어머니를 찾아보게. 100,000갈리드를 준비해 오면 돕겠네.",
        _=>"준비가 되었군. 이 사라트(브렐)를 챙기게. 라니체의 소식은 에필로리아에서 이어질 걸세."
    });
}
