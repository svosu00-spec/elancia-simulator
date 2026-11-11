namespace ElanciaLocal;

internal static class TiponeMineTravel{
    internal const string MenuPath="390104DA/3A01000000020102";
    internal const string OfferPath=MenuPath+"/3A01000000020101";
    internal const string AcceptPath=OfferPath+"/3A01000000020101";
    internal const uint Fee=50;

    internal static bool IsTipone(LocalNpc npc)=>npc.Map%10000==210&&npc.Name=="티포네"&&npc.Sprite==0x407B;
    internal static StartLocation Arrival(int originMap)=>new(originMap/10000*10000+8,33,122,1);
}
