namespace ElanciaLocal;

internal enum GuildRegistration { Created, MissingMembers, AlreadyMember, NameTaken, InsufficientGold, SaveFailed, Waiting, Declined, Expired, Busy }

internal sealed record GuildFoundingRequest(ushort Script,LocalNpc Npc,GuildFoundingDialog Form,
    WorldSession Founder,WorldSession First,WorldSession Second,string Name,long Due){
    internal WorldSession[] Participants=>[Founder,First,Second];
    internal HashSet<WorldSession> Accepted {get;}=[];
    internal byte[] Invitation()=>GamePackets.NpcPursuitPage(Npc,
        $"[{Founder.Character.Name}]님과 [{Name}] 길드를 함께 창립하시겠습니까?",["예","아니오"],script:Script);
}

internal sealed class GuildFoundingDialog(uint npcId)
{
    internal const string FoundingIntro="390104BE/3A01000000020102";
    internal const string FirstInputPath=FoundingIntro+"/3A010000000200/3A010000000200";
    internal const string DepartureConfirmation="390104BD/3A01000000020108/3A01000000020101/3A010000000200/3A010000000200";
    internal const string DepartureAccept=DepartureConfirmation+"/3A01000000020101";
    internal const string WaitingMessage="두 사람에게 길드 창립 의사를 물어볼 테니 잠시 기다리게.";
    const byte InputLimit=14;
    enum Page { FirstMember, SecondMember, ConfirmMembers, ExplainName, EnterName, ConfirmName, InvalidName, Duplicate, Result }
    Page current=Page.FirstMember;
    string first="",second="",guild="";
    bool invalidMember;
    bool retryName;
    GuildRegistration result;
    internal static bool IsGuildNpc(LocalNpc npc)=>npc.Map%10000==2&&npc.Name=="길드만들기";
    internal uint NpcId=>npcId;
    internal bool AtFirstMember=>current==Page.FirstMember;
    internal void Finish(GuildRegistration outcome){
        result=outcome;
        current=outcome==GuildRegistration.NameTaken?Page.InvalidName:Page.Result;
    }

    internal bool Advance(ushort page,byte action,byte choice,string? input,string founder,Func<string,string,string,GuildRegistration> register){
        if(page==0&&action==0){
            if(current==Page.EnterName)current=Page.ExplainName;
            else if(current==Page.ExplainName&&retryName)current=Page.InvalidName;
            else return false;
            return true;
        }
        if(page!=2)return false;
        input=input?.Trim();
        switch(current,action,choice){
            case (Page.FirstMember,2,_):
                invalidMember=input==null||!CharacterStore.ValidName(input);
                if(!invalidMember){first=input!;current=Page.SecondMember;}
                return true;
            case (Page.SecondMember,2,_):
                invalidMember=input==null||!CharacterStore.ValidName(input);
                if(invalidMember)return true;
                second=input!;
                current=string.Equals(first,second,StringComparison.OrdinalIgnoreCase)||
                    string.Equals(first,founder,StringComparison.OrdinalIgnoreCase)||
                    string.Equals(second,founder,StringComparison.OrdinalIgnoreCase)?Page.Duplicate:Page.ConfirmMembers;
                return true;
            case (Page.ConfirmMembers,1,1):retryName=false;current=Page.ExplainName;return true;
            case (Page.ConfirmMembers,1,2):current=Page.FirstMember;return true;
            case (Page.ExplainName,0,_):current=Page.EnterName;return true;
            case (Page.EnterName,2,_) when input!=null:
                guild=input;
                current=CharacterStore.ValidName(input)&&input.Length<=8?Page.ConfirmName:Page.InvalidName;
                return true;
            case (Page.ConfirmName,1,1):
                Finish(register(first,second,guild));
                return true;
            case (Page.ConfirmName,1,2):current=Page.EnterName;return true;
            case (Page.InvalidName,0,_):retryName=true;current=Page.ExplainName;return true;
            case (Page.Result,0,_) when result is GuildRegistration.MissingMembers or GuildRegistration.AlreadyMember:
                first=second=guild="";
                current=Page.FirstMember;
                return true;
            default:return false;
        }
    }

    string ResultText()=>result switch {
        GuildRegistration.Created=>$"[{{=f{guild}{{=a] 길드가 창립되었습니다!",
        GuildRegistration.MissingMembers=>"자네까지 세명이 모두 여기 있어야 한다네...!!",
        GuildRegistration.AlreadyMember=>"자네와 같이 길드를 만드려는 사람 중에 이미 길드에 가입한 사람이 있네!!!",
        GuildRegistration.InsufficientGold=>$"길드 창립 기부금 {WorldSettings.GuildFoundingCost} 갈리드가 부족합니다.",
        GuildRegistration.Waiting=>WaitingMessage,
        GuildRegistration.Declined=>"함께 길드를 창립하기로 한 사람이 거절했네.",
        GuildRegistration.Expired=>"두 사람의 동의를 제시간에 받지 못했네.",
        GuildRegistration.Busy=>"이미 길드 창립 동의를 기다리고 있는 사람이 있네.",
        _=>"길드 등록을 저장하지 못했습니다."
    };

    internal byte[] Packet(LocalNpc npc){
        var (mode,text,back,next,prompt)=current switch {
            Page.FirstMember=>(4,"첫번째 길드원의 이름은 무엇인가?",true,true,"첫번째길드원의 이름은"),
            Page.SecondMember=>(4,"두번째 길드원의 이름은 무엇인가?",false,true,"두번째길드원의 이름은"),
            Page.ConfirmMembers=>(2,$"지금까지 입력한 이름이 [{{=f{first}{{=a], [{{=f{second}{{=a] 가(이) 맞는가?",false,false,null),
            Page.ExplainName=>(0,"그럼 다음에는 길드의 이름을 정해야지... 길드의 이름은 한글로 8글자까지 가능하다네.. 더 길게 지으면, 이상한 이름으로 나올수도 있다네..",retryName,true,null),
            Page.EnterName=>(4,"자네가 만들고자 하는 길드의 이름은 무엇인가?",true,true,"만들고자 하는 길드의 이름은"),
            Page.ConfirmName=>(2,$"자네가 만들고자 하는 길드의 이름이 [{{=f{guild}{{=a] 이 맞는가?",false,false,null),
            Page.InvalidName=>(0,"그 이름의 길드는 만들 수가 없다네.. 다른 이름으로 다시 정해보게...",false,true,null),
            Page.Duplicate=>(0,"두사람의 이름이 같지 않은가!!!",false,false,null),
            _=>(0,ResultText(),false,result is GuildRegistration.MissingMembers or GuildRegistration.AlreadyMember,null)
        };
        if(invalidMember&&current is Page.FirstMember or Page.SecondMember)
            text="캐릭터 이름을 확인하고 다시 입력해 주세요. "+text;
        var packet=PageHeader(npc,mode,text,back,next);
        if(mode==2)packet.U8(2).Str8("예").Str8("아니오");
        if(mode==4)packet.Str8(prompt!).U8(InputLimit).Str8("입니다");
        return packet.Build();
    }
    internal static byte[] Notice(LocalNpc npc,string text,bool next=false)=>PageHeader(npc,0,text,false,next).Build();
    static PacketWriter PageHeader(LocalNpc npc,int mode,string text,bool back,bool next)=>
        new PacketWriter(0x30).U8(mode).U8(1).U32(npc.Id).U8(1).U16(npc.Sprite).U8(0).U32(0)
            .U16(0).U16(1).U8(back?1:0).U8(next?1:0).Str16(text);
}
