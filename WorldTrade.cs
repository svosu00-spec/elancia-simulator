namespace ElanciaLocal;

// All exchange events run under the existing world update lock.
public sealed class WorldTrade {
    sealed class Participant(WorldSession session){
        public WorldSession Session {get;}=session;
        public List<(ItemPlacement Source,uint Quantity)> Offers {get;}=[];
        public bool Confirmed {get;set;}
    }
    readonly Participant first,second;
    const int OfferLimit=8;
    internal const string TooFar="교환 상대가 너무 멀리 있습니다.";

    WorldTrade(WorldSession initiator,WorldSession target){
        first=new(initiator);
        second=new(target);
        initiator.Trade=target.Trade=this;
    }
    static bool InRange(Character a,Character b)=>a.Map==b.Map&&a.Floor==b.Floor&&
        Math.Max(Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y))<=2;

    public static async Task Open(WorldSession initiator,WorldSession? target){
        if(target==null){
            await WorldHub.Notify(initiator,GamePackets.Message("교환 상대가 접속 중이 아닙니다."));
            return;
        }
        string? reason=null;
        if(initiator==target)reason="자신과는 교환할 수 없습니다.";
        else if(!InRange(initiator.Character,target.Character))reason=TooFar;
        else if(initiator.Trade!=null)reason="이미 교환 중입니다.";
        else if(target.Trade!=null)reason="상대가 이미 교환 중입니다.";
        else if(initiator.CraftStation!=null)reason="제작 창을 닫은 뒤 교환해 주세요.";
        else if(target.CraftStation!=null)reason="상대가 제작 중입니다.";
        if(reason!=null){
            await WorldHub.Notify(initiator,GamePackets.Message(reason));
            return;
        }
        _=new WorldTrade(initiator,target);
        await WorldHub.Notify(initiator,GamePackets.ExchangeOpen(target.Character));
        await WorldHub.Notify(target,GamePackets.ExchangeOpen(initiator.Character));
    }

    void Detach(){
        first.Session.Trade=null;
        second.Session.Trade=null;
    }
    public async Task Close(string text){
        Detach();
        var packet=GamePackets.ExchangeClose(text);
        await WorldHub.Notify(first.Session,packet);
        await WorldHub.Notify(second.Session,packet);
    }
    public async Task CheckRange(){
        if(!InRange(first.Session.Character,second.Session.Character))await Close(TooFar);
    }

    public async Task Handle(WorldSession sender,uint targetId,byte mode,byte slot,uint quantity,CharacterStore store){
        var (owner,partner)=sender==first.Session?(first,second):(second,first);
        if(partner.Session.Character.Id!=targetId)return;
        if(mode==4){await Close("교환을 취소했습니다.");return;}
        if(!InRange(sender.Character,partner.Session.Character)){
            await Close(TooFar);
            return;
        }
        if(mode==5){await Confirm(owner,partner,store);return;}
        if(owner.Confirmed||partner.Confirmed||owner.Offers.Count==OfferLimit||
            owner.Offers.Any(o=>o.Source.InventorySlot==slot))return;
        var item=sender.Character.Items?.SingleOrDefault(i=>i.InventorySlot==slot);
        if(slot==0||item==null||item.Quantity==0)return;
        if(mode==1&&item.Quantity>1){
            await WorldHub.Notify(sender,GamePackets.ExchangeQuantity(slot));
            return;
        }
        if(mode==1)quantity=1;
        if(quantity==0||quantity>item.Quantity)return;
        owner.Offers.Add((item,quantity));
        var offer=item with {Quantity=quantity};
        byte index=(byte)owner.Offers.Count;
        await WorldHub.Notify(sender,GamePackets.ExchangeItem(false,index,offer));
        await WorldHub.Notify(partner.Session,GamePackets.ExchangeItem(true,index,offer));
    }

    async Task Confirm(Participant owner,Participant partner,CharacterStore store){
        if(owner.Confirmed)return;
        if(partner.Confirmed){
            var ownItems=Plan(owner,partner);
            var peerItems=Plan(partner,owner);
            if(ownItems==null||peerItems==null){
                await Close("교환 물품이 바뀌었거나 소지품 창에 빈 칸이 없습니다.");
                return;
            }
            var oldOwner=owner.Session.Character;
            var oldPartner=partner.Session.Character;
            (Character newOwner,Character newPartner) updated;
            try{
                updated=store.ExchangeItems(oldOwner.Id,ownItems,oldPartner.Id,peerItems);
            }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Exchange persistence: {ex.Message}");
                await Close("교환 내용을 저장하지 못했습니다.");
                return;
            }
            owner.Session.Character=updated.newOwner;
            partner.Session.Character=updated.newPartner;
            Detach();
            await NotifyConfirmation(owner,partner,"교환이 완료되었습니다.");
            await SendInventoryChanges(owner.Session,oldOwner.Items??[],ownItems);
            await SendInventoryChanges(partner.Session,oldPartner.Items??[],peerItems);
            return;
        }
        owner.Confirmed=true;
        await NotifyConfirmation(owner,partner,"");
    }
    static async Task NotifyConfirmation(Participant owner,Participant partner,string text){
        await WorldHub.Notify(owner.Session,GamePackets.ExchangeConfirm(false,text));
        await WorldHub.Notify(partner.Session,GamePackets.ExchangeConfirm(true,text));
    }
    static ItemPlacement[]? Plan(Participant owner,Participant partner){
        var next=(owner.Session.Character.Items??[]).ToList();
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var (source,quantity) in owner.Offers){
            if(source.IsExpired(now))return null;
            int index=next.FindIndex(item=>item.InventorySlot==source.InventorySlot&&
                item.Key==source.Key&&item.Quantity==source.Quantity&&item.Durability==source.Durability&&
                item.NativeDurability==source.NativeDurability&&item.BaitRemaining==source.BaitRemaining&&item.ExpiresAt==source.ExpiresAt);
            if(index<0)return null;
            if(quantity==source.Quantity)next.RemoveAt(index);
            else next[index]=next[index] with {Quantity=source.Quantity-quantity};
        }
        foreach(var (source,quantity) in partner.Offers){
            byte slot=owner.Session.Character.FreeInventorySlots(next).FirstOrDefault();
            if(slot==0)return null;
            next.Add(source with {InventorySlot=slot,Quantity=quantity});
        }
        return next.ToArray();
    }
    static async Task SendInventoryChanges(WorldSession session,ItemPlacement[] previous,ItemPlacement[] current){
        long bagWeight=EquipmentItems.CarriedWeightTotal(previous);
        foreach(var old in previous.Where(p=>p.InventorySlot!=0&&!current.Contains(p))){
            bagWeight-=EquipmentItems.InventoryWeight(old);
            await WorldHub.Notify(session,GamePackets.InventoryRemove(old.InventorySlot,EquipmentItems.WeightWord(bagWeight)));
        }
        foreach(var item in current.Where(p=>p.InventorySlot!=0&&!previous.Contains(p))){
            bagWeight+=EquipmentItems.InventoryWeight(item);
            await WorldHub.Notify(session,GamePackets.InventoryAdd(item,EquipmentItems.WeightWord(bagWeight)));
        }
    }
}
