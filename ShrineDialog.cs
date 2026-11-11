namespace ElanciaLocal;

internal enum ShrinePage {
    Prayer, PrayerWarning, Welcome, JobMenu, JobIntro, JobActions, Jobs, JobDescriptions,
    MartialIntro, MartialBonus, MartialTradeoff, MartialClothes, MartialOffer, MartialCheck,
    MartialSuccess, InsufficientGold, AlreadyCommoner,
    LeaveJob, Commoner, JobInformation, JobOffer, JobCheck, JobSuccess, JobSkillRequired, AlreadyJob,
    FateMenu, FateExplanation, FateChoices, FateAllocated, FateRemaining, FateOrderChanged, FateEmpty,
    GradeMenu, GradeInformation, GradeResetWarning, GradeOffering, GradeKind, GradeInput, GradeConfirm, GradeResult
}

internal static class ShrineDialog {
    static readonly string[] Jobs=[..ShrineJob.All.Select(job=>job.Name),"이전","종료"];
    static readonly string[] Aptitudes=["전사","모험가","상인"];
    internal static uint SpeakerId(int map)=>map%10000==229?0x8EB9u:0x8F13u;

    internal static bool InRange(Character character){
        var altar=(character.Map%10000) switch {
            229=>new StartLocation(character.Map,39,29,0),
            2229=>new StartLocation(character.Map,19,17,1),
            _=>null
        };
        return altar!=null&&character.Floor==altar.Floor&&
            Math.Max(Math.Abs(character.X-altar.X),Math.Abs(character.Y-altar.Y))<=6;
    }

    static string OrderText(byte order)=>string.Join("-",Aptitudes[order>>4],Aptitudes[(order>>2)&3],Aptitudes[order&3]);

