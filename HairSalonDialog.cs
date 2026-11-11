namespace ElanciaLocal;

internal enum HairServiceResult { Changed, Already, InsufficientGold, SaveFailed }
internal enum HairPart { Style=2, Color=3 }

internal sealed class HairSalonDialog(LocalNpc npc)
{
    internal const ushort Script=0x00C4;
    enum Page : ushort { Welcome=0, Services=12, Styles=21, Punk=92, Retry=103, Reggae=114, Done=122, Colors=159, Dyed=246, Closed }
    static readonly (string Label,uint Price)[] Colors=[
        ("은갈색",1000),("청회색",1000),("자주색",1500),("녹  색",1500),("금  색",2000),
        ("빨간색",2500),("주황색",2500),("파란색",2500),("검정색",2000),("보라색",1500),
        ("분홍색",2000),("갈  색",2500),("청록색",2500),("하얀색",2500)
    ];
    Page current=Page.Welcome;
    Page retryDestination=Page.Styles;
    string resultText="";
    internal bool Closed=>current==Page.Closed;
    internal bool Dyed=>current==Page.Dyed;

    internal static bool IsBarber(LocalNpc npc)=>npc.Map%10000==210&&npc.Name=="미용바니"&&npc.Sprite==0x406C;
    internal static NpcDialogData Open(LocalNpc npc)=>new(13,1,1,npc.Sprite,0,0x01406C00,"",
        X:npc.X,Y:npc.Y,MenuCodes:[0,0,0,0x6F]);

    internal bool Advance(ushort page,byte action,byte choice,Func<HairPart,byte,uint,HairServiceResult> purchase){
        if(page==(ushort)current&&action==0){current=Page.Closed;return true;}
        if(page!=(ushort)current+1)return false;
        Page next=(current,action,choice) switch {
            (Page.Welcome,0,0)=>Page.Services,
            (Page.Services,1,1)=>Page.Styles,
            (Page.Services,1,2)=>Page.Colors,
            (Page.Services,1,3)=>Page.Closed,
            (Page.Styles,1,2)=>Page.Punk,
            (Page.Styles,1,4)=>Page.Reggae,
            (Page.Styles,1,7)=>Page.Services,
            (Page.Styles,1,>=1 and <=6)=>Retry("현재 이 스타일을 선택할 수 없습니다.",Page.Styles),
            (Page.Punk,0,0)=>Finish("펑크",purchase(HairPart.Style,2,30000)),
            (Page.Reggae,0,0)=>Finish("레게",purchase(HairPart.Style,3,100000)),
            (Page.Colors,1,15)=>Page.Services,
            (Page.Colors,1,>=1 and <=14)=>FinishDye(purchase(HairPart.Color,choice,Colors[choice-1].Price)),
            (Page.Retry,0,0)=>retryDestination,
            (Page.Done,0,0)=>Page.Closed,
            _=>current
        };
        if(next==current)return false;
        current=next;return true;
    }

    Page Retry(string text,Page returnTo){
        resultText=text;
        retryDestination=returnTo;
        return Page.Retry;
    }

    Page Finish(string style,HairServiceResult result){
        if(result==HairServiceResult.Changed){
            resultText=$"{style} 스타일로 머리를 해드렸습니다.|괜찮으시지요.?||찾아 주셔서 감사합니다. 또 오세요.";
            return Page.Done;
        }
        string text=result switch {
            HairServiceResult.Already=>$"현재 모양이 {style} 스타일입니다.!|다른 모양을 선택해 주세요.",
            HairServiceResult.InsufficientGold=>"미용에 필요한 갈리드가 부족합니다.",
            _=>"머리 모양을 저장하지 못했습니다."
        };
        return Retry(text,Page.Styles);
    }

    Page FinishDye(HairServiceResult result)=>result switch {
        HairServiceResult.Changed=>Page.Dyed,
        HairServiceResult.Already=>Retry("이미 같은 색입니다. 다른 색을 선택해 주세요.",Page.Colors),
        HairServiceResult.InsufficientGold=>Retry("염색에 필요한 갈리드가 부족합니다.",Page.Colors),
        _=>Retry("머리 색을 저장하지 못했습니다.",Page.Colors)
    };

    internal byte[] Packet(){
        (string Text,string[]? Options) page=current switch {
            Page.Welcome=>("안녕하세요.?|헤어스타일의 마술사 \"{=m미용바니{=a\" 입니다.",null),
            Page.Services=>("어떻게 해드릴까요.?",["스타일 변형","염     색","종     료"]),
            Page.Styles=>("원하시는 모양을 선택 해주세요.",
                ["기  본 25000","펑  크 30000","삭  발 20000","레  게 100000","변  형 30000","식  모 50000","전화면으로.."]),
            Page.Punk=>("와우.! 펑크 스타일은 원하신다고요.? 멋쩌요.!!",null),
            Page.Reggae=>("아주 좋은 선택 입니다.|멋있는 레게모양으로 만들어 드리겠습니다.",null),
            Page.Colors=>("머리를 어떤 색으로 염색해드릴까요.? 염색비용은 색상에 따라 다릅니다.",
                [..Colors.Select(color=>$"{color.Label} {color.Price}"),"전화면으로.."]),
            Page.Dyed=>("염색이 끝났습니다. .. 잘 나왔네요~|찾아 주셔서 감사합니다. 또 오세요.",null),
            Page.Retry or Page.Done=>(resultText,null),
            _=>throw new InvalidOperationException("No open hair salon page")
        };
        var packet=new PacketWriter(0x30).U8(page.Options==null?0:2).U8(1).U32(npc.Id).U8(1).U16(npc.Sprite).U8(0).U32(0)
            .U16(Script).U16((ushort)current).U8(0).U8(Dyed?0:1).Str16(page.Text);
        if(page.Options is {} options){
            packet.U8(options.Length);
            foreach(string option in options)packet.Str8(option);
        }
        return packet.Build();
    }
}
