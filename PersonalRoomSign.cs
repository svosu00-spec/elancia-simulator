namespace ElanciaLocal;

internal readonly record struct PersonalRoomSign(ushort Sprite,ushort Minutes,bool PaddedConsumption){
    internal static PersonalRoomSign? FromItem(EquipmentItem item)=>(item.Sprite,item.Name) switch {
        (0x8F81,"개인방팻말(Lv1)")=>new(0x40C3,10,true),
        (0x8F82,"개인방팻말(Lv2)")=>new(0x40C4,30,false),
        (0x8F83,"개인방팻말(Lv3)")=>new(0x40C5,60,true),
        (0x8F84,"개인방팻말(Lv4)")=>new(0x40C6,120,true),
        (0x8F85,"개인방팻말(Lv5)")=>new(0x40C7,240,true),
        _=>null
    };
}
