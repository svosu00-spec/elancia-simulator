'use strict';
// Read-only observation hooks: no packet injection, blocking, authentication,
// address redirection, encryption keys, or changes to game data.
const cfg = __CONFIG__;
const base = Process.getModuleByName('jElancia.exe').base;
const allowed = new Set(cfg.serverOpcodes);
const decorationKeys = new Set(cfg.decorationKeys);
const outbound = new Map();
const spellTargets = new Map();
const serverPlain = new Set([0,3,0x40]);
const clientPlain = new Set([0,0x10,0x48]);
let packetCounter = 0;
let skipped = {}, errors = 0, received = 0, stopped = false;
let lastMap=null, roomTransitionUntil=0, fishingNoticeUntil=0;
let selfId=null, pendingPickup=null;
let friendNames=null, pendingFriend=null;
const partyNames=new Array(7).fill(null);
const partyNoticeWords=(cfg.partyNoticeWords??[]).map(([word,wire])=>[word,String.fromCharCode(...wire)]);
let pendingPartyNotice=null;
let masteryUpdate=null, masteryMarker=null;
function nextPacketId() { return cfg.attachId+':'+(++packetCounter); }
function u16be(p) { return (p.readU8()<<8)|p.add(1).readU8(); }
function u32be(p) { return u16be(p)*65536+u16be(p.add(2)); }
function skip(dir, op) { const k=dir+':'+op.toString(16).padStart(2,'0'); skipped[k]=(skipped[k]||0)+1; }
function frame(packetId,direction,transport,opcode,bodyLength,plainLength,encrypted,wireSequence,plainOpcode,messageMode,speech) {
    const lengthMatches=plainLength>0&&bodyLength===plainLength+(encrypted?1:0);
    const opcodeMatches=opcode===plainOpcode;
    send({type:'frame',packetId,direction,transport,opcode,bodyLength,plainLength,encrypted,wireSequence,
        lengthMatches,opcodeMatches,
        ...(transport==='aa'?{marker:0xaa,frameLength:bodyLength+3}:{}),
        ...(messageMode!==undefined?{messageMode}:{}),
        ...(lengthMatches&&opcodeMatches&&speech?{speech}:{})});
}
function speechFields(p,n,inbound) {
    const header=inbound?8:4;
    if(n<header||n>header+256)return;
    const textBytes=p.add(header-1).readU8(), end=header+textBytes;
    if(end!==n&&!(end+1===n&&p.add(end).readU8()===0))return;
    return {color:p.add(2).readU8(),textBytes,padding:n-end,...(inbound?{speaker:u32be(p.add(3))}:{})};
}
function zeroString(b, pos) {
    if(pos>=b.length) throw Error('short string');
    const n=b[pos++]; if(pos+n>b.length) throw Error('short string body');
    b.fill(0,pos,pos+n); return pos+n;
}
function sanitizeFriendList(b) {
    if(b.length<3)return null;
    // Compare wire names inside the observer and export only lengths and ordering facts.
    const names=[];
    let pos=3;
    for(let i=0;i<b[2];i++){
        if(pos>=b.length||pos+1+b[pos]>=b.length)throw Error('short friend entry');
        const end=pos+1+b[pos];
        names.push(String.fromCharCode(...b.subarray(pos+1,end)));
        b.fill(0,pos+1,end);
        pos=end+1;
    }
    if(b.subarray(pos).some(value=>value!==0))return null;
    const facts={bytewiseAscending:names.every((name,i)=>i===0||names[i-1]<=name)};
    if(pendingFriend&&Date.now()<=pendingFriend.expires){
        const {packetId,target,previous}=pendingFriend;
        facts.requestPacketId=packetId;
        facts.targetIndex=names.indexOf(target);
        if(previous!==null){
            const oldOthers=previous.filter(name=>name!==target), newOthers=names.filter(name=>name!==target);
            facts.otherEntriesKeptOrder=oldOthers.length===newOthers.length&&oldOthers.every((name,i)=>name===newOthers[i]);
        }
    }
    pendingFriend=null;
    friendNames=names;
    return facts;
}
function matchesChatNotice(p,n) {
    if(!cfg.chatNoticeMatches?.length||n<4||n>512)return false;
    const end=4+u16be(p.add(2));
    if(end>n)return false;
    const text=new Uint8Array(p.add(4).readByteArray(end-4));
    return cfg.chatNoticeMatches.some(pattern=>{
        for(let start=0;start+pattern.length<=text.length;start++){
            if(pattern.every((value,i)=>value===text[start+i]))return true;
        }
        return false;
    });
}
function partyNoticeAffix(wire) {
    const segments=new Array(wire.length+1);
    segments[0]='';
    for(let offset=0;offset<wire.length;offset++){
        if(segments[offset]===undefined)continue;
        for(const [word,bytes] of partyNoticeWords)
            if(wire.startsWith(bytes,offset))segments[offset+bytes.length]=segments[offset]+word;
    }
    return segments[wire.length];
}
function redactPartyNotice(notice, member, packetId) {
    if(!notice||!member||Date.now()-notice.time>100)return;
    const [connection,index]=packetId.split(':'), [previous,priorIndex]=notice.packetId.split(':');
    if(connection!==previous||Number(index)!==Number(priorIndex)+1)return;
    const start=notice.text.indexOf(member);
    if(start<0||notice.text.indexOf(member,start+1)>=0)return;
    const prefix=partyNoticeAffix(notice.text.slice(0,start));
    const suffix=partyNoticeAffix(notice.text.slice(start+member.length));
    if(prefix===undefined||suffix===undefined||!(prefix+suffix).includes('파티'))return;
    return {packetId:notice.packetId,prefix,suffix,memberBytes:member.length,padding:notice.padding};
}
function sanitizeCashShop(b) {
    if(b.length<2)throw Error('missing cash shop mode');
    if(b[1]!==0)return b.slice(0,2);
    if(b.length<3)throw Error('missing cash shop count');
    let pos=3;
    for(let i=0;i<b[2];i++){
        b.fill(0,pos,pos+8); // Order and entry IDs.
        pos=zeroString(b,zeroString(b,pos+10));
        pos+=4;
        if(pos>=b.length)throw Error('missing cash shop description flag');
        if(b[pos++]){
            if(pos+2>b.length)throw Error('missing cash shop description length');
            const end=pos+2+((b[pos]<<8)|b[pos+1]);
            if(end>b.length)throw Error('short cash shop description');
            b.fill(0,pos+2,end);
            pos=end;
        }
        pos=zeroString(b,pos);
        if(pos+9>b.length)throw Error('short cash shop row suffix');
        b.fill(0,pos,pos+4); // Price.
        b.fill(0,pos+5,pos+9); // Package ID.
        pos+=9;
    }
    if(pos!==b.length&&!(pos+1===b.length&&b[pos]===0))throw Error('invalid cash shop list suffix');
    return b;
}
function sanitizeDragDropItems(b) {
    const bank=b[1]===2;
    const list=b[2]===0;
    const start=bank?8:4;
    const count=list?b[start-1]:1;
    if(bank&&count>100)throw Error('too many bank slots');
    let pos=list?start:3;
    for(let i=0;i<count;i++){
        if(pos+3>b.length||bank&&(b[pos]<1||b[pos]>100))throw Error('invalid drag-and-drop slot');
        pos=zeroString(b,zeroString(b,pos+3))+(bank?9:15);
        if(pos>=b.length)throw Error('short drag-and-drop description flag');
        if(b[pos++]){
            if(pos+2>b.length)throw Error('short drag-and-drop description length');
            const end=pos+2+((b[pos]<<8)|b[pos+1]);
            if(end>b.length)throw Error('short drag-and-drop description');
            b.fill(0,pos+2,end);
            pos=end;
        }
        pos+=6;
        if(pos>b.length)throw Error('short drag-and-drop item suffix');
    }
    if(pos!==b.length&&!(pos+1===b.length&&b[pos]===0))throw Error('invalid drag-and-drop packet suffix');
    return b;
}
function sanitizeClanStatus(b) {
    if(b.length<2)throw Error('missing clan mode');
    if(b[1]>1)return b.slice(0,2);
    try {
        let pos=zeroString(b,zeroString(b,2))+7;
        if(b[1]===1){
            if(pos+2>b.length)throw Error('missing clan roster count');
            const count=(b[pos]<<8)|b[pos+1];
            pos+=2;
            if(count>Math.floor((b.length-pos)/2))throw Error('short clan roster');
            for(let i=0;i<count;i++)pos=zeroString(b,pos)+1;
        }
        if(pos>b.length)throw Error('short clan status');
        // Retain only fields consumed by the mode's native reader and a lone zero pad.
        if(pos+1===b.length&&b[pos]===0)pos++;
        return b.slice(0,pos);
    } catch(e) {
        return b.slice(0,2);
    }
}
function sanitizeRoomLabel(b, pos) {
    if(pos>=b.length)throw Error('short room label');
    const end=pos+1+b[pos];
    if(end>b.length)throw Error('short room label body');
    const key=Array.from(b.subarray(pos+1,end), n=>n.toString(16).padStart(2,'0')).join('');
    return decorationKeys.has(key)?end:zeroString(b,pos);
}
function sanitizeRoom(b) {
    if(b.length<3||b[1]!==3||b[2]>8)return false;
    const selector=b[2];
    let pos=3;
    if(selector===8)pos=21;
    else if(selector===6||selector===7)pos=sanitizeRoomLabel(b,pos)+3;
    else if(selector!==2){
        if(selector===0){
            if(b[pos]>1)return false;
            pos++;
        }
        if(pos>=b.length)return false;
        const count=b[pos++];
        if(selector===1)pos+=2*count;
        else for(let i=0;i<count;i++){
            if(selector!==0)pos+=4;
            pos=sanitizeRoomLabel(b,pos);
            if(selector===0||selector===4)pos+=3;
        }
    }
    return b.length===pos||b.length===pos+1&&b[pos]===0;
}
function sanitizeHuman(b) {
    if(b.length<41) throw Error('short human');
    let pos=zeroString(b,39); if(pos>=b.length) throw Error('short aliases');
    const count=b[pos++];
    for(let i=0;i<count;i++) { if(pos>=b.length) throw Error('short alias'); pos++; pos=zeroString(b,pos); }
}
function sanitizeHumanInfo(b) {
    if(b.length<7||![1,2].includes(b[1]))throw Error('invalid human info');
    let pos=7;
    for(const [bit,max] of [[0x80,1],[0x40,1],[0x20,1],[0x10,2],[0x08,1],[0x04,2],[0x02,6],[0x01,4]]){
        if(!(b[6]&bit))continue;
        if(pos>=b.length)throw Error('short human info');
        const count=max>1?b[pos++]:1;
        if(count>max)throw Error('too many human info entries');
        for(let i=0;i<count;i++){
            pos=zeroString(b,pos);
            if(b[1]===1&&++pos>b.length)throw Error('short human info flag');
        }
    }
    if(pos!==b.length&&!(pos+1===b.length&&b[pos]===0))throw Error('unknown human info suffix');
}
function sanitizeObjects(b) {
    if(b.length<3) throw Error('short objects');
    const count=(b[1]<<8)|b[2]; let pos=3;
    for(let i=0;i<count;i++) {
        if(pos+14>b.length) throw Error('short object');
        const namePos=pos+13; pos+=14+b[namePos];
        if(pos>=b.length) throw Error('short category');
        const category=b[pos++];
        if(category===2||category===6||category===7) { zeroString(b,namePos); pos=zeroString(b,pos); }
        else if(category===3) { pos+=4; if(pos>b.length) throw Error('short item'); }
        else if(category!==0&&category!==1) {zeroString(b,namePos);}
    }
}
Interceptor.attach(base.add(0xB5DB0), {
    onEnter() {
        if(stopped)return;
        try {
            const ctx=this.context;
            this.out=ctx.esp.add(8).readPointer();
            this.capacity=ctx.esp.add(4).readU32();
            if(this.capacity<1||this.capacity>65535){errors++;return;}
            this.op=ctx.eax.readU8();
            this.seq=this.capacity>=2?ctx.eax.add(1).readU8():null;
            const caller=this.returnAddress.sub(base).toUInt32();
            this.transport=caller===0xB59F3?'aa':([0xB583D,0xB5B36].includes(caller)?'text':'unknown');
            this.packetId=nextPacketId();
            this.packetIndex=packetCounter;
        } catch(e) {errors++;}
    },
    onLeave(result) {
        if(stopped||!this.packetId)return;
        try {
            const n=result.toInt32();
            const op=n>0&&n<=this.capacity?this.out.readU8():null;
            const mode=n>=2&&[0x0a,0x0d,0x57].includes(op)?this.out.add(1).readU8():undefined;
            frame(this.packetId,'S',this.transport,this.op,this.capacity,n,true,this.seq,op,
                mode,op===0x0d&&mode===4?speechFields(this.out,n,true):undefined);
            const priorPartyNotice=pendingPartyNotice;
            pendingPartyNotice=null;
            const priorMastery=masteryUpdate, priorMarker=masteryMarker;
            masteryUpdate=masteryMarker=null;
            if(n<1||n>this.capacity||n>65535) { errors++; return; }
            if(op===0x05&&n>=9&&this.transport==='aa'&&op===this.op&&this.capacity===n+1)selfId=u32be(this.out.add(1));
            let confirmedMasteryMarker;
            if(lastMap!==null&&this.transport==='aa'&&op===this.op&&this.capacity===n+1){
                const p=this.out;
                const time=Date.now();
                if(pendingPickup){
                    const pickup=pendingPickup;
                    if(time>pickup.expires||op===0x15||op===0x05)pendingPickup=null;
                    else if(!pickup.itemId&&op===0x53&&n===10&&p.add(9).readU8()===0&&u32be(p.add(5))===selfId){
                        pickup.itemId=u32be(p.add(1));
                        pickup.pickupPacketId=this.packetId;
                        pickup.index=this.packetIndex;
                    }else if(pickup.itemId){
                        if(!pickup.removalPacketId&&pickup.index+1===this.packetIndex&&op===0x0E&&n===6&&
                                p.add(5).readU8()===0&&u32be(p.add(1))===pickup.itemId){
                            pickup.removalPacketId=this.packetId;
                            pickup.index=this.packetIndex;
                        }else{
                            pendingPickup=null;
                            if(pickup.removalPacketId&&pickup.index+1===this.packetIndex&&op===0x0A&&n===28&&(mode===3||mode===4)){
                                const end=4+u16be(p.add(2));
                                if(end===n||end+1===n&&p.add(end).readU8()===0){
                                    send({type:'pickup_notice',packetId:this.packetId,requestPacketId:pickup.requestPacketId,
                                        pickupPacketId:pickup.pickupPacketId,removalPacketId:pickup.removalPacketId,itemId:pickup.itemId},
                                        p.readByteArray(n));
                                    return;
                                }
                            }
                        }
                    }
                }
                if(op===0x2C&&n>=14&&p.add(1).readU8()===1&&p.add(2).readU8()!==0&&p.add(3).readU8()===0){
                    const nameBytes=p.add(9).readU8(), end=13+nameBytes;
                    if(nameBytes>0&&(n===end||n===end+1&&p.add(end).readU8()===0))
                        masteryUpdate={index:this.packetIndex,packetId:this.packetId,slot:p.add(2).readU8(),time};
                }else if(op===0x3F&&n===5&&priorMastery?.index+1===this.packetIndex&&time-priorMastery.time<=500){
                    masteryMarker={index:this.packetIndex,packetId:this.packetId,abilityPacketId:priorMastery.packetId,
                        slot:priorMastery.slot,time:priorMastery.time,words:[u16be(p.add(1)),u16be(p.add(3))]};
                }else if(op===0x2C&&(n===7||n===8&&p.add(7).readU8()===0)&&priorMarker?.index+1===this.packetIndex&&
                        time-priorMarker.time<=500&&p.add(1).readU8()===2&&p.add(2).readU8()===priorMarker.slot){
                    confirmedMasteryMarker={type:'mastery_marker',packetId:priorMarker.packetId,
                        abilityPacketId:priorMarker.abilityPacketId,refreshPacketId:this.packetId,words:priorMarker.words};
                }
            }
            if(op===0x0a&&mode===3&&op===this.op&&this.capacity===n+1&&n>=5&&n<=cfg.partyNoticeLimit){
                const length=u16be(this.out.add(2)), end=4+length;
                if(length>0&&(end===n||end+1===n&&this.out.add(end).readU8()===0)){
                    const text=String.fromCharCode(...new Uint8Array(this.out.add(4).readByteArray(length)));
                    pendingPartyNotice={text,packetId:this.packetId,time:Date.now(),padding:n-end};
                }
            }
            if(op===0x0a&&mode===0&&op===this.op&&this.capacity===n+1&&matchesChatNotice(this.out,n)){
                send({type:'chat_notice',packetId:this.packetId},this.out.readByteArray(n));return;
            }
            if(op===0x0a&&mode===0x0e&&op===this.op&&this.capacity===n+1&&n>=6){
                send({type:'shop_notice',packetId:this.packetId},this.out.readByteArray(n));return;
            }
            if(op===0x0a&&(mode===3||mode===4)&&op===this.op&&this.capacity===n+1&&n>=6&&n<=128&&Date.now()<=fishingNoticeUntil){
                const end=4+u16be(this.out.add(2));
                if(end<=n){
                    for(let i=4;i+1<end;i++)if(u16be(this.out.add(i))===0xb3ac){ // CP949 '낚'
                        send({type:'fishing_notice',packetId:this.packetId},this.out.readByteArray(n));return;
                    }
                }
            }
            if(op===0x0d&&mode===5&&op===this.op&&this.capacity===n+1&&n>=5&&n<=516){
                send({type:'chat',direction:'S',packetId:this.packetId},this.out.readByteArray(n));return;
            }
            // Check replies can contain account text: export only numeric result.
            if(op===2) { if(n>=2)send({type:'check',packetId:this.packetId,code:this.out.add(1).readU8()}); return; }
            if(op===0x15&&n>=3)lastMap=u16be(this.out.add(1));
            if(op===0x64){
                if(lastMap===9801&&n>=3&&this.out.add(1).readU8()===3){
                    const bytes=new Uint8Array(this.out.readByteArray(n));
                    let valid=false;
                    try { valid=sanitizeRoom(bytes); } catch(e) { /* Incomplete room records stay header-only. */ }
                    if(valid){
                        send({type:'packet',direction:'S',packetId:this.packetId},bytes.buffer);
                        received++;return;
                    }
                }
                skip('S',op);return;
            }
            if(op===0x67){
                if(n===7&&Date.now()<=roomTransitionUntil){
                    roomTransitionUntil=0;
                    send({type:'packet',direction:'S',packetId:this.packetId},this.out.readByteArray(7));received++;
                }else skip('S',op);
                return;
            }
            if(!allowed.has(op)) {skip('S',op);return;}
            if(op===0x58&&(lastMap===null||op!==this.op||this.capacity!==n+1||(n!==2&&n!==3))){
                skip('S',op);return;
            }
            if((op===0x3D||op===0x45||op===0x5B)&&(op!==this.op||this.capacity!==n+1)){
                skip('S',op);return;
            }
            // SBlockClientPacket reads two bytes. Keep the observed suffix out of the log.
            if(op===0x51) {
                if(n!==3&&n!==5){errors++;return;}
                send({type:'packet',direction:'S',packetId:this.packetId},this.out.readByteArray(3));received++;return;
            }
            if(op===0x61) {
                if(n<3){errors++;return;}
                const group=this.out.add(1).readU8(),bank=group===2,mode=this.out.add(2).readU8();
                const merc=group===3;
                const prefix=bank?[8,6,4,12][mode]:merc&&mode===0&&n>=4?4:merc&&mode===2&&(n===7||n===8)?7:3;
                if(!prefix||n<prefix){errors++;return;}
                let safe=this.out.readByteArray(prefix);
                if((bank||merc)&&mode<=1&&n>prefix&&op===this.op&&this.capacity===n+1){
                    try {
                        safe=sanitizeDragDropItems(new Uint8Array(this.out.readByteArray(n))).buffer;
                    } catch(e) { /* Fall back to the known numeric prefix. */ }
                }
                send({type:'packet',direction:'S',packetId:this.packetId},safe);received++;return;
            }
            if(op===0x82) {
                if(n<2){errors++;return;}
                const mode=this.out.add(1).readU8();
                // Other modes carry pet names. Export their mode byte only.
                if(mode===4&&!(n===3||n===4&&this.out.add(3).readU8()===0)){errors++;return;}
                send({type:'packet',direction:'S',packetId:this.packetId},this.out.readByteArray(mode===4?n:2));received++;return;
            }
            const b=new Uint8Array(this.out.readByteArray(n));
            if(op===0x3D){
                send({type:'packet',direction:'S',packetId:this.packetId},sanitizeClanStatus(b).buffer);
                received++;return;
            }
            if(op===0x45){
                send({type:'packet',direction:'S',packetId:this.packetId},sanitizeCashShop(b).buffer);
                received++;return;
            }
            if(op===0x62){
                if(op!==this.op||this.capacity!==n+1||n<3){skip('S',op);return;}
                let end;
                try {
                    end=zeroString(b,2);
                    if(b[1]===5)end=zeroString(b,end);
                } catch(e) {skip('S',op);return;}
                if(end!==n&&!(end+1===n&&b[end]===0)){skip('S',op);return;}
            }
            if(op===0x5B){
                if(n<8)throw Error('short advertise info');
                const end=3+((b[1]<<8)|b[2]);
                if(end+5!==n&&!(end+6===n&&b[end+5]===0))throw Error('invalid advertise info length');
                b.fill(0,3,end);
            }
            if(op===0x33) sanitizeHuman(b);
            if(op===0x34) {
                const end=zeroString(b,zeroString(b,6))+35;
                if(end!==b.length&&!(end+1===b.length&&b[end]===0))throw Error('invalid object info length');
            }
            if(op===0x39) zeroString(b,zeroString(b,2));
            if(op===0x69) sanitizeHumanInfo(b);
            if(op===0x4F){
                if(b[1]===2&&b[2]===0&&(n===7||n===8)){
                    send({type:'packet',direction:'S',packetId:this.packetId},b.slice(0,7).buffer);received++;return;
                }else if(b[1]===2&&b[2]===1&&n>=35+b[3]){
                    const nameEnd=4+b[3],end=nameEnd+31;
                    b.fill(0,4,nameEnd);
                    send({type:'packet',direction:'S',packetId:this.packetId},b.slice(0,end).buffer);received++;return;
                }else if(b[1]===2&&b[2]===2){
                    let pos=4,end=3;
                    while(pos<n){
                        const selector=b[pos++];
                        if(selector===0){
                            if(pos===n||pos+1===n)end=n;
                            break;
                        }
                        const width=[0,7,12,12][selector];
                        if(!width||pos+width>n)break;
                        pos+=width;
                    }
                    send({type:'packet',direction:'S',packetId:this.packetId},b.slice(0,end).buffer);received++;return;
                }else if(b[1]===2&&b[2]===11&&(n===5||n===6)){
                    send({type:'packet',direction:'S',packetId:this.packetId},b.slice(0,5).buffer);received++;return;
                }else if(b[1]===2&&b[2]===3){
                    if(n<8)throw Error('short merc equipment');
                    let pos=zeroString(b,7+b[6]);
                    if(pos>=n)throw Error('missing merc description flag');
                    if(b[pos++]){
                        if(pos+2>n)throw Error('short merc description length');
                        pos+=2+((b[pos]<<8)|b[pos+1]);
                    }
                    if(pos+6!==n&&!(pos+7===n&&b[n-1]===0))throw Error('invalid merc equipment length');
                }else if(b[1]===2&&b[2]===12){
                    if(n!==8||b[7]!==0)throw Error('invalid merc control length');
                }else if(b[1]===3){
                    if(n!==10)throw Error('invalid merc dialog length');
                }else{
                    const prefix=Math.min(n,b[1]===2?3:2);
                    send({type:'packet',direction:'S',packetId:this.packetId},b.slice(0,prefix).buffer);received++;return;
                }
            }
            if(op===7) sanitizeObjects(b);
            if(op===0x5D){
                const partyMode=b[1], slot=b[2];
                let member;
                if(partyMode===0){
                    const length=b[3], nameEnd=4+length, end=nameEnd+2;
                    if(end!==n&&!(end+1===n&&b[end]===0))return;
                    if(slot<partyNames.length&&length>0&&length<=32&&!b.subarray(4,nameEnd).includes(0))
                        member=String.fromCharCode(...b.subarray(4,nameEnd));
                    if(slot<partyNames.length)partyNames[slot]=member??null;
                    b.fill(0,4,nameEnd);
                }else if(partyMode===2&&slot<partyNames.length&&(n===3||n===4&&b[3]===0)){
                    member=partyNames[slot];
                    partyNames[slot]=null;
                }else if(partyMode===3&&(n===2||n===3&&b[2]===0))partyNames.fill(null);
                const partyNotice=op===this.op&&this.capacity===n+1
                    ?redactPartyNotice(priorPartyNotice,member,this.packetId):undefined;
                send({type:'packet',direction:'S',packetId:this.packetId,...(partyNotice?{partyNotice}:{})},b.buffer);
                received++;return;
            }
            if(op===0x6A){
                if(b[1]===0){
                    const friendListFacts=sanitizeFriendList(b);
                    if(!friendListFacts)return;
                    send({type:'packet',direction:'S',packetId:this.packetId,friendListFacts},b.buffer);received++;return;
                }else if(b[1]===1||b[1]===2){
                    const end=zeroString(b,2);if(b.subarray(end).some(value=>value!==0))return;
                    friendNames=null;pendingFriend=null;
                }else return;
            }
            send({type:'packet',direction:'S',packetId:this.packetId},b.buffer);received++;
            if(confirmedMasteryMarker)send(confirmedMasteryMarker);
        } catch(e) {errors++;}
    }
});
// The three dispatcher joins also see packets that bypass the decryptor.
for(const [address,lengthOffset,transport] of [[0xB5848,-12,'text'],[0xB5A04,-8,'aa'],[0xB5B41,-8,'text']]) {
    Interceptor.attach(base.add(address),function() {
        if(stopped)return;
        try {
            const ctx=this.context, p=ctx.edi;
            if(p.isNull())return;
            const n=ctx.ebp.add(lengthOffset).readS32();
            if(n<1||n>65535)return;
            const op=p.readU8();
            if(!serverPlain.has(op))return;
            const packetId=nextPacketId();
            frame(packetId,'S',transport,op,n,n,false,null,op);
            if(op===0&&n>=7&&p.add(1).readU8()===0)send({type:'seed',packetId,seed:p.add(6).readU8()});
            skip('S',op);
        } catch(e) {errors++;}
    });
}
// Read target fields only when the original CUseSpell serializer selects mode 2.
// Other modes can carry user-entered text.
Interceptor.attach(base.add(0xD23A6), {
    onEnter() {
        if(stopped)return;
        spellTargets.delete(this.threadId);
        try {
            const spell=this.context.ecx;
            if(spell.add(0x11).readU8()!==2)return;
            spellTargets.set(this.threadId,{slot:spell.add(0x12).readU8(),
                targetId:spell.add(0x13).readU32(),spellTargetFlag:spell.add(0x17).readU8()});
        } catch(e) {errors++;}
    },
});
Interceptor.attach(base.add(0xB48C9), {
    onEnter() {
        if(stopped)return;
        try {
            const n=this.context.ecx.toInt32()&65535, p=this.context.edx;
            if(n<1||n>32767||p.isNull())return;
            const op=p.readU8(), packetId=nextPacketId();
            pendingPickup=null;
            this.pending={op,n,packetId,messageMode:n>=2&&(op===0x0e||op===0x65)?p.add(1).readU8():undefined};
            if(op===0x0e&&this.pending.messageMode===4)this.pending.speech=speechFields(p,n,false);
            this.previous=outbound.get(this.threadId);
            outbound.set(this.threadId,this.pending);
            const action=(fields)=>send({type:'action',packetId,opcode:op,length:n,...fields});
            if(n<=515&&(op===0x19&&n>=3||op===0x0e&&this.pending.messageMode===5&&n>=5))
                this.pending.chat=p.readByteArray(n);
            // Never export CLogin, passwords, transfer tokens, public chat text, or crash reports.
            // Match fixed shrine words and emit labels without message bytes.
            if(op===0x0E&&n===8&&p.add(1).readU8()===4&&p.add(3).readU8()===4){
                const word=u32be(p.add(4));
                if(word===0xB1E2B5B5){action({action:'shrine_prayer'});return;}
                if(word===0xC1F7BEF7){action({action:'shrine_job'});return;}
                if(word===0xB8EDBCBA){action({action:'shrine_fame'});return;}
            }
            if(op===2) {action({action:'creation_name_submitted'});return;}
            if(op===3&&n===13) {
                action({action:'creation_appearance',appearance:Array.from(new Uint8Array(p.add(1).readByteArray(7)))});return;
            }
            if(op===0x8D&&n>=3) {action({action:p.add(2).readU8()===0?'creation_started':'character_selection'});return;}
            if(op===0x10) {lastMap=null;selfId=null;roomTransitionUntil=0;friendNames=null;pendingFriend=null;partyNames.fill(null);pendingPartyNotice=null;masteryUpdate=masteryMarker=null;send({type:'context_reset',packetId});return;}
            if(op===0x33&&n===3) {action({action:'move_request',direction:p.add(1).readU8(),sequence:p.add(2).readU8()});return;}
            if(op===0x0C&&n===5&&lastMap!==null) {action({action:'object_refresh_request',targetId:u32be(p.add(1))});return;}
            if(op===0x11&&n===2) {action({action:'turn_request',direction:p.add(1).readU8()});return;}
            if(op===0x51&&n===2) {action({action:'combat_mode_request',mode:p.add(1).readU8()});return;}
            if(op===0x1B&&n===3&&p.add(1).readU8()===1&&p.add(2).readU8()===0) {
                action({action:'status_option_toggle',mode:1});return;
            }
            if(op===0x73&&n===2&&lastMap!==null) {
                action({action:'web_board_request',mode:p.add(1).readU8()});return;
            }
            if(op===0x85&&n===4&&p.add(1).readU8()===0) {
                action({action:'pet_slot_request',mode:0,slot:p.add(2).readU8(),petAction:p.add(3).readU8()});return;
            }
            if(op===0x13&&n===3) {
                const attackMode=p.add(1).readU8(),attackParam=p.add(2).readU8();
                fishingNoticeUntil=attackMode===5&&attackParam===0?Date.now()+2000:0;
                action({action:'interaction',attackMode,attackParam});return;
            }
            // CUseSkill emits the skill slot first. Remaining fields depend on the
            // skill's argument type and may include text, so retain only the slot.
            if(op===0x3E&&n>=2) {
                action({action:'interaction',slot:p.add(1).readU8(),slotType:'skill'});return;
            }
            if(op===0x14&&n===7) {
                action({action:'interaction',attackMode:p.add(1).readU8(),targetId:u32be(p.add(2)),attackParam:p.add(6).readU8()});return;
            }
            if(op===0x0F&&n>=2) {
                const target=spellTargets.get(this.threadId), slot=p.add(1).readU8();
                spellTargets.delete(this.threadId);
                if(n===7&&target?.slot===slot&&target.targetId===u32be(p.add(2))&&
                    target.spellTargetFlag===p.add(6).readU8()){
                    action({action:'spell_slot_request',...target});return;
                }
                action({action:'spell_slot_request',slot});return;
            }
            if(op===0x56&&n===2) {action({action:'spell_cast_request',mode:p.add(1).readU8()});return;}
            if(op===0x07&&n===10) {
                const mode=p.add(1).readU8();
                if(mode===0&&lastMap!==null&&selfId)pendingPickup={requestPacketId:packetId,expires:Date.now()+1000};
                action({action:'interaction',mode,pickupWords:[2,4,6,8].map(i=>u16be(p.add(i)))});return;
            }
            if(op===0x08&&n===10) {
                action({action:'interaction',slot:p.add(1).readU8(),dropX:u16be(p.add(2)),dropY:u16be(p.add(4)),dropQuantity:u32be(p.add(6))});return;
            }
            if(op===0x30&&n===8) {
                action({action:'inventory_drag',mode:p.add(1).readU8(),slot:p.add(2).readU8(),
                    destinationSlot:p.add(3).readU8(),dropX:u16be(p.add(4)),dropY:u16be(p.add(6))});return;
            }
            if(op===0x58&&n===11) {
                action({action:'inventory_bundle',slot:p.add(1).readU8(),destinationSlot:p.add(2).readU8(),
                    dropX:u16be(p.add(3)),dropY:u16be(p.add(5)),dropQuantity:u32be(p.add(7))});return;
            }
            if(op===0x6D&&n===6) {
                action({action:'inventory_remove_request',slot:p.add(1).readU8(),dropQuantity:u32be(p.add(2))});return;
            }
            if(op===0x45&&n===3){
                action({action:'crc_reply',checksum:u16be(p.add(1))});return;
            }
            if(op===0x36&&n===2){
                action({action:'clan_info_request',mode:p.add(1).readU8()});
                return;
            }
            if(op===0x6C&&n>=2){
                const mode=p.add(1).readU8();
                if(((mode===0||mode===2)&&n===2)||((mode===1||mode===3)&&n===10))
                    action({action:'cash_shop_request',mode});
                return;
            }
            if(op===0x69&&n===7){
                action({action:'push_request',pushX:u16be(p.add(1)),pushY:u16be(p.add(3)),
                    pushFloor:p.add(5).readU8(),direction:p.add(6).readU8()});return;
            }
            if(op===0x35&&n===5) {
                action({action:'map_checksum',mapId:u16be(p.add(1)),checksum:u16be(p.add(3))});return;
            }
            if(op===0x72&&n>=3) {
                const group=p.add(1).readU8(),mode=p.add(2).readU8();
                if((group===2||group===3)&&mode<=2&&n===13)action({
                    action:group===2?'bank_move':'merc_inventory_move',mode,slot:p.add(3).readU8(),
                    destinationSlot:p.add(4).readU8(),dropX:u16be(p.add(5)),dropY:u16be(p.add(7)),dropQuantity:u32be(p.add(9))});
                else if(group===2&&mode===3&&n===8)
                    action({action:'bank_move',mode,slot:p.add(3).readU8(),dropQuantity:u32be(p.add(4))});
                if(group===2||group===3)return;
            }
            if(op===0x2D&&n===2){action({action:'self_look_request'});return;}
            if(op===0x77&&n>=3){
                const mode=p.add(1).readU8(), value=p.add(2).readU8();
                pendingFriend=null;
                if(mode===0&&n===3&&value===0)action({action:'friend_request',mode});
                else if((mode===1||mode===2)&&value>0&&n===value+3){
                    const target=String.fromCharCode(...new Uint8Array(p.add(3).readByteArray(value)));
                    pendingFriend={packetId,target,previous:friendNames,expires:Date.now()+250};
                    action({action:'friend_request',mode,inputBytes:value,
                            ...(friendNames!==null?{targetPreviouslyListed:friendNames.includes(target)}:{})});
                }
                return;
            }
            if(op===0x2E&&n>=2){
                const mode=p.add(1).readU8();
                if(mode===0&&n===2)action({action:'party_request',mode});
                else if(mode===1&&n===6)action({action:'party_request',mode,targetId:u32be(p.add(2))});
                else if(mode===2&&n===3)action({action:'party_request',mode,slot:p.add(2).readU8()});
                return;
            }
            if(op===0x39&&n>=8){
                const fields={action:'interaction',mode:p.add(1).readU8(),targetId:u32be(p.add(2)),menu:u16be(p.add(6))};
                if(n>=10&&fields.menu===0x4b&&(fields.mode===1||fields.mode===14)){
                    const count=p.add(9).readU8(),lines=[];
                    let pos=10;
                    while(lines.length<count&&pos<n){
                        if(fields.mode===14){
                            if(pos+5>n)break;
                            lines.push({productId:u32be(p.add(pos)),quantity:p.add(pos+4).readU8()});
                            pos+=5;
                        }else{
                            const nameBytes=p.add(pos++).readU8();
                            if(pos+nameBytes>=n)break;
                            pos+=nameBytes;
                            lines.push({nameBytes,quantity:p.add(pos++).readU8()});
                        }
                    }
                    if(pos===n&&lines.length===count&&count<=50){
                        fields.orderMode=p.add(8).readU8();
                        fields.orderLines=lines;
                    }
                }else if(n>=10&&(fields.menu===0x4e||fields.menu===0x5b)){
                    const count=p.add(9).readU8(),lines=[];
                    let pos=10;
                    while(lines.length<count&&pos+2<=n){
                        const kind=fields.menu===0x5b?p.add(pos++).readU8():0;
                        if(kind>1||pos+(kind===0?2:1)>n)break;
                        lines.push({kind,slot:p.add(pos++).readU8(),quantity:kind===0?p.add(pos++).readU8():1});
                    }
                    if(pos===n&&lines.length===count&&count<=100){
                        fields.orderMode=p.add(8).readU8();
                        fields.orderLines=lines;
                    }
                }
                action(fields);return;
            }
            if(op===0x3A&&n>=11){
                const dialogAction=p.add(10).readU8();
                action({action:'interaction',mode:p.add(1).readU8(),targetId:u32be(p.add(2)),script:u16be(p.add(6)),
                    page:u16be(p.add(8)),dialogAction,...(dialogAction===1&&n===12?{dialogChoice:p.add(11).readU8()}:{}),
                    ...(dialogAction===2&&n>=12&&p.add(11).readU8()===n-12?{dialogInputBytes:n-12}:{})});return;
            }
            if(n>=6&&(op===0x43||op===0x76||op===0x4A)){
                action({action:'interaction',mode:p.add(1).readU8(),targetId:u32be(p.add(2))});return;
            }
            if(op===0x54&&n>=3&&p.add(1).readU8()===2){
                const selector=p.add(2).readU8();
                if(n===3&&[0,3,10].includes(selector)){
                    action({action:'merc_dialog',mode:2,selector});return;
                }
                if(n===4&&[1,6].includes(selector)){
                    action({action:'merc_dialog',mode:2,selector,controlOptions:[p.add(3).readU8()]});return;
                }
                if(n===8&&selector===2){
                    action({action:'merc_dialog',mode:2,selector,slot:p.add(3).readU8(),
                        mercWords:[u16be(p.add(4)),u16be(p.add(6))]});return;
                }
                if(n===5&&selector===4&&p.add(3).readU8()===p.add(4).readU8()){
                    action({action:'merc_dialog',mode:2,selector,slot:p.add(3).readU8()});return;
                }
                if(n===5&&selector===9){
                    action({action:'merc_dialog',mode:2,selector,controlOptions:[p.add(3).readU8(),p.add(4).readU8()]});return;
                }
            }
            if(op===0x54&&n>=7&&p.add(1).readU8()===3){
                if(n===7)roomTransitionUntil=Date.now()+1500;
                action({action:'merc_dialog',mode:3,targetId:u32be(p.add(2)),selector:p.add(6).readU8(),inputBytes:n-7});return;
            }
            if(op===0x6A&&n>=3&&p.add(1).readU8()===3){
                const selector=p.add(2).readU8();
                if((selector===10||lastMap===9801&&[0,7,12].includes(selector))&&
                   n>=4&&p.add(3).readU8()===n-4){
                    action({action:'room_minigame',mode:3,selector,inputBytes:n-4});return;
                }
                if(lastMap===9801&&((n===3&&[1,3,5,8,11].includes(selector))||selector===2&&n===5)){
                    if(selector===1||selector===2)roomTransitionUntil=Date.now()+1500;
                    action({action:'room_minigame',mode:3,selector,...(selector===2?{value:u16be(p.add(3))}:{})});return;
                }
                if(lastMap===9801&&(selector===4&&n===10||[6,9].includes(selector)&&n===7)){
                    const placement=selector===4?[p.add(7).readU8(),p.add(8).readU8(),p.add(9).readU8()]:undefined;
                    action({action:'room_minigame',mode:3,selector,entryId:u32be(p.add(3)),placement});return;
                }
            }
            if(op===0x16&&n>=6){
                const targetId=u32be(p.add(1));
                if(targetId===0&&n===10&&p.add(5).readU8()===1) {
                    action({action:'destination_choice',targetId,mapId:u32be(p.add(6))});return;
                }
                action({action:'interaction',targetId});return;
            }
            if((op===0x1C||op===0x1E)&&n===2) {
                action({action:'interaction',slot:p.add(1).readU8(),slotType:'inventory'});return;
            }
            if(op===0x44&&n===6) {
                action({action:'interaction',slot:p.add(1).readU8(),slotType:'equipment',
                    dropX:u16be(p.add(2)),dropY:u16be(p.add(4))});return;
            }
            if([0x07,0x08,0x13,0x14,0x16,0x1C,0x1E,0x39,0x44,0x55,0x6E].includes(op)) {
                action({action:'interaction'});return;
            }
            skip('C',op);
        } catch(e) {errors++;}
    },
    onLeave() {
        if(!this.pending)return;
        if(this.previous)outbound.set(this.threadId,this.previous);
        else outbound.delete(this.threadId);
    },
});
// Report the frame after native encryption, before binary/text serialization.
for(const [address,transport] of [[0xB4932,'aa'],[0xB49C9,'text']]) {
    Interceptor.attach(base.add(address),function() {
        if(stopped)return;
        try {
            const pending=outbound.get(this.threadId);
            if(!pending)return;
            const ctx=this.context, aa=transport==='aa';
            const n=aa?ctx.ebx.toInt32():(ctx.eax.toUInt32()&65535);
            const p=ctx.ebp.add(aa?-0x7c:-0x80).readPointer();
            if(n<1||n>65535||p.isNull()){errors++;return;}
            const encrypted=!clientPlain.has(pending.op);
            const wireOpcode=p.readU8();
            frame(pending.packetId,'C',transport,wireOpcode,n,pending.n,encrypted,
                encrypted&&n>=2?p.add(1).readU8():null,pending.op,pending.messageMode,pending.speech);
            if(pending.chat&&wireOpcode===pending.op&&n===pending.n+(encrypted?1:0)){
                send({type:'chat',direction:'C',packetId:pending.packetId},pending.chat);
                pending.chat=null;
            }
        } catch(e) {errors++;}
    });
}
// Seed map context when attaching to a client already in the world. Only read
// known map/player identifiers, never arbitrary memory regions or account data.
function snapshot() {
    try {
        const map=base.add(0x18EB1C).readPointer(),player=base.add(0x18DAD4).readPointer();
        if(!map.isNull()&&!player.isNull()) {
            const id=map.add(0x10A).readU32(),oid=player.add(0x62).readU32();
            if(id<=65535&&oid!==0) {
                if(lastMap===null)lastMap=id;
                if(selfId===null)selfId=oid;
                const x=player.add(0x10).readU32(),y=player.add(0x14).readU32(),floor=player.add(0x18).readU32();
                const position=x<=65535&&y<=65535&&floor<=3?{x,y,floor}:null;
                send({type:'snapshot',map:id,selfId:oid,position});
            }
        }
    }catch(e){}
}
snapshot();
const timer=setInterval(()=>{if(stopped)return;send({type:'stats',skipped,hookErrors:errors,received});skipped={};},1000);
rpc.exports={stop(){stopped=true;clearInterval(timer);Interceptor.detachAll();return {received,hookErrors:errors,skipped};}};
send({type:'ready',module:'jElancia.exe',base:base.toString()});
