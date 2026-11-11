namespace ElanciaLocal;

internal sealed class WorldParty(WorldSession leader)
{
    readonly SortedDictionary<byte,WorldSession> members=new(){[0]=leader};
    // Nexon's columns are warrior, adventurer, and merchant. Rows use the total party size.
    static readonly byte[][] CounterRates=[
        [9,7,4],[10,8,5],[8,10,5],[6,9,11],[6,11,9],[7,10,12]
    ];

    internal int CounterChance(WorldSession trainee){
        if(members.Count<2||!members.ContainsValue(trainee))return 0;
        var rates=CounterRates[members.Count-2];
        // C03's first selection occupies bits 4–5. Its order is warrior, adventurer, merchant.
        return members.Values.Where(member=>member.Ready&&WorldHub.CanSee(trainee.Character,member.Character))
            .Sum(member=>rates[member.Character.AptitudeOrder()>>4]);
    }

    internal ushort TrainingCounters(WorldSession trainee,ushort baseCounters,int roll)=>
        roll<CounterChance(trainee)?(ushort)(baseCounters*2):baseCounters;

    public static async Task Join(WorldSession inviter,WorldSession invitee){
        if(inviter==invitee)return;
        if(inviter.Party!=null&&inviter.Party==invitee.Party)return;
        if(!invitee.Character.PartyAllowed){
            await WorldHub.Notify(inviter,GamePackets.Message("상대가 파티 신청을 허용하지 않습니다."));
            return;
        }
        var party=inviter.Party??new WorldParty(inviter);
        if(party.members[0]!=inviter||party.members.Count>=7||invitee.Party!=null){
            await WorldHub.Notify(inviter,GamePackets.Message("이 캐릭터와 파티를 구성할 수 없습니다."));
            return;
        }
        bool newParty=inviter.Party==null;
        byte slot=Enumerable.Range(1,6).Select(index=>(byte)index).First(index=>!party.members.ContainsKey(index));
        party.members.Add(slot,invitee);
        inviter.Party=invitee.Party=party;
        await WorldHub.Notify(invitee,GamePackets.Message($"{inviter.Character.Name}님과 파티를 맺었습니다."));
        if(newParty){
            await WorldHub.Notify(inviter,GamePackets.Message("파티를 만들었습니다."));
            await WorldHub.Notify(inviter,MemberPacket(0,inviter));
        }
        var addition=MemberPacket(slot,invitee);
        foreach(var viewer in party.members.Values)
            if(viewer!=invitee)await WorldHub.Notify(viewer,GamePackets.Message(viewer==inviter
                ?$"{invitee.Character.Name}님과 파티를 맺었습니다."
                :$"{invitee.Character.Name}님이 파티에 참가했습니다."),addition);
        await party.SendRoster(invitee,clear:false);
    }

    public async Task Remove(WorldSession requester,byte index){
        if(!members.TryGetValue(index,out var target))return;
        if(requester==members[0]||requester==target)await Leave(target);
    }

    public async Task Leave(WorldSession member){
        var entry=members.FirstOrDefault(row=>row.Value==member);
        if(entry.Value==null)return;
        if(entry.Key==0||members.Count==2){
            var remaining=members.Values.ToArray();
            members.Clear();
            foreach(var viewer in remaining)viewer.Party=null;
            foreach(var viewer in remaining)await WorldHub.Notify(viewer,GamePackets.PartyClear());
            return;
        }
        members.Remove(entry.Key);
        member.Party=null;
        await WorldHub.Notify(member,GamePackets.PartyClear());
        var removal=GamePackets.PartyRemove(entry.Key);
        var notice=GamePackets.Message($"{member.Character.Name}님이 파티에서 나갔습니다.");
        foreach(var viewer in members.Values)await WorldHub.Notify(viewer,notice,removal);
    }

    static byte Health(Character member)=>(byte)(100UL*member.CurrentHp()/member.MaxHp(EquipmentItems.EquippedEffects(member)));

    public async Task UpdateHealth(WorldSession member){
        var entry=members.FirstOrDefault(row=>row.Value==member);
        if(entry.Value==null)return;
        var packet=GamePackets.PartyVitals(entry.Key,Health(member.Character));
        foreach(var viewer in members.Values)await WorldHub.Notify(viewer,packet);
    }

    static byte[] MemberPacket(byte slot,WorldSession member)=>
        GamePackets.PartyMember(slot,member.Character.Name,Health(member.Character),slot==0);

    internal async Task SendRoster(WorldSession viewer,bool clear=true){
        if(clear)await WorldHub.Notify(viewer,GamePackets.PartyClear());
        foreach(var (slot,member) in members)await WorldHub.Notify(viewer,MemberPacket(slot,member));
    }
}