    internal static byte[] Packet(ShrinePage page,ShrineJob? job=null,Character? character=null,byte aptitude=0,byte? previousOrder=null,
        ShrineGrade? skill=null,string? result=null){
        if(page==ShrinePage.GradeInput)
            return GamePackets.PursuitInput(SpeakerId(character?.Map??229),"그레이드를 올릴 어빌리티를 알려 주세요.",
                "어빌리티 이름",ShrineGrade.InputLimit,"");
        string Offering(ShrineJob target)=>character!=null&&target.Fee(character)==0?
            "잡포인트를 마스터하여 제물 없이 전직할 수 있습니다.":
            $"전직에는 {{=g{ShrineJob.Offering}G{{=a의 제물이 필요합니다.";
        (string Text,string[]? Options,bool Back,bool Next) dialog=page switch {
            ShrinePage.Prayer=>("당신의 성향(Tend)는 {=f0{=a 입니다.|성향은 사람을 죽이거나 나쁜일을 하면 떨어집니다",null,false,true),
            ShrinePage.PrayerWarning=>("성향이 떨어지면, 여러가지 불이익이 있을 수 있으니 조심하세요... ",null,true,true),
            ShrinePage.Welcome=>("빛과 질서의 여신 {=m엘(El){=a의 신전에 오신 것을 환영합니다.|무엇을 도와드릴까요?",["기도","직업","그레이드","성향포인트","종료"],true,false),
            ShrinePage.JobMenu=>("엘(El)은 빛과, 질서 형태를 가진 것을 관장하는 여신입니다.||그대는 무엇이 필요해서 이곳을 찾아 왔습니까.?",["직업","무직","이전","종료"],false,false),
            ShrinePage.JobIntro=>("케린어(고대어)로 직업은 'Gunn'입니다.|당신은 직업을 선택 하시기 전에 적성을 알아 보신뒤에 적성을 알아 보신뒤에 적성 맞는 길을 택하는것이 좋습니다.",null,false,true),
            ShrinePage.JobActions=>("적성에 맞지 않는 직업의 경우 남보다 더 노력해야 합니다.",["직업 가지기","직업 설명","이전 메뉴","종료"],false,false),
            ShrinePage.Jobs=>("이곳에서 얻을 수 있는 직업은 아래와 같습니다. 어떤 직업을 원하시나요.?",Jobs,false,false),
            ShrinePage.JobDescriptions=>("어떤 직업에 대해 알고 싶으신가요?",Jobs,false,false),
            ShrinePage.MartialIntro=>("무도가는 튼튼한 육체의 수련을 목표로 하는, 격투와 봉술의 전문가입니다. ",null,false,true),
            ShrinePage.MartialBonus=>("격투, 봉술의 [{=bMASTER{=a]가 가능하며, 일정 직업 레벨마다 [{=bVIT{=a] 보너스를 받습니다.",null,false,true),
            ShrinePage.MartialTradeoff=>("그리고, [{=bINT, STR{=a]가 조금씩 떨어집니다.",null,false,true),
            ShrinePage.MartialClothes=>("세르니카 마을 [편집국]의 [{=m터크{=a]에게 가시면 [{=f견습옷(무도가){=a]를 구입하실 수 있습니다.",null,false,true),
            ShrinePage.MartialOffer=>(character!=null&&ShrineJob.All[0].Fee(character)==0?
                $"{Offering(ShrineJob.All[0])}|{{=m무도가{{=a로 전직 하시겠습니까.?":
                "무도가로 전직 하시려면, {=g5000G{=a의 제물이 필요 합니다.|{=m무도가{=a로 전직 하시겠습니까.?",["예","아니오"],false,false),
            ShrinePage.MartialCheck=>("무도가의 소질이 있으신지 살펴보겠습니다|잠시만 기다려주세요 ...",null,false,true),
            ShrinePage.MartialSuccess=>("빛과 질서의 여신의 축복으로, 무도가가 되셨습니다.",null,true,true),
            ShrinePage.InsufficientGold=>("전직에 필요한 5000 갈리드가 부족합니다.",null,false,true),
            ShrinePage.AlreadyCommoner=>("당신은 이미 무직입니다.",null,false,true),
            ShrinePage.LeaveJob=>("현재 직업을 내려놓으시겠습니까?",["예","아니오"],false,false),
            ShrinePage.Commoner=>("평민이 되셨습니다.",null,false,true),
            ShrinePage.JobInformation=>($"{job!.Name} 전직 조건은 {job.Requirement}입니다.|{job.Progress(character!)}",null,false,true),
            ShrinePage.JobOffer=>($"{{=m{job!.Name}{{=a 전직 조건은 {job.Requirement}입니다.|{Offering(job)}|전직하시겠습니까?",["예","아니오"],false,false),
            ShrinePage.JobCheck=>($"{job!.Name}의 소질이 있으신지 살펴보겠습니다.|잠시만 기다려주세요 ...",null,false,true),
            ShrinePage.JobSuccess=>($"빛과 질서의 여신의 축복으로, {job!.Name} 직업을 얻었습니다.",null,true,true),
            ShrinePage.JobSkillRequired=>($"{job!.Name} 전직 조건은 {job.Requirement}입니다.",null,false,true),
            ShrinePage.AlreadyJob=>($"현재 직업은 이미 {job!.Name}입니다.",null,false,true),
            ShrinePage.FateMenu=>("무엇을 도와드릴까요?",["성향포인트 설명","성향포인트 올리기","종료"],false,false),
            ShrinePage.FateExplanation=>("레벨이 오르면 성향포인트를 얻습니다.|전사, 모험가, 상인 중 한 성향에 배분할 수 있습니다.",null,false,true),
            ShrinePage.FateChoices=>("어떤 성향포인트를 올려드릴까요?",[..Aptitudes,"종료"],false,false),
            ShrinePage.FateAllocated=>($"{Aptitudes[aptitude]}적 성향 포인트가 {{=f{character!.FatePoints!.For(aptitude)-1}{{=a에서 {{=f{character.FatePoints.For(aptitude)}{{=a(으)로 올랐습니다.",null,false,true),
            ShrinePage.FateRemaining=>($"미사용 성향 포인트는 {{=f{character!.AvailableFatePoints()}{{=a입니다.",null,true,true),
            ShrinePage.FateOrderChanged=>($"당신의 첫번째, 두번째, 세번째 성향이 {{=f{OrderText(previousOrder!.Value)}{{=a에서 {{=f{OrderText(character!.AptitudeOrder())}{{=a(으)로 바뀌었습니다.",null,true,true),
            ShrinePage.FateEmpty=>("사용할 수 있는 성향포인트가 없습니다.",null,false,true),
            ShrinePage.GradeMenu=>("그레이드에 대해 무엇을 도와드릴까요?",["그레이드 설명","그레이드 올리기","종료"],false,false),
            ShrinePage.GradeInformation=>("100.00에 도달한 어빌리티는 그레이드를 올릴 수 있습니다.",null,false,true),
            ShrinePage.GradeResetWarning=>("그레이드를 올리면 선택한 어빌리티의 숙련도가 0.00으로 돌아갑니다.",null,false,true),
            ShrinePage.GradeOffering=>("100만 갈리드와 정령의보석 10개가 필요합니다.|그레이드를 올리시겠습니까?",["예","아니오"],false,false),
            ShrinePage.GradeKind=>("어떤 종류의 그레이드를 올리시겠습니까?",["어빌리티","스펠","종료"],false,false),
            ShrinePage.GradeConfirm=>($"{skill!.Name}의 그레이드를 올리시겠습니까?|100만 갈리드와 정령의보석 10개를 바칩니다.",["예","아니오"],false,false),
            ShrinePage.GradeResult=>(result!,null,false,true),
            _=>throw new ArgumentOutOfRangeException(nameof(page))
        };
        return GamePackets.Pursuit(SpeakerId(character?.Map??229),dialog.Text,dialog.Options,dialog.Back,dialog.Next);
    }

