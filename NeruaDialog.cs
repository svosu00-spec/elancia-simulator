namespace ElanciaLocal;

internal sealed class NeruaDialog
{
    internal const string CandyKey="nerua-candy";
    internal const ushort CandySprite=0x8138;
    internal const string CandyName="사탕";

    static readonly (string Text,string Choice)[] Pages=[
        ("안녕? 나는 네루아야. 너도 이곳에 자주 오니?", "그냥 한 번 들러 봤어."),
        ("가끔 쓸쓸할 때 여기 와. 오늘은 새로운 사람을 만나서 기분이 좋네. 넌 초콜릿을 좋아하니?", "응, 초콜릿 정말 좋아해."),
        ("나도 초콜릿을 좋아해! 카카오 열매에서 초콜릿을 어떻게 만드는지 이야기해 줄까?", "좋아, 들려줘."),
        ("카카오 씨를 볶아서 과자나 음료를 만들어. 오래전에는 귀해서 신의 음식이라고도 불렀대. 더 들어 볼래?", "그래. 나도 한가해."),
        ("카카오버터 때문에 초콜릿이 입에서 부드럽게 녹는 거야. 발렌타인데이에 초콜릿을 줄 사람이 있으면 좋을 텐데. 내 이야기 어땠어?", "재미있게 들었어.")
    ];

    int index;
    internal static bool IsNpc(LocalNpc npc)=>npc.Map%10000==214&&npc.Sprite==0x4000&&npc.Name=="네루아";
    internal byte[] Packet(LocalNpc npc)=>GamePackets.NpcPursuitPage(npc,Pages[index].Text,
        [Pages[index].Choice,index==Pages.Length-1?"무슨 일 있어?":"다음에 들을게."],page:(ushort)(index+1));

    internal NeruaAction Advance(byte[] request){
        if(request[1]!=1||request[6]!=0||request[7]!=0||request[8]!=0)return NeruaAction.Ignore;
        if(request.Length==11&&request[9]==index+1)return NeruaAction.Close;
        if(request.Length!=12||request[9]!=index+2)return NeruaAction.Ignore;
        if(request[11]==2)return NeruaAction.Farewell;
        if(request[11]!=1)return NeruaAction.Ignore;
        if(index==Pages.Length-1)return NeruaAction.Reward;
        index++;
        return NeruaAction.Next;
    }
}

internal enum NeruaAction { Ignore, Close, Farewell, Next, Reward }
internal enum NeruaReward { AlreadyCarrying, InventoryFull, Granted }
