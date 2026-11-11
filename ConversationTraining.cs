namespace ElanciaLocal;

internal static class ConversationTraining{
    internal const string Name="대화",StartPath="3901006F/3A0100DC00310101/3A0100DC00A70104";
    internal const byte Slot=16,InputLimit=24;
    internal const ushort Icon=26,Reference=6,FirstPage=0x230;
    internal const int UseIntervalMs=1200;
    internal const byte RequiredJobPoints=20;
    internal static readonly TimeSpan RetryDelay=TimeSpan.FromHours(1);
    internal static readonly string[] Goods=["무로","델린","라마트","파시리온","브리셸","미리온","카잘리온","순수갈리드"];
    static readonly (string Question,string Answer)[] Written=[
        ("빛을 다스리는 여신의 이름을 말해 보게.","엘"),
        ("로랜시아 성을 다스리는 성주는 누구인가?","렉스"),
        ("이 마을에서 보석을 파는 상인의 이름은?","루비"),
        ("로랜시아의 첫 길드 마스터를 알고 있는가?","슈퍼킹"),
        ("그 길드 마스터가 이끌었던 길드의 이름은?","퓨처길드"),
        ("인간을 사랑했던 비극의 주인공은 누구인가?","오크왕자"),
        ("이제 내 이름을 말해 보겠나?","그레파"),
        ("내가 늙어 보이는가?","아닙니다.")
    ];
    internal const int Consent=1,FirstQuestion=2,GoodsQuestion=9,Personal=10,FinalQuestion=11;

    internal static bool Eligible(Character character)=>character.AptitudeOrder()>>4==1&&
        (character.JobPoints?.GetValueOrDefault("모험가")??0)>=RequiredJobPoints;
    internal static ushort CounterReference(byte rank)=>ShrineGrade.GradedReference(Reference,rank);
    internal static ushort Mastery(uint points,byte rank=0)=>(ushort)Math.Min(10_000u,points/CounterReference(rank));
    internal static ushort Page(int stage)=>(ushort)(FirstPage+stage);
    internal static bool IsInput(int stage)=>stage is >=FirstQuestion and <GoodsQuestion or FinalQuestion;
    internal static bool Correct(int stage,string answer)=>
        string.Equals(Written[stage==FinalQuestion?^1:stage-FirstQuestion].Answer,answer.Trim(),StringComparison.Ordinal);

    internal static byte[] ExamPage(LocalNpc teacher,int stage)=>stage switch{
        0=>GamePackets.NpcPursuitPage(teacher,"여러 곳을 여행하는 모험가라면 사람들과 말을 나누는 법도 알아야 하네.",
            page:Page(stage),next:true,script:MeditationTraining.Script),
        Consent=>GamePackets.NpcPursuitPage(teacher,"나와 이야기를 나눠 보겠는가?",["예","아니오"],
            page:Page(stage),script:MeditationTraining.Script),
        GoodsQuestion=>GamePackets.NpcPursuitPage(teacher,"다음 중 가장 값비싼 물건은 무엇인가?",Goods,
            page:Page(stage),script:MeditationTraining.Script),
        Personal=>GamePackets.NpcPursuitPage(teacher,"이제 마지막으로 개인적인 질문을 하나 하지.",
            page:Page(stage),next:true,script:MeditationTraining.Script),
        _=>GamePackets.NpcPursuitInput(teacher,Written[stage==FinalQuestion?^1:stage-FirstQuestion].Question,
            Page(stage),"",InputLimit,script:MeditationTraining.Script)
    };
}