    internal static ShrinePage? Next(ShrinePage page,byte action,byte choice,bool employed)=> (page,action,choice) switch {
        (ShrinePage.Prayer,0,0)=>ShrinePage.PrayerWarning,
        (ShrinePage.PrayerWarning,0,0)=>ShrinePage.Welcome,
        (ShrinePage.Welcome,1,1)=>ShrinePage.Prayer,
        (ShrinePage.Welcome,1,2)=>ShrinePage.JobMenu,
        (ShrinePage.Welcome,1,3)=>ShrinePage.GradeMenu,
        (ShrinePage.Welcome,1,4)=>ShrinePage.FateMenu,
        (ShrinePage.GradeMenu,1,1)=>ShrinePage.GradeInformation,
        (ShrinePage.GradeMenu,1,2)=>ShrinePage.GradeResetWarning,
        (ShrinePage.GradeInformation or ShrinePage.GradeResult,0,0)=>ShrinePage.GradeMenu,
        (ShrinePage.GradeResetWarning,0,0)=>ShrinePage.GradeOffering,
        (ShrinePage.GradeOffering,1,1)=>ShrinePage.GradeKind,
        (ShrinePage.GradeOffering or ShrinePage.GradeConfirm,1,2)=>ShrinePage.GradeMenu,
        (ShrinePage.GradeKind,1,1)=>ShrinePage.GradeInput,
        (ShrinePage.FateMenu,1,1)=>ShrinePage.FateExplanation,
        (ShrinePage.FateMenu,1,2)=>ShrinePage.FateChoices,
        (ShrinePage.FateExplanation or ShrinePage.FateRemaining or ShrinePage.FateEmpty,0,0)=>ShrinePage.FateMenu,
        (ShrinePage.JobMenu,1,1)=>ShrinePage.JobIntro,
        (ShrinePage.JobMenu,1,2)=>employed?ShrinePage.LeaveJob:ShrinePage.AlreadyCommoner,
        (ShrinePage.JobMenu,1,3)=>ShrinePage.Welcome,
        (ShrinePage.JobIntro,0,0)=>ShrinePage.JobActions,
        (ShrinePage.JobActions,1,1)=>ShrinePage.Jobs,
        (ShrinePage.JobActions,1,2)=>ShrinePage.JobDescriptions,
        (ShrinePage.JobActions,1,3)=>ShrinePage.JobMenu,
        (ShrinePage.Jobs,1,1)=>ShrinePage.MartialIntro,
        (ShrinePage.Jobs,1,>=2 and <=10)=>ShrinePage.JobOffer,
        (ShrinePage.JobDescriptions,1,>=1 and <=10)=>ShrinePage.JobInformation,
        (ShrinePage.Jobs or ShrinePage.JobDescriptions,1,11)=>ShrinePage.JobMenu,
        (ShrinePage.JobInformation,0,0)=>ShrinePage.JobActions,
        (ShrinePage.MartialIntro,0,0)=>ShrinePage.MartialBonus,
        (ShrinePage.MartialBonus,0,0)=>ShrinePage.MartialTradeoff,
        (ShrinePage.MartialTradeoff,0,0)=>ShrinePage.MartialClothes,
        (ShrinePage.MartialClothes,0,0)=>ShrinePage.MartialOffer,
        (ShrinePage.MartialOffer,1,1)=>ShrinePage.MartialCheck,
        (ShrinePage.MartialOffer,1,2)=>ShrinePage.Jobs,
        (ShrinePage.JobOffer,1,1)=>ShrinePage.JobCheck,
        (ShrinePage.JobOffer,1,2)=>ShrinePage.Jobs,
        (ShrinePage.LeaveJob,1,2)=>ShrinePage.JobMenu,
        _=>null
    };

}
