namespace ElanciaLocal;

internal static class MercenaryHiring {
    internal const uint Price=10000;
    internal const string LeonDeed="leon-hiring-deed";
    internal const string SummonScroll="mercenary-summon-scroll";
    internal const ushort SummonScrollSprite=0x8574;
    internal const int EpiloriaPubMap=1214;
    internal const string CopeName="코페";
    internal static readonly ShopEntry SummonScrollOffer=new(SummonScroll,500,0);
    internal static LocalNpc PubMerchant(int map)=>new(0x64010000U+(uint)map,CopeName,map,13,25,0,
        0x402B,11,2,CopeName,[SummonScrollOffer]);
    internal const int FollowIntervalMs=1300;
    internal const int ControlRefreshIntervalMs=120000;
    internal const int FollowDistance=4;
    internal const int RasArrivalDelayMs=2800;
    internal const byte ChaseCommandSlot=1,AttackCommandSlot=2;
    internal static uint LeonId(uint ownerId)=>0x65000000U+ownerId;
    internal static Character LeonAt(Character owner,byte direction)=>new(LeonId(owner.Id),"",$"{owner.Name}의레온",
        "400100004001",owner.Map,owner.X,owner.Y,direction,owner.Floor,DisplayAppearance:"400100004001");
    internal static byte[] BindLeon(uint id)=>new PacketWriter(0x4F).U8(2).U8(0).U32(id).U8(0).Build();
    internal static byte[] ShowControls(bool padded=false)=>padded?[0x4F,2,2,0,0,0]:[0x4F,2,2,0,0];
    static PacketWriter Attributes(PacketWriter packet)=>packet.U8(5).U8(5).U8(5).U8(5).U8(0).U16(75);
    static PacketWriter Vitals(PacketWriter packet)=>packet.U32(100).U32(20).U32(0);
    internal static byte[] InitialControls(){
        var packet=new PacketWriter(0x4F).U8(2).U8(2).U8(0);
        Attributes(packet.U8(1));
        Vitals(packet.U8(2));
        Vitals(packet.U8(3));
        return packet.U8(0).U8(0).Build();
    }
    internal static byte[] Profile(Character leon,byte commandSlot){
        var packet=new PacketWriter(0x4F).U8(2).U8(1).Str8(leon.Name);
        Attributes(packet);
        Vitals(packet);
        Vitals(packet);
        // The fourth list contains the stock chase and attack commands.
        return packet.Bytes([0,0,0,2]).U8(ChaseCommandSlot).Str8("체이스1").U16(54)
            .U8(AttackCommandSlot).Str8("공격1").U16(60).U8(0).U8(0).U8(commandSlot).U8(0).Build();
    }
    internal static byte[] CommandSelection(byte slot)=>[0x4F,2,11,0,slot,0];
    internal static byte[] CloseControls(uint id)=>new PacketWriter(0x4F).U8(2).U8(12).U32(id).U8(0).Build();
    internal static ushort InventoryWeight(ItemPlacement[] items)=>EquipmentItems.WeightWord(EquipmentItems.CarriedWeightTotal(items));
    internal static byte[] Inventory(Character owner){
        var items=owner.LeonItems??[];
        ushort weight=InventoryWeight(items);
        var packet=new PacketWriter(0x61).U8(3).U8(0).U8(items.Length);
        foreach(var item in items.OrderBy(item=>item.InventorySlot))
            packet.Bytes(GamePackets.InventoryAdd(item,weight).AsSpan(1));
        return packet.U8(0).Build();
    }
    internal static byte[] InventoryAdd(ItemPlacement item,ushort weight)=>[0x61,3,1,..GamePackets.InventoryAdd(item,weight).AsSpan(1),0];
    internal static byte[] InventoryRemove(byte slot,ushort weight)=>new PacketWriter(0x61).U8(3).U8(2)
        .U8(slot).U8(0x0C).U16(weight).U8(0).Build();
    const string Next="/3A010000000200";
    const string Yes="/3A01000000020101";
    const string No="/3A01000000020102";
    internal const string Offer="3901006F"+Next+Next+No+Next+Next+Next+Yes+Next;
    internal const string Confirmed=Offer+Yes;
    internal const string Declined=Offer+No;
    internal const string Dismissal=Confirmed+Next;
    internal const string Farewell=Dismissal+Next;

    internal static bool IsGuard(LocalNpc npc)=>npc.Map%10000==1209&&npc.Sprite==0x4036&&npc.Name=="은행가드";
    internal static bool IsReceiptPage(string? path)=>path is Confirmed or Dismissal or Farewell;

    internal static byte[]? ReceiptPage(LocalNpc npc,string path)=>path switch {
        Confirmed=>GamePackets.NpcPursuit(npc,Confirmed),
        Dismissal=>GamePackets.NpcPursuitPage(npc,"이 녹색몸의 종족은 내 말만 잘 듣거든...",next:true,back:true),
        Farewell=>GamePackets.NpcPursuitPage(npc,"내가 준 고용서를 더블클릭해봐.. 그럼 그들이 나타날테니.. 그럼 안녕...",next:true,back:true),
        _=>null
    };
}
