using System.Globalization;

namespace ElanciaLocal;

internal sealed class FameExchangeDialog
{
    internal const uint SpeakerId=0x8F46;
    internal const byte InputLimit=9;
    const uint FamePerUnit=100;
    const uint GalridPerUnit=120;
    static readonly string[] YesNo=["예","아니오"];
    enum Page { Welcome, About, Support, Rate, Consent, Units, Input, Invalid, Quote, Complete, Unavailable, Closed }
    Page current=Page.Welcome;
    uint spent,credit;
    internal bool Closed=>current==Page.Closed;
    internal static bool InRange(Character character)=>character.Map%10000==2229&&ShrineDialog.InRange(character);

    internal bool Advance(byte page,byte action,byte choice,string? input,Character character,Func<uint,uint,bool> exchange){
        if(page==1&&action==0){current=Page.Closed;return true;}
        if(page==0&&action==0&&current==Page.Input){current=Page.Units;return true;}
        if(page!=2)return false;
        var next=(current,action,choice) switch {
            (Page.Welcome,1,1)=>Page.About,
            (Page.Welcome,1,2)=>Page.Support,
            (Page.Welcome,1,3) or (Page.Support,1,3)=>Page.Closed,
            (Page.About,0,0)=>Page.Welcome,
            (Page.Support,1,1)=>Page.Rate,
            (Page.Support,1,2)=>Page.Consent,
            (Page.Rate,0,0)=>Page.Support,
            (Page.Consent,1,1)=>Page.Units,
            (Page.Consent or Page.Quote,1,2)=>Page.Closed,
            (Page.Units,0,0)=>Page.Input,
            (Page.Input,2,0)=>Quote(input!,character),
            (Page.Quote,1,1)=>exchange(spent,credit)?Page.Complete:Page.Unavailable,
            (Page.Complete or Page.Invalid or Page.Unavailable,0,0)=>Page.Closed,
            _=>current
        };
        if(next==current)return false;
        current=next;return true;
    }

    Page Quote(string input,Character character){
        if(input.Length is 0 or >InputLimit||input.Any(c=>!char.IsAsciiDigit(c))||
            !uint.TryParse(input,NumberStyles.None,CultureInfo.InvariantCulture,out uint units)||
            units==0||units>uint.MaxValue/GalridPerUnit)
            return Page.Invalid;
        spent=units*FamePerUnit;credit=units*GalridPerUnit;
        return CombatAbilities.Fame(character)>=spent&&character.Gold<=uint.MaxValue-credit?Page.Quote:Page.Unavailable;
    }

    internal byte[] Packet(Character character){
        if(current==Page.Input)
            return GamePackets.PursuitInput(SpeakerId,"쓰려고 하는 [{=fFAME{=a]은?|여기에 {=j1{=a을 쓰게 되면 {=j100{=a FAME 을 의미하게 됩니다",
                "쓰려고 하는 [{=fFAME{=a]의 양은?",InputLimit,"X [100 {=fFAME{=a] 입니다");
        (string Text,string[]? Options,bool Next) page=current switch {
            Page.Welcome=>($"안녕하세요, [{{=f{character.Name}{{=a]님!! 무엇을 도와드릴까요...?",["명성(FAME)이란?","명성보상지원금","종료"],false),
            Page.About=>("명성(FAME)은 사냥 등으로 얻으며, 명성보상지원금으로 바꿀 수 있습니다.",null,true),
            Page.Support=>("명성보상지원금",["지원금비율정보","지원금받기","종료"],false),
            Page.Rate=>($"명성 {FamePerUnit}마다 {GalridPerUnit}갈리드를 받을 수 있습니다.",null,true),
            Page.Consent=>($"현재 당신의 [{{=fFAME{{=a]은 [{{=j{CombatAbilities.Fame(character)}{{=a]입니다. [{{=fFAME{{=a]을 쓰고, 지원금을 받으시겠습니까?",YesNo,false),
            Page.Units=>("얼마만큼의 [{=fFAME{=a]을 쓰시겠습니까? {=j100{=a 단위로만 가능합니다",null,true),
            Page.Invalid=>("잘못 입력하셨습니다!!!",null,true),
            Page.Quote=>($"받으실 돈은 [{{=j{credit}{{=a]갈리드이고, [{{=j{spent}{{=a FAME]을 쓰게 됩니다. 받으시겠습니까?",YesNo,false),
            Page.Complete=>($"[{{=j{spent}{{=a FAME]을 쓰고, [{{=j{credit}{{=a] 갈리드를 얻으셨습니다!!!",null,true),
            Page.Unavailable=>("명성이나 지갑 상태를 확인한 뒤 다시 시도해 주세요.",null,true),
            _=>throw new InvalidOperationException("No open fame exchange page")
        };
        return GamePackets.Pursuit(SpeakerId,page.Text,page.Options,false,page.Next);
    }
}
