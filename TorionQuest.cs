namespace ElanciaLocal;

public enum TorionStage : byte { None, Slimes, SeekErenoa, Knights, SeekLena, Rats, Epiloria }
internal enum TorionResult { Advanced, Unavailable, InventoryFull }

internal static class TorionQuest {
    internal const ushort Script=0x1A1, ObloMenuPage=1;
    internal const int Required=20;
    internal const string AxeKey="torion-old-axe", BraemaKey="torion-sahtt-braema", LenaRingKey="torion-lena-ring";
    internal const ushort SlimeSprite=0x43EB, KnightSprite=0x47D2, RatSprite=0x43F6;

    internal static bool IsLena(LocalNpc npc)=>npc.Map%10000==212&&npc.Name=="레나"&&npc.Sprite==0x4068;
    internal static bool MeetsMastery(Character c)=>CombatAbilities.All.Concat(CombatAbilities.Optional)
        .Any(ability=>ability!=CombatAbilities.Harp&&CombatAbilities.Mastery(c,ability)>=1000);

    internal static bool IsNpc(LocalNpc npc,TorionStage stage)=>stage switch {
        TorionStage.None or TorionStage.Slimes=>KondQuest.IsOblo(npc),
        TorionStage.SeekErenoa or TorionStage.Knights=>ErenoaQuest.IsNpc(npc),
        TorionStage.SeekLena or TorionStage.Rats or TorionStage.Epiloria=>IsLena(npc),
        _=>false
    };
    internal static (string Name,ushort Sprite,int Map) Target(TorionStage stage)=>stage switch {
        TorionStage.Slimes=>("슬라임디",SlimeSprite,241),
        TorionStage.Knights=>("오크(나이트)",KnightSprite,4),
        TorionStage.Rats=>("작은쥐",RatSprite,232),
        _=>("",0,-1)
    };
    internal static Character RecordDefeat(Character c,FieldObject mob)=>
        c.TorionKills<Required&&mob.Map is >=0 and <30000&&
        Target(c.TorionProgress)==(mob.Name,mob.Sprite,mob.Map%10000)
            ?c with {TorionKills=(byte)(c.TorionKills+1)}:c;

    internal static bool CanAdvance(Character c)=>c.TorionProgress switch {
        TorionStage.None=>c.AptitudeOrder()>>4==0&&MeetsMastery(c),
        TorionStage.SeekErenoa or TorionStage.SeekLena=>true,
        TorionStage.Slimes or TorionStage.Knights or TorionStage.Rats=>c.TorionKills>=Required,
        _=>false
    };
    internal static string? Reward(TorionStage stage)=>stage switch {
        TorionStage.None=>AxeKey,
        TorionStage.Knights=>BraemaKey,
        TorionStage.Rats=>LenaRingKey,
        _=>null
    };

    internal static ushort Page(TorionStage stage)=>(ushort)(10+(byte)stage*2);
    internal static byte[] ObloMenu(LocalNpc npc)=>GamePackets.NpcPursuitPage(npc,"어떤 이야기를 들으러 왔나?",
        ["아버지의 말씀","컨드의 낚싯대","어머니가 남긴 반지"],ObloMenuPage,script:Script);
    internal static byte[] Offer(LocalNpc npc,Character c){
        var stage=c.TorionProgress;
        if(!IsNpc(npc,stage))
            return GamePackets.NpcPursuitPage(npc,stage switch {
                TorionStage.None or TorionStage.Slimes=>"퍼브의 오블로에게 먼저 이야기를 들어 보세요.",
                TorionStage.SeekErenoa or TorionStage.Knights=>"마법상점의 에레노아에게 가 보세요.",
                _=>"무기상점의 레나에게 가 보세요."
            });
        bool advance=CanAdvance(c);
        string text=stage switch {
            TorionStage.None when !advance=>"첫 번째 적성이 전사이고 전사 계열 어빌리티 중 하나가 10.00 이상이면 다시 오게.",
            TorionStage.None=>"자네 아버지는 바스티의 아들을 돌보겠다고 약속했지. 로랜시아에서 그 아이를 잃어버렸다네. 이 낡은액스를 징표로 삼아 찾아주겠나?",
            TorionStage.SeekErenoa=>"내가 돌보던 아이는 아버지를 찾아 떠났어요. 먼저 동쪽 필드의 오크(나이트)를 20마리 잡아 주세요.",
            TorionStage.SeekLena=>"토리온은 에필로리아로 떠났어요. 반지를 맡기기 전에 하수도의 작은쥐 20마리를 잡아 주세요.",
            TorionStage.Epiloria=>"토리온을 만나면 제 안부를 전해 주세요.",
            _=>$"{Target(stage).Name} {c.TorionKills}/{Required}마리. "+(advance?"부탁을 마쳤군요.":"아직 조금 더 잡아 주세요.")
        };
        return GamePackets.NpcPursuitPage(npc,text,advance?["예","아니오"]:null,Page(stage),script:Script);
    }
    internal static byte[] Advanced(LocalNpc npc,TorionStage stage)=>GamePackets.NpcPursuitPage(npc,stage switch {
        TorionStage.Slimes=>"아버지의 징표인 낡은액스를 받았네. 조카를 괴롭히는 슬라임디를 20마리 잡고 돌아오게.",
        TorionStage.SeekErenoa=>"고맙네. 그 아이를 돌봤던 마법상점의 에레노아를 찾아가게.",
        TorionStage.Knights=>"동쪽 필드의 오크(나이트) 20마리를 잡고 돌아와 주세요.",
        TorionStage.SeekLena=>"그 아이는 토리온이에요. 무기상점의 레나와 친했지요. 사라트(브레마)를 드릴게요.",
        TorionStage.Rats=>"하수도의 작은쥐 20마리를 잡고 돌아와 주세요.",
        _=>"레나의링을 드릴게요. 엘 속성 마법에 써 보세요. 토리온은 에필로리아로 갔어요."
    });
}
