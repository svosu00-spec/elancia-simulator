"""Version-specific protocol decoding and local, append-only observation storage."""
from __future__ import annotations
import collections, datetime, hashlib, json, os, re, shutil, struct, time, zlib
from pathlib import Path

EXPECTED_SHA256='61d79a82c676e30750c015301b0eeed5f5df2729180ed8be4f45aa2042af8c5a'
# The v611 receive table has null factory entries for these observed server opcodes.
UNREGISTERED_SERVER_OPCODES=frozenset({0x3F,0x58,0x67,0x68,0x96})
MERCHANT_LIST_MODES=frozenset({10,11,14})
PARTY_NOTICE_WORDS=tuple((
    '파티 파티원 파티장 그룹 팀 멤버 동료 새 새로운 다른 현재 기존 추가 가입 합류 참가 참여 입장 입회 '
    '신청 요청 수락 초대 초청 탈퇴 이탈 제외 추방 방출 해제 해산 종료 변경 등록 구성 결성 만들기 '
    '받아들여 받아들였습니다 했습니다 하였습니다 하셨습니다 했어요 하였어요 되었습니다 됐습니다 되셨습니다 '
    '됐어요 되었어요 들어왔습니다 들어오셨습니다 들어왔어요 들어오게 나갔습니다 나가셨습니다 '
    '나갔어요 나왔습니다 나오셨습니다 떠났습니다 빠졌습니다 빠져나갔습니다 맺었습니다 맺게 되었습니다 '
    '함께 같이 성공 님 분 명 으로 에서 에게 께서 부터 까지 님께서는 께서는 이 가 은 는 을 를 의 에 로 와 과 과의 '
    '한 하여 하게 합니다 수 없는 알림 안내 당신 상대방 자신').split())+tuple(' []().!,:<>~{}')+tuple(
        '{='+color for color in 'abcdefghijklmnopqrstuvwxyz')
PARTY_NOTICE_LIMIT=128
SERVER_OPCODES={4,5,7,8,0x0B,0x0C,0x0E,0x0F,0x10,0x11,0x13,0x14,0x15,
    0x17,0x18,0x19,0x1A,0x1E,0x1F,0x20,0x21,0x27,0x29,0x2C,0x2D,
    0x2F,0x30,0x33,0x34,0x37,0x38,0x39,0x3A,0x3B,0x3D,0x45,0x4F,0x50,0x51,0x52,0x53,0x55,0x58,0x5B,0x5D,0x5F,
    0x61,0x62,0x64,0x67,0x69,0x6A,0x6B,0x6C,0x6E,0x6F,0x82,0x85}
DELTAS=((0,-1),(1,0),(0,1),(-1,0),(1,-1),(1,1),(-1,1),(-1,-1))
HUMAN_INFO_GROUPS=((0x80,0,1),(0x40,4,1),(0x20,5,1),(0x10,6,2),
                   (0x08,1,1),(0x04,2,2),(0x02,8,6),(0x01,14,4))
STAGES=('00 준비 / 로그인','01 캐릭터 생성','02 첫 접속 / 초기 상태','03 이동 / 맵 전환',
        '04 NPC 대화 / 상점','05 아이템 / 장비','06 전투 / 스킬','07 기타')
CATALOG={}
try:
    root=Path(__file__).resolve().parent.parent
    path=root/'evidence/packet-catalog.json'
    if not path.is_file():path=root/'ElanciaLocalServer/evidence/packet-catalog.json'
    catalog=json.loads(path.read_text(encoding='utf-8'))
    if catalog.get('sha256')==EXPECTED_SHA256:CATALOG={(p['direction'],p['opcode']):p for p in catalog['packets']}
except (OSError,ValueError):pass

def now(): return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='milliseconds')
def dump(path,value):
    path=Path(path); temp=path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8');os.replace(temp,path)

def valid_party_notice_affix(text):
    if not isinstance(text,str) or len(text)>PARTY_NOTICE_LIMIT:return False
    offsets={0}
    for index in range(len(text)):
        if index in offsets:
            offsets.update(index+len(word) for word in PARTY_NOTICE_WORDS if text.startswith(word,index))
    return len(text) in offsets

class Reader:
    def __init__(self,b): self.b=b;self.p=1
    def take(self,n):
        if(n<0 or self.p+n>len(self.b)): raise ValueError('truncated packet')
        value=self.b[self.p:self.p+n];self.p+=n;return value
    def u8(self): return self.take(1)[0]
    def u16(self): return int.from_bytes(self.take(2),'big')
    def u32(self): return int.from_bytes(self.take(4),'big')
    def string(self): return self.take(self.u8()).split(b'\0',1)[0].decode('cp949','replace')
    def string16(self):return self.take(self.u16()).split(b'\0',1)[0].decode('cp949','replace')

def merc_state_block(r,selector):
    if selector==1:return [r.u8() for _ in range(5)]+[r.u16()]
    if selector in (2,3):return [r.u32() for _ in range(3)]
    raise ValueError('unknown merc state selector')

def decode_clan_status(r, *, redact=False):
    mode=r.u8()
    if mode not in (0,1) or len(r.b)==2:return {'kind':'clan_info_response','mode':mode}
    def name_size():
        count=r.u8()
        start=r.p
        r.take(count)
        if redact:r.b[start:r.p]=bytes(count)
        return count
    event=dict(kind='clan_status',mode=mode,nameBytes=name_size(),masterBytes=name_size(),
               grade=r.u8(),level=r.u16(),galrid=r.u32())
    if mode==1:event['roster']=[dict(nameBytes=name_size(),flag=r.u8()) for _ in range(r.u16())]
    if r.b[r.p:] not in (b'',b'\0'):raise ValueError('unexpected clan status suffix')
    return decoded_fields(r,event)

def decode_cash_shop(r, *, redact=False):
    mode=r.u8()
    if mode!=0:return {'kind':'cash_shop_response','mode':mode}
    def private_bytes(size):
        start=r.p
        r.take(size)
        if redact:r.b[start:r.p]=bytes(size)
        return size
    rows=[]
    for _ in range(r.u8()):
        private_bytes(8)
        sprite=r.u16()
        name_bytes=[private_bytes(r.u8()),private_bytes(r.u8())]
        quantity=r.u32()
        description_bytes=private_bytes(r.u16()) if r.u8() else None
        date_bytes=private_bytes(r.u8())
        private_bytes(4)
        refund_flag=r.u8()
        private_bytes(4)
        rows.append(dict(spriteFrame=sprite,quantity=quantity,nameBytes=name_bytes,
                         descriptionBytes=description_bytes,dateBytes=date_bytes,refundFlag=refund_flag))
    if r.b[r.p:] not in (b'',b'\0'):raise ValueError('unexpected cash shop list suffix')
    return decoded_fields(r,{'kind':'cash_shop_list','mode':mode,'entries':rows})

def decode_drag_drop_items(r, group, mode, *, redact=False):
    # The v611 bank and mercenary readers are 004D6718 and 004D67EA.
    bank=group==2
    def skip_text(size):
        start=r.p
        r.take(size)
        if redact:r.b[start:r.p]=bytes(size)

    def item():
        slot,sprite=r.u8(),r.u16()
        if bank and not 1<=slot<=100:raise ValueError('invalid bank slot')
        skip_text(r.u8())
        skip_text(r.u8())
        row=dict(slot=slot,wireSprite=sprite,quantity=r.u32(),word1=r.u16(),word2=r.u16())
        if not bank:row['mercFlags']=[r.u8(),r.u8()]
        row['stackFlag']=r.u8()
        if not bank:row['mercWords']=[r.u16(),r.u16()]
        if r.u8():skip_text(r.u16())
        row.update(durabilityByte=r.u8(),remainingMinutes=r.u32(),itemFlag=r.u8())
        return row

    if mode==0:
        oid=r.u32() if bank else None
        count=r.u8()
        if bank and count>100:raise ValueError('too many bank slots')
        event=dict(kind='bank_open' if bank else 'merc_inventory_header',group=group,mode=mode,
                   count=count,entries=[item() for _ in range(count)])
        if bank:event['openingWord']=oid
    else:
        event=dict(kind='bank_slot' if bank else 'merc_inventory_slot',group=group,mode=mode,**item())
    if r.b[r.p:] not in (b'',b'\0'):raise ValueError('invalid drag-and-drop packet suffix')
    return decoded_fields(r,dict(event,textRedacted=True))


def decode_web_board(r, *, redact=False):
    mode=r.u8()
    sizes=[]
    for _ in range(2 if mode==5 else 1):
        size=r.u8()
        start=r.p
        r.take(size)
        if redact:r.b[start:r.p]=bytes(size)
        sizes.append(size)
    if r.b[r.p:] not in (b'',b'\0'):raise ValueError('unexpected web board suffix')
    return decoded_fields(r,dict(kind='web_board',mode=mode,stringBytes=sizes,textRedacted=True))


def sanitized_packet(data, *, decoration_keys=None):
    """Repeat source-side privacy policy before anything reaches the disk."""
    if not data or data[0] not in SERVER_OPCODES:return None
    if data[0]==0x3B and len(data)!=3:raise ValueError('unexpected world CRC length')
    if data[0]==0x58 and len(data) not in (2,3):raise ValueError('unexpected S58 probe length')
    if data[0]==0x2F and len(data)>=2 and data[1] in MERCHANT_LIST_MODES:
        event=decode(data)
        if event.get('layout')=='raw_pending_decode':return None
        end=event['nativeBytesConsumed']
        return data[:end+(data[end:end+1]==b'\0')]
    if data[0]==0x3D:
        safe=bytearray(data)
        event=decode_clan_status(Reader(safe),redact=True)
        return bytes(safe if event['mode'] in (0,1) else safe[:2])
    if data[0]==0x45:
        safe=bytearray(data)
        event=decode_cash_shop(Reader(safe),redact=True)
        return bytes(safe if event['mode']==0 else safe[:2])
    if data[0]==0x5B:
        event=decode(data)
        return data[:3]+bytes(event['textBytes'])+data[3+event['textBytes']:]
    if data[0]==0x62:
        safe=bytearray(data)
        decode_web_board(Reader(safe),redact=True)
        return bytes(safe)
    if data[0]==0x64:
        if len(data)<3 or data[1]!=3 or data[2]>8:return None
        safe=bytearray(data)
        reader=Reader(safe)
        reader.p=3
        decode_room(reader,data[2],redact=True,decoration_keys=decoration_keys)
        if safe[reader.p:] not in (b'',b'\0'):raise ValueError('unexpected room packet suffix')
        return bytes(safe)
    if data[0]==0x67 and len(data)!=7:raise ValueError('unexpected room transition length')
    if data[0]==0x51:
        if len(data) not in (3,5):raise ValueError('unexpected client block length')
        return bytes(data[:3])
    if data[0]==0x61:
        if len(data)<3 or data[1] not in (1,2,3) or data[2]>(3 if data[1]==2 else 2):
            raise ValueError('unexpected drag-and-drop selectors')
        if data[1]==2 and len(data)>3:
            prefix=(8,6,4,12)[data[2]]
            if len(data)<prefix:raise ValueError('truncated bank fields')
            if data[2] in (0,1) and len(data)>prefix:
                safe=bytearray(data)
                reader=Reader(safe)
                reader.p=3
                try:
                    decode_drag_drop_items(reader,2,data[2],redact=True)
                    return bytes(safe)
                except ValueError:
                    pass
            return bytes(data[:prefix])
        if data[1]==3:
            mode=data[2]
            if mode in (0,1):
                prefix=4 if mode==0 else 3
                if len(data)>prefix:
                    safe=bytearray(data)
                    reader=Reader(safe)
                    reader.p=3
                    try:
                        decode_drag_drop_items(reader,3,mode,redact=True)
                        return bytes(safe[:reader.p])
                    except ValueError:
                        pass
                return bytes(data[:prefix])
            if mode==2 and len(data) in (7,8):return bytes(data[:7])
        return bytes(data[:3])
    if data[0]==0x82:
        if len(data)<2:raise ValueError('missing pet mode')
        if data[1]!=4:return bytes(data[:2])
        if len(data)!=3 and not (len(data)==4 and data[3]==0):raise ValueError('invalid pet slot removal length')
        return bytes(data)
    b=bytearray(data)
    def zero(pos):
        if pos>=len(b):raise ValueError('truncated string')
        n=b[pos];pos+=1
        if pos+n>len(b):raise ValueError('truncated string body')
        b[pos:pos+n]=bytes(n);return pos+n
    if b[0]==0x33:
        pos=zero(39)
        if pos>=len(b):raise ValueError('missing alias count')
        count=b[pos];pos+=1
        for _ in range(count):pos=zero(pos+1)
    elif b[0]==0x34:
        end=zero(zero(6))+35
        if b[end:] not in (b'',b'\0') or end>len(b):raise ValueError('invalid object info length')
    elif b[0]==0x39:
        zero(zero(2))
    elif b[0]==0x69:
        _,_,_,entries,suffix=human_info(b)
        for _,pos,_ in entries:zero(pos)
        if suffix not in (b'',b'\0'):raise ValueError('unknown human info suffix')
    elif b[0]==0x30:
        dialog=decode(b)
        b[11:15]=bytes(4)
        del b[dialog['nativeBytesConsumed']:]
    elif b[0]==0x4F:
        if b[1:3]==b'\x02\x00' and len(b) in (7,8):
            return bytes(b[:7])
        elif b[1:3]==b'\x02\x01' and len(b)>=4 and len(b)>=35+b[3]:
            name_end=4+b[3]
            return bytes(b[:4])+bytes(b[3])+bytes(b[name_end:name_end+31])
        elif b[1:3]==b'\x02\x02' and len(b)>=5:
            try:
                event=decode(b)
                if len(b)-event['nativeBytesConsumed']<=1:
                    return bytes(b)
            except ValueError:
                pass
            return bytes(b[:3])
        elif b[1:3]==b'\x02\x0b' and len(b) in (5,6):
            return bytes(b[:5])
        elif b[1:3]==b'\x02\x03':
            event=decode(b)
            if event['trailingBytes'] not in ('','00'):raise ValueError('unexpected merc equipment suffix')
            # Preserve the catalog item name and stats, never a display alias.
            zero(7+b[6])
        elif b[1:3]==b'\x02\x0C':
            if len(b)!=8 or b[-1]!=0:raise ValueError('unexpected merc control length')
        elif b[1:2]==b'\x03':
            if len(b)!=10:raise ValueError('unexpected merc dialog length')
        else:
            return bytes(b[:3] if b[1:2]==b'\x02' else b[:2])
    elif b[0]==0x5D and len(b)>1 and b[1]==0:
        end=zero(3)+2
        if end>len(b) or b[end:] not in (b'',b'\0'):raise ValueError('unexpected party fields')
    elif b[0]==0x6A:
        r=Reader(b);mode=r.u8()
        if mode==0:
            for _ in range(r.u8()):r.p=zero(r.p);r.u8()
        elif mode in (1,2):r.p=zero(r.p)
        else:return None
        if any(b[r.p:]):raise ValueError('unexpected friend list fields')
    elif b[0]==7:
        r=Reader(b);count=r.u16()
        for _ in range(count):
            r.take(13);namepos=r.p;r.string();category=r.u8()
            if category in (2,6,7):zero(namepos);r.p=zero(r.p)
            elif category==3:r.u32()
            elif category not in (0,1):zero(namepos)
    return bytes(b)

STAT_LINE=re.compile(r'\{=h([^{}|]+?)\s*\{=f([+-]?\d+)(?:([+-])(\d+))?\{=h([^{}|]*)\{=a')

def fishing_bait_remaining(name, sprite):
    if sprite!=0x8D44 or not isinstance(name,str):
        return None
    match=re.fullmatch(r'지렁이병\(대\) \[([0-9]{1,3}) 마리\]',name)
    if match and (remaining:=int(match[1]))<=200:
        return remaining
    return None


def item_stats(text):
    result=[]
    for name,value,sign,extra,condition in STAT_LINE.findall(text):
        name=name.strip()
        field={'name':name,'condition':condition.strip()}
        if name=='공격력' and sign=='-':
            field.update(minimum=int(value),maximum=int(extra))
        else:
            field.update(base=int(value),modifier=(int(sign+extra) if sign else 0))
        result.append(field)
    return result

def ability_entry(r,full):
    entry={'slot':r.u8(),'category':r.u8(),'headerWords':[r.u16(),r.u16()],
           'flags':r.u8(),'name':r.string(),'kindId':r.u8(),'value':r.u16()}
    if full:entry['additionalWords']=[r.u16(),r.u16()]
    return entry


def spell_entry(r,full):
    entry={'slot':r.u8(),'word1':r.u16(),'flags':r.u8(),'name':r.string(),'text':r.string(),
           'byte1':r.u8(),'word2':r.u16(),'additionalBytes':list(r.take(8))}
    if full:entry['additionalWords']=[r.u16(),r.u16()]
    return entry


def decoded_fields(r,event):
    event['nativeBytesConsumed']=r.p
    event['trailingBytes']=r.b[r.p:].hex()
    return event


def human_info(b):
    r=Reader(b)
    mode,oid,mask=r.u8(),r.u32(),r.u8()
    if mode not in (1,2):raise ValueError('unknown human info mode')
    entries=[]
    for bit,start,maximum in HUMAN_INFO_GROUPS:
        if not mask&bit:continue
        count=r.u8() if maximum>1 else 1
        if count>maximum:raise ValueError('too many human info entries')
        for slot in range(start,start+count):
            pos=r.p
            r.take(r.u8())
            entries.append((slot,pos,r.u8() if mode==1 else None))
    return mode,oid,mask,entries,b[r.p:]


EXTENDED_PACKET_KINDS={0x17:'spell',0x18:'spell_remove',0x1E:'world_day',0x27:'combat_mode',0x30:'npc_pursuit',0x34:'object_info',0x39:'self_look',0x3A:'timed_spell',0x3B:'crc_challenge',
    0x4F:'merc',0x52:'cast_spell',0x5D:'party',0x62:'web_board',0x64:'room_exit_menu',0x82:'pet_slot_remove',
                       0x55:'job_list',0x69:'human_info',0x6F:'metadata'}


def decode_room(r,selector,*,redact=False,decoration_keys=None):
    def entry(has_id,has_placement):
        fields={'entryId':r.u32()} if has_id else {}
        size=r.u8()
        start=r.p
        label=bytes(r.take(size))
        key=decoration_keys.get(label) if decoration_keys else None
        if key is not None:fields['catalogKey']=key
        elif redact:r.b[start:r.p]=b'\0'*size
        fields['labelBytes']=size
        if has_placement:fields['placement']=list(r.take(3))
        return fields

    event={'mode':3,'selector':selector}
    if selector==0:
        flag,count=r.u8(),r.u8()
        if flag>1:raise ValueError('unknown room control flag')
        event.update(kind='room_controls',flag=flag,count=count)
        if count:event['entries']=[entry(False,True) for _ in range(count)]
    elif selector in (1,2):
        event['kind']='room_exit_menu'
        if selector==1:event['destinationIds']=[r.u16() for _ in range(r.u8())]
    elif selector in (3,4,5):
        event.update(kind='room_furniture_list',entries=[entry(True,selector==4) for _ in range(r.u8())])
    elif selector in (6,7):
        event.update(kind='room_furniture_update',**entry(False,True))
    elif selector==8:
        event.update(kind='room_status',level=r.u16(),ratings=[r.u16() for _ in range(5)],today=r.u16(),total=r.u32())
    else:raise ValueError('unknown room selector')
    return decoded_fields(r,event)


def decode(b, *, decoration_keys=None):
    r=Reader(b);op=b[0]
    if op==0x3B:
        return decoded_fields(r,{'kind':'crc_challenge','challenge':r.u16()})
    if op==0x3D:return decode_clan_status(r)
    if op==0x45:return decode_cash_shop(r)
    if op==0x58:
        return {'kind':'s58_probe','layout':'unregistered_v611','opaqueBytes':list(b[1:])}
    if op==0x62:return decode_web_board(r)
    if op==0x5B:
        # 004D4BB4 reads Str16, two words and one byte.
        size=r.u16()
        r.take(size)
        event={'kind':'advertise_info','textBytes':size,'textRedacted':True,'words':[r.u16(),r.u16()],'value':r.u8()}
        if b[r.p:] not in (b'',b'\0'):raise ValueError('unexpected advertise info suffix')
        return decoded_fields(r,event)
    if op==0x64:
        if r.u8()!=3:raise ValueError('unknown room layout')
        return decode_room(r,r.u8(),decoration_keys=decoration_keys)
    if op==0x67:
        return {'kind':'room_transition_probe','layout':'raw_pending_decode','opaqueBytes':list(r.take(6))}
    if op==0x27:
        return decoded_fields(r,{'kind':'combat_mode','oid':r.u32(),'mode':r.u8()})
    if op==0x82:
        mode=r.u8()
        if mode==4:return decoded_fields(r,{'kind':'pet_slot_remove','mode':mode,'slot':r.u8()})
        return {'kind':'pet_packet','mode':mode,'layout':'raw_pending_decode'}
    if op==0x17:
        # SAddSpellPacket 004D48A9: individual entry, update, or full list.
        mode=r.u8();event={'kind':'spell','mode':mode}
        if mode==1:event['entries']=[spell_entry(r,False)]
        elif mode==2:event.update(slot=r.u8(),word1=r.u16(),word2=r.u16())
        elif mode==3:event['entries']=[spell_entry(r,True) for _ in range(r.u8())]
        else:event['layout']='raw_pending_decode'
        return decoded_fields(r,event)
    if op==0x18:
        return decoded_fields(r,{'kind':'spell_remove','slot':r.u8()})
    if op==0x52:
        return decoded_fields(r,{'kind':'cast_spell','oid':r.u32(),'mode':r.u8()})
    if op==0x39:
        # SSelfLookPacket 004DDDF6 uses two Str8s and a byte-pair vector.
        mode=r.u8()
        name_bytes=len(r.take(r.u8()));title_bytes=len(r.take(r.u8()))
        event={'kind':'self_look','mode':mode,'nameBytes':name_bytes,'titleBytes':title_bytes,
               'textRedacted':True,'byte1':r.u8()}
        event['bytePairs']=[list(r.take(2)) for _ in range(r.u8())]
        event.update(byte2=r.u8(),dwords=[r.u32(),r.u32()],bytesAfterDwords=list(r.take(2)),
                     dword=r.u32(),finalBytes=list(r.take(6)))
        event['characterPanelLevel'],event['characterPanelPercent']=event['bytesAfterDwords']
        if mode==0 and event['byte1']==4:
            event['characterPanelExperience']=event['dword']
            _,total,first,second,third,available=event['finalBytes']
            event['fatePoints']={'total':total,'available':available,'allocated':[first,second,third]}
        return decoded_fields(r,event)
    if op==0x55:
        # SUpdateJobListPacket 004DE47B: count, then a byte and Str8 per row.
        return decoded_fields(r,{'kind':'job_list','entries':[{'code':r.u8(),'name':r.string()} for _ in range(r.u8())]})
    if op==0x6F:
        # SMetaDataPacket 004DBFFF: file (0) or catalog (1). Keep the compressed
        # body in the existing raw record and report only its size in the fields.
        mode=r.u8();event={'kind':'metadata','mode':mode}
        if mode==0:
            event.update(name=r.string(),crc32=r.u32(),compressedSize=r.u16())
            r.take(event['compressedSize'])
        elif mode==1:event['entries']=[{'name':r.string(),'crc32':r.u32()} for _ in range(r.u16())]
        else:event['layout']='raw_pending_decode'
        return decoded_fields(r,event)
    if op==0x37:
        # Native SAddEquipmentPacket codec 004D459C.
        event={'kind':'equipment_add','slot':r.u8(),'wireSprite':r.u16(),'name':r.string(),'displayName':r.string()}
        event['hasDescription']=r.u8()
        if event['hasDescription']:
            event['description']=r.string16()
            event['statFields']=item_stats(event['description'])
        event['word1']=r.u16();event['word2']=r.u16();event['dword']=r.u32();event['flag']=r.u8()
        event['nativeBytesConsumed']=r.p;event['trailingBytes']=b[r.p:].hex();return event
    if op==0x0F:
        # Native SAddInventoryPacket codec 004D473A. Field meanings not inferred
        # solely from their size; keep unknown words/flags available for comparison.
        event={'kind':'inventory_add','slot':r.u8(),'wireSprite':r.u16(),'name':r.string(),'displayName':r.string(),
               'quantity':r.u32(),'word1':r.u16(),'word2':r.u16(),'byte1':r.u8(),'byte2':r.u8(),'flag1':r.u8(),
               'word3':r.u16(),'word4':r.u16(),'hasDescription':r.u8()}
        if event['hasDescription']:
            event['description']=r.string16()
            event['statFields']=item_stats(event['description'])
        event['byte3']=r.u8();event['dword']=r.u32();event['flag2']=r.u8()
        if event['byte3']<=100:event['durabilityPercent']=event['byte3']
        event['nativeBytesConsumed']=r.p;event['trailingBytes']=b[r.p:].hex();return event
    if op==0x10:
        event={'kind':'inventory_remove','slot':r.u8(),'byte2':r.u8(),'word':r.u16()}
        event['nativeBytesConsumed']=r.p;event['trailingBytes']=b[r.p:].hex();return event
    if op==0x38:
        return decoded_fields(r,{'kind':'equipment_remove','slot':r.u8(),'refreshWord':r.u16()})
    if op==0x4F:
        # SMercPacket 004D92B2.
        mode=r.u8();event={'kind':'merc','mode':mode}
        if mode==3:
            event.update(record='object_dialog',targetId=r.u32(),flag=r.u8(),dialogMode=r.u8(),value=r.u16())
        elif mode==2:
            subtype=r.u8();event['subtype']=subtype
            if subtype==0 and len(b)>=7:
                event.update(record='binding',targetId=r.u32())
            elif subtype==1 and len(b)>=4 and len(b)>=35+b[3]:
                name_bytes=len(r.take(r.u8()))
                event.update(record='profile_stats',nameBytes=name_bytes,
                             blocks=[dict(selector=i,values=merc_state_block(r,i)) for i in (1,2,3)])
            elif subtype==2 and len(b)>=5:
                event.update(record='state',controlMode=r.u8(),blocks=[])
                while (selector:=r.u8())!=0:
                    event['blocks'].append(dict(selector=selector,values=merc_state_block(r,selector)))
            elif subtype==3:
                event.update(record='equipment',slot=r.u8(),wireSprite=r.u16(),name=r.string())
                event['displayNameBytes']=len(r.take(r.u8()))
                event['hasDescription']=r.u8()
                if event['hasDescription']:
                    event['description']=r.string16()
                    event['statFields']=item_stats(event['description'])
                event.update(itemByte=r.u8(),itemDword=r.u32(),itemFlag=r.u8())
            elif subtype==11 and len(b)>=5:event.update(record='control_values',values=[r.u8(),r.u8()])
            elif subtype==12:event['record']='control'
            else:event['layout']='raw_pending_decode'
        else:event['layout']='raw_pending_decode'
        return decoded_fields(r,event)
    if op==0x85:
        # SComboBonusPacket 004D608C and the combo window 004326AE.
        return decoded_fields(r,{'kind':'combo','count':r.u8(),'bonusCounter':r.u16(),'skillLevel':r.u32(),
                                 'levelBonus':r.u8()*10,'monsterLevel':r.u32(),'secondsLeft':r.u16(),'secondsLimit':r.u16()})
    if op==8:
        mask=r.u8();event={'kind':'status','mask':mask,'state':r.u8(),'ghost':bool(mask&0x80),'flag1':bool(mask&2),'flag0':bool(mask&1)}
        if mask&0x40:
            event['baseBytes']=list(r.take(4));event['maximumValues']=[r.u32() for _ in range(4)]
            event['baseWords']=[r.u16() for _ in range(6)];event['baseDword']=r.u32();event['baseWord']=r.u16();r.take(2)
            event['characterLevel'],event['fame']=event['baseBytes'][3],event['baseDword']
            event['remainingBaseWords']=[r.u16() for _ in range(3)]
            event['physicalDefense'],event['magicDefense']=event['remainingBaseWords'][1:]
            event['maxSatiety']=event['maximumValues'][2]
        if mask&0x20:
            event['currentValues']=[r.u32() for _ in range(4)]
            event['satiety']=event['currentValues'][2]
        if mask&0x10:event['gold']=r.u32()
        if mask&8:
            event['extraBytes']=list(r.take(5));r.take(1);event['extraDword']=r.u32();event['extraWord']=r.u16()
        return event
    if op==0x2C:
        # SAddAbilityPacket 004D42EB: full list (3), single entry (1), progress update (2).
        mode=r.u8()
        event={'kind':'ability','mode':mode}
        if mode==1:event['entries']=[ability_entry(r,False)]
        elif mode==2:event.update(slot=r.u8(),value=r.u16(),referenceWord=r.u16())
        elif mode==3:event['entries']=[ability_entry(r,True) for _ in range(r.u8())]
        else:event['layout']='raw_pending_decode'
        event['nativeBytesConsumed']=r.p;event['trailingBytes']=b[r.p:].hex()
        return event
    if op==0x2F:
        # SMerchantPacket 004DB6B1. Keep the full portrait header and decode
        # the tail by mode and flags before pairing the reply with a request.
        mode=r.u8()
        event={'kind':'merchant_dialog','mode':mode,'flags':r.u8(),'oid':r.u32(),
               'unknown':r.u8(),'sprite':r.u16(),'color':r.u8(),'headerData':r.u32(),'text':r.string16()}
        if mode in (0,1):
            if mode==1:event['prompt']=r.string()
            event['options']=[{'text':r.string(),'id':r.u16()} for _ in range(r.u8())]
        elif mode==12:
            event['actionCode']=r.u16()
            if event['actionCode'] in (0x83,0x85,0x86):event['actionText']=r.string()
        elif mode==13:
            event['x']=r.u16();event['y']=r.u16();event['menuCodes']=[r.u16() for _ in range(4)]
        elif mode in (11,14):
            event['menuCode']=r.u16()
            event['offers']=[]
            for _ in range(r.u8()):
                kind=r.u8() if mode==14 else 0
                if kind not in (0,1):raise ValueError('unknown repair item kind')
                offer={'kind':kind,'slot':r.u8(),'price':r.u32()}
                if kind==1:offer['name']=r.string()
                event['offers'].append(offer)
        elif mode==10 and event['flags'] in (1,14):
            extended=event['flags']==14
            event['menuCode']=r.u16()
            event['items']=[]
            for _ in range(r.u16()):
                item={'id':r.u32()} if extended else {}
                item.update(sprite=r.u16(),price=r.u32(),flags=r.u8(),name=r.string())
                if extended:
                    item['displayName']=item['name']
                    item['hasDescription']=r.u8()
                    item['description']=r.string16() if item['hasDescription'] else None
                    item['tailByte']=r.u8();item['tailDword']=r.u32()
                else:
                    item['displayName']=r.string();item['description']=r.string()
                event['items'].append(item)
        else:event['layout']='raw_pending_decode'
        event['nativeBytesConsumed']=r.p
        event['trailingBytes']=b[r.p:].hex()
        return event
    if op==0x30:
        # SPursuitPacket at 004DD184, separate from the merchant menu.
        mode,flags,oid=r.u8(),r.u8(),r.u32()
        portrait,sprite,color=r.u8(),r.u16(),r.u8()
        r.take(4)
        event={'kind':'npc_pursuit','mode':mode,'flags':flags,'oid':oid,
               'portrait':portrait,'sprite':sprite,'color':color,
               'script':r.u16(),'page':r.u16(),'back':r.u8(),'next':r.u8()}
        if mode in (0,2,4):event['text']=r.string16()
        if mode in (2,3):event['options']=[r.string() for _ in range(r.u8())]
        if mode in (4,5):event.update(prompt=r.string(),inputLimit=r.u8(),inputHint=r.string())
        if mode>5:raise ValueError('unknown NPC dialog mode')
        return decoded_fields(r,event)
    if op==0x3A:
        return decoded_fields(r,{'kind':'timed_spell','id':r.u16(),'name':r.string(),'duration':r.u16()})
    if op==0x15:
        return {'kind':'map','map':r.u16(),'width':r.u16(),'height':r.u16(),
                'flags':r.u8(),'unknown':r.u8(),'name':r.string()}
    if op==4:
        return decoded_fields(r,{'kind':'self_position','x':r.u16(),'y':r.u16(),'floor':r.u8(),
                'cameraX':r.u16(),'cameraY':r.u16()})
    if op==5:return decoded_fields(r,{'kind':'self_identity','oid':r.u32(),'unknown':r.u16(),
                'flags':r.u8(),'direction':r.u8()})
    if op==0x0B:
        direction=r.u8();x=r.u16();y=r.u16();floor=r.u8();cx=r.u16();cy=r.u16();seq=r.u8()
        dx,dy=DELTAS[direction] if direction<8 else (0,0)
        return {'kind':'self_move','direction':direction,'wireX':x,'wireY':y,'x':x+dx,'y':y+dy,
                'floor':floor,'cameraX':cx,'cameraY':cy,'sequence':seq,'correction':direction==8}
    if op==0x33:
        event={'kind':'human','x':r.u16(),'y':r.u16(),'floor':r.u8(),'direction':r.u8(),
               'oid':r.u32(),'flag1':r.u8(),'flag2':r.u8()}
        event['appearance']=r.take(6).hex();event['equipment']=[r.u16() for _ in range(10)]
        r.string();event['namesRedacted']=True;return event
    if op==7:
        count=r.u16();objects=[]
        for _ in range(count):
            obj={'x':r.u16(),'y':r.u16(),'floor':r.u8(),'oid':r.u32(),'sprite':r.u16(),
                 'flags':r.u8(),'type':r.u8(),'name':r.string(),'category':r.u8()}
            if obj['category'] in (2,6,7):r.string();obj['name']='';obj['textRedacted']=True
            elif obj['category']==3:obj['quantity']=r.u32()
            elif obj['category'] not in (0,1):obj['name']='';obj['classification']='unresolved_native_category'
            objects.append(obj)
        event={'kind':'objects','objects':objects,'layout':'existing_capture_decoder'}
        if r.b[r.p:] in (b'',b'\0') and all(obj['category'] in (0,1,2,3,6,7) for obj in objects):
            return decoded_fields(r,event)
        return event
    if op==0x0C:return {'kind':'object_move','oid':r.u32(),'x':r.u16(),'y':r.u16(),'floor':r.u8(),'direction':r.u8(),'animation':r.u8()}
    if op==0x0E:return {'kind':'object_remove','oid':r.u32()}
    if op==0x11:return {'kind':'direction','oid':r.u32(),'direction':r.u8()}
    if op==0x19:
        v=r.u16();return {'kind':'sound','id':v&0x7FFF,'bgm':bool(v&0x8000)}
    if op==0x14:return decoded_fields(r,{'kind':'map_check','phase':r.u8()})
    if op==0x13:
        e={'kind':'damage_effect','attacker':r.u32(),'target':r.u32(),'animation':r.u8(),'effect':r.u8(),
           'words':[r.u16() for _ in range(4)],'extraByte':r.u8(),'flags':r.u8()}
        # 0047C1A4 and 0043B1EC/0043B262 dispatch two independent sounds and the floating number.
        e['motionId'],e['swingSoundId']=e['words'][:2]
        if e['flags']&1:
            e['extraWords']=[r.u16() for _ in range(3)]
            _,e['displayedDamage'],e['impactSoundId']=e['extraWords']
        return e
    if op==0x1A:return {'kind':'motion','oid':r.u32(),'mode':r.u8(),'words':[r.u16() for _ in range(3)]}
    if op==0x5F:return {'kind':'death','oid':r.u32()}
    if op==0x29:return {'kind':'effect','oid':r.u32(),'effect':r.u16(),'parameter':r.u16()}
    if op==0x5D:
        mode=r.u8();event={'kind':'party','mode':mode}
        if mode in (0,1,2):event['slot']=r.u8()
        if mode==0:event['nameBytes']=len(r.take(r.u8()));event['nameRedacted']=True
        if mode in (0,1):event['memberValue']=r.u8()
        if mode==0:event['leader']=bool(r.u8())
        if mode in (4,5):event['enabled']=bool(r.u8())
        return decoded_fields(r,event)
    if op==0x6A:
        mode=r.u8();event={'kind':'friend_list','mode':mode}
        if mode==0:
            count=r.u8();statuses=[];name_lengths=[]
            for _ in range(count):
                name_lengths.append(len(r.take(r.u8())))
                statuses.append(r.u8())
            event.update(count=count,status=statuses,nameLengths=name_lengths)
        elif mode in (1,2):event['nameBytes']=len(r.take(r.u8()))
        return decoded_fields(r,event)
    if op==0x53:return {'kind':'pickup','itemId':r.u32(),'pickerId':r.u32()}
    if op==0x34:
        oid,kind=r.u32(),r.u8()
        title_bytes,job_bytes=len(r.take(r.u8())),len(r.take(r.u8()))
        badge,kind_value=r.u16(),r.u8()
        return decoded_fields(r,{'kind':'object_info','oid':oid,'flags':kind,'titleBytes':title_bytes,'jobBytes':job_bytes,
            'badge':badge,'kindValue':kind_value,'equipment':[r.u16() for _ in range(15)],'status':[r.u8(),r.u8()]})
    if op==0x69:
        mode,oid,mask,entries,suffix=human_info(b)
        return {'kind':'human_info','mode':mode,'oid':oid,'mask':mask,'entries':[
            {'slot':slot,'textBytes':b[pos],**({'active':bool(flag)} if flag is not None else {})} for slot,pos,flag in entries],
            'nativeBytesConsumed':len(b)-len(suffix),'trailingBytes':suffix.hex()}
    if op==0x1E:
        # The in-world hourly notification has only a day. The login layout
        # supplies the month and day required by the original client codec.
        if len(b)==2:return {'kind':'world_day_tick','day':r.u8()}
        month=r.u8()
        return decoded_fields(r,{'kind':'world_day','month':month,'season':month,'day':r.u8()})
    if op==0x1F:return {'kind':'weather','weatherType':r.u8(),'intensity':r.u8()}
    if op==0x20:
        event=decoded_fields(r,{'kind':'world_hour','hour':r.u8()})
        if len(b)>=3:event['minute']=b[2]
        return event
    if op==0x51:return {'kind':'client_block','selector':r.u8(),'effectIndex':r.u8()}
    if op==0x61:
        group,mode=r.u8(),r.u8()
        if group==2 and len(b)>3:
            if mode in (0,1) and len(b)>(8 if mode==0 else 6):
                return decode_drag_drop_items(r,2,mode)
            if mode==0:return {'kind':'bank_open','group':group,'mode':mode,'openingWord':r.u32(),'count':r.u8()}
            if mode==1:return {'kind':'bank_slot','group':group,'mode':mode,'slot':r.u8(),'wireSprite':r.u16()}
            if mode==2:return {'kind':'bank_remove','group':group,'mode':mode,'slot':r.u8()}
            if mode==3:return {'kind':'bank_quote','group':group,'mode':mode,'slot':r.u8(),'quoteWord':r.u32(),'fee':r.u32()}
        if group==3 and mode==0 and len(b)>=4:
            if len(b)>4:return decode_drag_drop_items(r,3,mode)
            return {'kind':'merc_inventory_header','group':group,'mode':mode,'count':r.u8()}
        if group==3 and mode==1 and len(b)>3:return decode_drag_drop_items(r,3,mode)
        if group==3 and mode==2 and len(b)>=7:
            return decoded_fields(r,dict(kind='merc_inventory_delta',group=group,mode=mode,
                                         slotByte=r.u8(),modeByte=r.u8(),word=r.u16()))
        return {'kind':'drag_drop_header','group':group,'mode':mode}
    if op==0x6B:return {'kind':'teleport_menu','flags':r.u8()}
    names={0x6C:'warp'}
    return {'kind':names.get(op,'packet'),'layout':'raw_pending_decode'}

def metadata_blocks(data, section=None):
    if len(data)<8:raise ValueError('Meta.dat header truncated')
    count,index_size=struct.unpack_from('<ii',data);idx=8;pos=8+index_size
    if not(0<=count<=4096 and 8<=pos<=len(data)):raise ValueError('Meta.dat invalid index')
    for _ in range(count):
        if idx>=8+index_size:raise ValueError('Meta.dat short index')
        n=data[idx];idx+=1
        if idx+n*2+4>8+index_size:raise ValueError('Meta.dat short name')
        name=data[idx:idx+n*2].decode('utf-16le');idx+=n*2
        size=int.from_bytes(data[idx:idx+4],'big');idx+=4
        if size<1 or pos+size>len(data):raise ValueError('Meta.dat truncated block')
        if section is None or name==section:
            decoder=zlib.decompressobj();raw=decoder.decompress(data[pos:pos+size],1024*1024+1)
            if len(raw)>1024*1024 or not decoder.eof:raise ValueError('Meta.dat oversized or invalid block')
            yield name,size,raw
        pos+=size
    if idx!=8+index_size or pos!=len(data):raise ValueError('Meta.dat trailing bytes')


def decoration_catalog(path):
    """Read canonical furniture keys and the corresponding local client metadata."""
    blocks=list(metadata_blocks(path.read_bytes(),section='Decorations'))
    if not blocks:return {}
    if len(blocks)!=1:raise ValueError('Duplicate Decorations section')
    raw=blocks[0][2]
    reader=Reader(raw);reader.p=0
    rows={}
    for _ in range(reader.u16()):
        key=bytes(reader.take(reader.u8()))
        if reader.u16()!=13 or key in rows:raise ValueError('Invalid Decorations row')
        fields=[reader.string16() for _ in range(13)]
        name=key.decode('cp949')
        if not re.fullmatch(r'(?:가구|바닥장식|벽장식)-[0-9]{4}|(?:바닥|벽)-[0-9]{4}\([LMS]\)',name):
            continue
        rows[key]={'catalogKey':name,'displayName':fields[12],'palette':fields[1],
                   'models':[f'D{model}.DEC' for model in fields[6:10]
                             if len(model)==7 and model.isascii() and model.isdecimal()]}
    if reader.p!=len(raw):raise ValueError('Decorations trailing bytes')
    return rows


def collect_assets(client_dir,output):
    """Read only Maps headers/hashes and public Meta.dat; no account profile files."""
    client_dir=Path(client_dir);output=Path(output);output.mkdir(parents=True,exist_ok=True)
    maps=[]
    for path in sorted((client_dir/'Maps').glob('*.map')):
        with path.open('rb') as f:
            header=f.read(4);f.seek(0);digest=hashlib.file_digest(f,'sha256').hexdigest()
        if len(header)!=4:continue
        maps.append({'file':path.name,'mapFileId':int(path.stem) if path.stem.isdecimal() else None,
                     'header':header.hex(),'sectorsX':header[0],'sectorsY':header[1],
                     'width':header[2],'height':header[3],'bytes':path.stat().st_size,'sha256':digest})
    dump(output/'maps-index.json',maps)
    meta=client_dir/'Profiles'/'Meta.dat';entries=[]
    if meta.exists():
        data=meta.read_bytes()
        for name,size,raw in metadata_blocks(data):
            entries.append({'name':name,'bytes':len(raw),'compressedBytes':size,'crc32':f'{zlib.crc32(raw):08x}'})
        (output/'Meta.dat').write_bytes(data);dump(output/'metadata-index.json',entries)
    dump(output/'manifest.json',{'clientDirectory':str(client_dir),'collectedAt':now(),'mapFiles':len(maps),
                               'metadataEntries':len(entries),'copiedFiles':['Meta.dat'] if meta.exists() else []})
    return len(maps),len(entries)

class Store:
    def __init__(self,directory,*,decoration_keys=None,chat_notice_matches=()):
        self.decoration_keys=decoration_keys
        self.chat_notice_matches=chat_notice_matches
        self.directory=Path(directory);self.directory.mkdir(parents=True,exist_ok=False)
        self.file=(self.directory/'events.jsonl').open('a',encoding='utf-8',buffering=1)
        self.stage=STAGES[0];self.map=None;self.self_id=None;self.position=None;self.epoch=0
        self.maps={};self.spawns={};self.humans={};self.counts=collections.Counter();self.skipped=collections.Counter()
        self.total=0;self.parse_errors=0;self.dropped=0;self.hook_errors=0;self.started=now()
        self.frame_counts=collections.Counter();self.frame_mismatches=0
        self.action_id=0;self.action_counts=collections.Counter();self.camera_counts=collections.Counter();self.floors=collections.Counter()
        self.raw_pending=collections.Counter();self.initializations=[];self.initialization=None
        self.coverage=set();self.last_action=None;self.npc_requests={};self.shop_order=None
        self.fishing_cast=self.fishing_bait_packet=None
        self.pickup_request=None
        self.friend_request=None
        self.party_notice_frame=None
        self.recent_frames=collections.deque(maxlen=3)
        self.mastery_updates=collections.deque(maxlen=2)
        self.write({'kind':'recording_started','schema':2,'privacy':'frame headers for both directions; allowlisted S payloads; private chat and scoped game events; no public chat/login/token/key payloads',
                    'chatNoticeMatchCount':len(chat_notice_matches)})
    def write(self,event):
        record={'time':now(),'stage':self.stage,'map':self.map,'epoch':self.epoch,'recentActionId':self.action_id or None,**event}
        self.file.write(json.dumps(record,ensure_ascii=False,separators=(',',':'))+'\n');self.total+=1
        return record
    def reset(self,reason,packet_id=None):
        self.map=None;self.self_id=None;self.position=None;self.epoch+=1;self.npc_requests.clear()
        self.shop_order=None
        self.fishing_cast=self.fishing_bait_packet=None
        self.pickup_request=None
        self.friend_request=None
        self.party_notice_frame=None
        self.recent_frames.clear()
        self.mastery_updates.clear()
        self.write({'kind':'context_reset','reason':reason,'packetId':packet_id})
        if reason=='server_transfer':
            self.initialization={'epoch':self.epoch,'packets':[],'note':'allowlisted receive order; omitted packets are not represented'}
            self.initializations.append(self.initialization)
    def auto_stage(self,index,reason):
        value=STAGES[index]
        if value!=self.stage:self.stage=value;self.write({'kind':'automatic_stage','reason':reason})
    def action(self,payload):
        action=payload.get('action','unknown')
        if action=='crc_reply':
            self.write({'kind':'crc_reply','opcode':0x45,'packetId':payload['packetId'],'checksum':payload['checksum']});return
        if action=='object_refresh_request':
            target=payload.get('targetId')
            if payload.get('opcode')==0x0C and payload.get('length')==5 and type(target) is int and 0<=target<=0xFFFFFFFF:
                self.write({'kind':'object_refresh_request','opcode':0x0C,'packetId':payload.get('packetId'),'targetId':target})
            return
        if action=='friend_request':
            mode,size=payload.get('mode'),payload.get('inputBytes')
            length=payload.get('length')
            if (payload.get('opcode')!=0x77 or type(mode) is not int or not
                (mode==0 and length==3 or mode in (1,2) and type(size) is int and 1<=size<=255 and length==size+3)):
                return
        if action=='cash_shop_request':
            mode,length=payload.get('mode'),payload.get('length')
            if (payload.get('opcode')!=0x6C or type(mode) is not int or not
                (mode in (0,2) and length==2 or mode in (1,3) and length==10)):
                return
        if action in ('clan_info_request','web_board_request'):
            mode=payload.get('mode')
            opcode=0x36 if action=='clan_info_request' else 0x73
            if payload.get('opcode')!=opcode or payload.get('length')!=2 or type(mode) is not int or not 0<=mode<=255:
                return
        if action=='merc_inventory_move':
            limits={'mode':2,'slot':255,'destinationSlot':255,'dropX':65535,'dropY':65535,'dropQuantity':0xFFFFFFFF}
            if (payload.get('opcode')!=0x72 or payload.get('length')!=13 or
                any(type(payload.get(key)) is not int or not 0<=payload[key]<=maximum for key,maximum in limits.items())):
                return
        self.action_id+=1;self.last_action=action;self.action_counts[action]+=1
        if action in ('creation_started','creation_name_submitted','creation_appearance'):
            self.auto_stage(1,action)
            if action=='creation_appearance':self.coverage.add('creation_appearance')
        elif action=='character_selection':self.auto_stage(2,action)
        elif action in ('move_request','turn_request','push_request'):
            self.auto_stage(3,action);self.coverage.add('movement');self.initialization=None
        elif action=='bank_move':
            self.auto_stage(5,action);self.coverage.add('bank_request')
        elif action=='merc_inventory_move':
            self.auto_stage(5,action);self.coverage.add('merc_inventory_request')
        elif action in ('inventory_drag','inventory_bundle','inventory_remove_request'):
            self.auto_stage(5,action);self.coverage.add('item_request')
        elif action in ('spell_slot_request','spell_cast_request'):
            self.auto_stage(6,action);self.coverage.add('spell_request')
        elif action=='interaction':
            op=payload.get('opcode')
            if op in (0x16,0x39,0x3A,0x43,0x55):self.auto_stage(4,'npc_or_shop_request');self.coverage.add('npc_request')
            elif op in (0x07,0x08,0x1C,0x1E,0x44,0x6E):self.auto_stage(5,'item_or_equipment_request');self.coverage.add('item_request')
            elif op in (0x0F,0x13,0x14,0x3E,0x56):self.auto_stage(6,'combat_or_skill_request');self.coverage.add('combat_request')
        allowed={'action','appearance','direction','sequence','opcode','length','slot','slotType','packetId','mapId','checksum',
                 'mode','targetId','spellTargetFlag','menu','script','page','dialogAction','dialogChoice','dialogInputBytes','attackMode','attackParam','pickupWords',
                 'dropX','dropY','dropQuantity','destinationSlot','orderMode','orderLines','petAction','pushX','pushY','pushFloor'}
        if action in ('cash_shop_request','clan_info_request','web_board_request'):allowed={'action','opcode','length','packetId','mode'}
        if action=='merc_inventory_move':allowed={'action','opcode','length','packetId',*limits}
        event={'kind':'client_action',**{k:v for k,v in payload.items() if k in allowed}}
        selector,value=payload.get('selector'),payload.get('value')
        if action=='friend_request' and mode in (1,2):
            event['inputBytes']=size
            if type(payload.get('targetPreviouslyListed')) is bool:
                event['targetPreviouslyListed']=payload['targetPreviouslyListed']
            self.friend_request=(self.action_id,event.get('packetId'),time.monotonic())
        elif action=='room_minigame' and payload.get('opcode')==0x6A and payload.get('mode')==3:
            if selector in (1,3,5,8,11) and payload.get('length')==3:
                event['selector']=selector
            elif selector==2 and payload.get('length')==5 and type(value) is int and 0<=value<=65535:
                event.update(selector=2,value=value)
            elif selector in (0,7,10,12):
                input_bytes=payload.get('inputBytes')
                if type(input_bytes) is int and 0<=input_bytes<=255 and payload.get('length')==4+input_bytes:
                    event.update(selector=selector,inputBytes=input_bytes)
            elif selector in (4,6,9):
                entry_id,placement=payload.get('entryId'),payload.get('placement')
                valid_placement=(isinstance(placement,list) and len(placement)==3 and
                                 all(type(n) is int and 0<=n<=255 for n in placement))
                length=payload.get('length')
                if (type(entry_id) is int and 0<=entry_id<=0xffffffff and
                        (selector==4 and length==10 and valid_placement or selector!=4 and length==7)):
                    event.update(selector=selector,entryId=entry_id)
                    if selector==4:event['placement']=placement
        elif action=='merc_dialog' and payload.get('opcode')==0x54 and payload.get('mode')==3:
            input_bytes=payload.get('inputBytes')
            if type(selector) is int and 0<=selector<=255 and type(input_bytes) is int and 0<=input_bytes<=65528:
                event.update(selector=selector,inputBytes=input_bytes)
        elif action=='merc_dialog' and payload.get('opcode')==0x54 and payload.get('mode')==2:
            options=payload.get('controlOptions')
            length=payload.get('length')
            slot=payload.get('slot')
            words=payload.get('mercWords')
            event.pop('slot',None)
            if length==3 and selector in (0,3,10):
                event['selector']=selector
            elif ((length,selector) in ((4,1),(4,6),(5,9)) and
                    isinstance(options,list) and len(options)==length-3 and
                    all(type(option) is int and 0<=option<=255 for option in options)):
                event.update(selector=selector,controlOptions=options)
            elif (length==5 and selector==4 and type(slot) is int and 0<=slot<=255):
                event.update(selector=selector,slot=slot)
            elif (length==8 and selector==2 and type(slot) is int and 0<=slot<=255 and
                    isinstance(words,list) and len(words)==2 and
                    all(type(word) is int and 0<=word<=65535 for word in words)):
                event.update(selector=selector,slot=slot,mercWords=words)
        if 'opcode' in event and ('C',event['opcode']) in CATALOG:event['packetClass']=CATALOG[('C',event['opcode'])]['class']
        record=self.write(event)
        if (event.get('opcode'),event.get('length'),event.get('mode'))==(7,10,0):
            self.pickup_request=(self.action_id,event.get('packetId'),time.monotonic())
        if (event.get('opcode'),event.get('length'),event.get('attackMode'),event.get('attackParam'))==(0x13,3,5,0):
            self.fishing_cast=(self.action_id,event.get('packetId'),time.monotonic())
            self.fishing_bait_packet=None
        if event.get('opcode') in (0x16,0x39,0x3A,0x43) and event.get('targetId'):
            self.npc_requests[event['targetId']]=record
        window=event.get('orderMode')
        if (event.get('opcode')==0x39 and event.get('menu') in (0x4B,0x4E,0x5B)
                and event.get('orderLines') and type(window) is int and 0<=window<=255):
            self.shop_order=(self.action_id,event.get('packetId'),window,time.monotonic())
    def frame(self,payload):
        fields=('packetId','direction','transport','opcode','marker','frameLength','bodyLength','plainLength',
                'encrypted','wireSequence','lengthMatches','opcodeMatches')
        event={k:payload[k] for k in fields if k in payload}
        direction,op=event['direction'],event['opcode']
        message_mode=payload.get('messageMode')
        has_message_mode=(direction=='S' and op in (0x0A,0x0D,0x57) or direction=='C' and op in (0x0E,0x65))
        if has_message_mode and event.get('plainLength',0)>=2 and type(message_mode) is int and 0<=message_mode<=255:
            event['messageMode']=message_mode
        if (event.get('messageMode')==4 and (direction,op) in (('C',0x0E),('S',0x0D))
                and event['lengthMatches'] and event['opcodeMatches'] and isinstance(speech:=payload.get('speech'),dict)):
            color,size,padding,speaker=(speech.get(k) for k in ('color','textBytes','padding','speaker'))
            header=8 if direction=='S' else 4
            if (type(color) is int and 0<=color<=255 and type(size) is int and 0<=size<=255
                    and type(padding) is int and padding in (0,1) and header+size+padding==event['plainLength']
                    and (direction=='C' or type(speaker) is int and 0<=speaker<=0xffffffff)):
                event['speech']=dict(color=color,textBytes=size,padding=padding)
                if direction=='S':event['speech']['speaker']=speaker
        self.frame_counts[f'{direction}:{op:02x}']+=1
        if not (event['lengthMatches'] and event['opcodeMatches']):self.frame_mismatches+=1
        native=CATALOG.get((direction,op))
        event['catalogued']=native is not None
        if native:event.update(packetClass=native['class'],nativeCodec=native['codec'])
        record=self.write({'kind':'frame_header',**event})
        self.recent_frames.append(record)
        if ((direction,op,event.get('messageMode'))==('S',0x0A,3) and event['lengthMatches'] and event['opcodeMatches']
                and 5<=event.get('plainLength',0)<=PARTY_NOTICE_LIMIT):
            self.party_notice_frame=record
    def mastery_marker(self,payload):
        if self.map is None or len(self.recent_frames)!=3 or len(self.mastery_updates)!=2:return
        ids=[payload.get(key) for key in ('abilityPacketId','packetId','refreshPacketId')]
        if not all(isinstance(pid,str) and re.fullmatch(r'[0-9]+:[0-9]+',pid) for pid in ids):return
        attach,index=ids[0].split(':')
        if ids[1:]!=[f'{attach}:{int(index)+offset}' for offset in (1,2)]:return
        for header,op,pid in zip(self.recent_frames,(0x2C,0x3F,0x2C),ids):
            if (header.get('packetId')!=pid or header.get('direction')!='S' or header.get('opcode')!=op
                    or header.get('transport')!='aa' or header.get('encrypted') is not True
                    or not (header.get('lengthMatches') and header.get('opcodeMatches'))
                    or header['map']!=self.map or header['epoch']!=self.epoch):return
        update,refresh=self.mastery_updates
        if (update['mode']!=1 or update['packetId']!=ids[0] or refresh['mode']!=2 or refresh['packetId']!=ids[2]
                or self.recent_frames[1].get('plainLength')!=5
                or self.recent_frames[0].get('plainLength')!=update['length']
                or self.recent_frames[2].get('plainLength')!=refresh['length']
                or update['trailingBytes'] not in ('','00') or refresh['trailingBytes'] not in ('','00')):return
        entry=update['entries'][0]
        if not (entry['category']==0 and entry['slot'] and entry['name'] and entry['slot']==refresh['slot']):return
        words=payload.get('words')
        if not (isinstance(words,list) and len(words)==2 and all(type(w) is int and 0<=w<=0xffff for w in words)):return
        self.mastery_updates.clear()
        self.coverage.add('mastery_marker')
        self.counts['S:3f']+=1
        self.raw_pending['S:3f']+=1
        self.write(dict(kind='mastery_marker',direction='S',opcode=0x3F,length=5,packetId=ids[1],
                        abilityPacketId=ids[0],refreshPacketId=ids[2],words=words,
                        slot=entry['slot'],ability=entry['name'],mastery=entry['headerWords'][1],
                        counter=refresh['value'],reference=refresh['referenceWord']))
    def chat(self,payload,data):
        direction=payload.get('direction')
        if not data or len(data)>516:return
        opcode=data[0]
        reader=Reader(data)
        mode=None
        color=None
        try:
            if (direction,opcode) in (('C',0x0E),('S',0x0D)):
                mode=reader.u8()
                if mode!=5:return
                color=reader.u8()
            elif (direction,opcode)!=('C',0x19):
                return
            peer=reader.take(reader.u8()).decode('cp949')
            message=reader.take(reader.u8())
            text=message.decode('cp949')
        except (UnicodeDecodeError,ValueError):
            return
        trailing=len(data)-reader.p
        if not peer or peer in ('!','!!') or not text or '\0' in peer or '\0' in text or (direction=='C' and trailing):
            return
        event=dict(kind='private_chat',packetId=payload.get('packetId'),direction=direction,opcode=opcode,
                   messageMode=mode,color=color,peer=peer,text=text,messageHex=message.hex(),trailingBytes=trailing)
        if direction=='S' and opcode==0x0D and trailing==1:
            event['trailingByte']=data[reader.p]
        self.write(event)
    def chat_notice(self,payload,data):
        if not self.chat_notice_matches or not 4<=len(data)<=512 or data[:2]!=b'\x0a\0':return
        reader=Reader(data)
        try:
            reader.u8()
            message=reader.take(reader.u16())
            if not any(pattern in message for pattern in self.chat_notice_matches):return
            text=message.decode('cp949')
        except (UnicodeDecodeError,ValueError):
            return
        self.write(dict(kind='chat_notice',packetId=payload.get('packetId'),direction='S',opcode=0x0A,
                        messageMode=0,text=text,messageHex=message.hex(),trailingBytes=len(data)-reader.p))
    def shop_notice(self,payload,data):
        if not self.shop_order or data[:2]!=b'\x0a\x0e':return
        action,order_packet,window,start=self.shop_order
        if action!=self.action_id or time.monotonic()-start>2:return
        reader=Reader(data)
        try:
            reader.u8()
            message=reader.take(reader.u16())
            text=message.decode('cp949')
        except (UnicodeDecodeError,ValueError):
            return
        # The declared string ends before any unparsed frame suffix. Retain only its length.
        if len(text)<2 or (ord(text[0])&0xff)!=window or text[1] not in ('0','1') or '\0' in text[2:]:return
        self.write(dict(kind='shop_notice',packetId=payload.get('packetId'),orderPacketId=order_packet,
                        direction='S',opcode=0x0A,messageMode=0x0E,windowId=window,resetSelection=text[1]=='1',
                        text=text[2:],messageBytes=len(message),trailingBytes=len(data)-reader.p))
    def fishing_notice(self,payload,data):
        if (not self.fishing_cast or not self.fishing_bait_packet or not 6<=len(data)<=128
                or data[0]!=0x0A or data[1] not in (3,4)):return
        action,cast_packet,start=self.fishing_cast
        if action!=self.action_id or time.monotonic()-start>2:return
        reader=Reader(data)
        try:
            mode=reader.u8()
            message=reader.take(reader.u16())
            text=message.decode('cp949')
        except (UnicodeDecodeError,ValueError):
            return
        if '낚' not in text or not text.isprintable() or data[reader.p:] not in (b'',b'\0'):return
        self.write(dict(kind='fishing_notice',packetId=payload.get('packetId'),castPacketId=cast_packet,
                        baitPacketId=self.fishing_bait_packet,direction='S',opcode=0x0A,messageMode=mode,
                        text=text,messageBytes=len(message),trailingBytes=len(data)-reader.p))
        self.fishing_cast=self.fishing_bait_packet=None
    def pickup_notice(self,payload,data):
        if (self.map is None or not self.pickup_request or len(data)!=28
                or data[0]!=0x0A or data[1] not in (3,4) or len(self.recent_frames)!=3):return
        action,request,started=self.pickup_request
        if action!=self.action_id or request!=payload.get('requestPacketId') or time.monotonic()-started>1:return
        ids=[payload.get(key) for key in ('pickupPacketId','removalPacketId','packetId')]
        for header,packet,opcode,length in zip(self.recent_frames,ids,(0x53,0x0E,0x0A),(10,6,28)):
            if ((header.get('packetId'),header.get('direction'),header.get('transport'),header.get('opcode'),header.get('plainLength'))
                    !=(packet,'S','aa',opcode,length) or not (header['lengthMatches'] and header['opcodeMatches'] and header.get('encrypted'))
                    or header['map']!=self.map or header['epoch']!=self.epoch or header['recentActionId']!=action):return
        item=payload.get('itemId')
        if type(item) is not int or not 0<item<=0xffffffff or self.recent_frames[-1].get('messageMode')!=data[1]:return
        reader=Reader(data)
        try:
            mode=reader.u8()
            message=reader.take(reader.u16())
            text=message.decode('cp949')
        except (UnicodeDecodeError,ValueError):
            return
        if not text or not text.isprintable() or data[reader.p:] not in (b'',b'\0'):return
        self.write(dict(kind='pickup_notice',packetId=ids[-1],requestPacketId=request,pickupPacketId=ids[0],removalPacketId=ids[1],
                        itemId=item,direction='S',opcode=0x0A,messageMode=mode,text=text,
                        messageBytes=len(message),trailingBytes=len(data)-reader.p))
        self.pickup_request=None
    def party_notice(self,event,details):
        frame=self.party_notice_frame
        if not frame or not isinstance(details,dict) or event.get('mode') not in (0,2) or event.get('slot') not in range(7):return
        prefix,suffix,member_bytes,padding=(details.get(k) for k in ('prefix','suffix','memberBytes','padding'))
        if (not valid_party_notice_affix(prefix) or not valid_party_notice_affix(suffix) or '파티' not in prefix+suffix
                or type(member_bytes) is not int or not 1<=member_bytes<=32 or type(padding) is not int or padding not in (0,1)
                or event['mode']==0 and event.get('nameBytes')!=member_bytes):return
        first,second=frame.get('packetId'),event.get('packetId')
        if not all(isinstance(pid,str) and re.fullmatch(r'[0-9]+:[0-9]+',pid) for pid in (first,second)):return
        connection,index=first.split(':'); following,next_index=second.split(':')
        if (connection!=following or int(next_index)!=int(index)+1 or first!=details.get('packetId')
                or frame['map']!=self.map or frame['epoch']!=self.epoch
                or len((prefix+suffix).encode('cp949'))+member_bytes+4+padding!=frame['plainLength']):return
        event['noticeTemplate']={'packetId':first,'prefix':prefix,'suffix':suffix,'memberBytes':member_bytes,'padding':padding}
    def packet(self,raw,packet_id=None,*,friend_list_facts=None,party_notice=None):
        try:b=sanitized_packet(raw,decoration_keys=self.decoration_keys)
        except ValueError as e:
            self.parse_errors+=1;self.write({'kind':'privacy_parse_error','packetId':packet_id,'opcode':raw[0] if raw else None,'length':len(raw),'error':str(e)});return
        if b is None:
            if raw:self.skipped[f'S:{raw[0]:02x}']+=1
            return
        op=b[0];self.counts[f'S:{op:02x}']+=1
        try:event=decode(b,decoration_keys=self.decoration_keys)
        except (ValueError,IndexError) as e:self.parse_errors+=1;event={'kind':'decode_error','error':str(e)}
        event.update(opcode=op,length=len(b),raw=b.hex(),packetId=packet_id)
        if event['kind']=='party':self.party_notice(event,party_notice)
        if op==0x2F and len(raw)!=len(b):
            event.update(length=len(raw),retainedLength=len(b),discardedSuffixBytes=len(raw)-len(b))
        if event['kind']=='friend_list' and event.get('mode')==0 and isinstance(friend_list_facts,dict):
            ascending=friend_list_facts.get('bytewiseAscending')
            if type(ascending) is bool:event['bytewiseAscending']=ascending
            index=friend_list_facts.get('targetIndex')
            if (self.friend_request and self.friend_request[:2]==(self.action_id,friend_list_facts.get('requestPacketId'))
                    and time.monotonic()-self.friend_request[2]<=.25 and type(index) is int and -1<=index<event['count']):
                event['targetIndex']=index
                kept=friend_list_facts.get('otherEntriesKeptOrder')
                if type(kept) is bool:event['otherEntriesKeptOrder']=kept
        if ('S',op) in CATALOG:event['packetClass']=CATALOG[('S',op)]['class'];event['nativeCodec']=CATALOG[('S',op)]['codec']
        if event.get('layout')=='raw_pending_decode':self.raw_pending[f'S:{op:02x}']+=1
        if self.initialization is not None and len(self.initialization['packets'])<40:
            self.initialization['packets'].append({'opcode':op,'class':event.get('packetClass'),'length':len(b)})
        if event['kind']=='map':
            self.auto_stage(3 if self.maps else 2,'map_packet');self.coverage.add('map')
            old=self.map;self.map=event['map'];self.position=None
            if old!=self.map:
                self.npc_requests.clear()
                self.fishing_cast=self.fishing_bait_packet=None
                self.pickup_request=None
            self.maps[str(self.map)]={**event,'lastSeen':now(),'previousMap':old}
        elif event['kind']=='self_identity':self.self_id=event['oid'];self.coverage.add('self_identity')
        elif event['kind'] in ('self_position','self_move'):
            self.position={k:event[k] for k in ('x','y','floor')};self.coverage.add('position')
            self.camera_counts[f"{event['cameraX']},{event['cameraY']}"]+=1;self.floors[str(event['floor'])]+=1
        elif event['kind']=='objects' and self.map is not None:
            for obj in event['objects']:
                if obj['category'] in (2,6,7):continue
                # Observed position, not a claimed authoritative spawn table.
                key=json.dumps([self.map,*[obj[k] for k in ('category','sprite','name','x','y','floor')]],ensure_ascii=False)
                record=self.spawns.setdefault(key,{'map':self.map,**obj,'observations':0,'firstSeen':now()})
                record['observations']+=1;record['lastSeen']=now()
                if obj['category']==1:self.coverage.add('npc_seen')
                elif obj['category']==0:self.coverage.add('monster_seen')
                elif obj['category']==3:self.coverage.add('ground_item_seen')
        elif event['kind']=='human':
            event['isSelf']=event['oid']==self.self_id
            if event['isSelf']:self.coverage.add('self_appearance')
            key=json.dumps([event['appearance'],event['equipment'],event['flag1'],event['flag2']])
            self.humans[key]={'appearance':event['appearance'],'equipment':event['equipment'],
                              'flag1':event['flag1'],'flag2':event['flag2'],'lastSeen':now(),'observedSelf':event['isSelf'] or self.humans.get(key,{}).get('observedSelf',False)}
        if event['kind'] in ('merchant_dialog','npc_pursuit'):
            self.coverage.add(event['kind']);self.auto_stage(4,'merchant_response')
            request=self.npc_requests.pop(event['oid'],None)
            if request and (datetime.datetime.fromisoformat(now())-datetime.datetime.fromisoformat(request['time'])).total_seconds()<=1:
                event['npcRequest']={k:request[k] for k in ('packetId','opcode','mode','menu','length','targetId','script','page','dialogAction','dialogChoice') if k in request}

        if op==8:self.coverage.add('status')
        if event['kind']=='combo':self.coverage.add('combo')
        if event['kind']=='ability':
            self.coverage.add('ability_progress' if event['mode']==2 else 'ability_list')
            if event['mode'] in (1,2):self.mastery_updates.append(event)
        if EXTENDED_PACKET_KINDS.get(op)==event['kind'] and event.get('layout')!='raw_pending_decode':
            self.coverage.add(event['kind'])
        if op==0x64 and event['kind'].startswith('room_'):self.coverage.add(event['kind'])
        if event.get('statFields'):self.coverage.add('item_stats')
        if op in (0x0F,0x37):self.coverage.add('inventory_or_equipment')
        if (event['kind']=='inventory_add' and self.fishing_cast and self.fishing_cast[0]==self.action_id
                and self.fishing_bait_packet is None and event.get('displayName')=='지렁이병(대)'):
            remaining=fishing_bait_remaining(event.get('name'),event.get('wireSprite'))
            if remaining is not None and remaining<200:self.fishing_bait_packet=packet_id
        self.write(event)
    def handle(self,payload,data=None):
        kind=payload.get('type')
        if kind=='packet':
            if payload.get('direction')=='S' and data:
                self.packet(bytes(data),payload.get('packetId'),friend_list_facts=payload.get('friendListFacts'),
                            party_notice=payload.get('partyNotice'))
        elif kind=='frame':self.frame(payload)
        elif kind=='mastery_marker':self.mastery_marker(payload)
        elif kind=='chat':self.chat(payload,bytes(data) if data else b'')
        elif kind=='chat_notice':self.chat_notice(payload,bytes(data) if data else b'')
        elif kind=='shop_notice':self.shop_notice(payload,bytes(data) if data else b'')
        elif kind=='fishing_notice':self.fishing_notice(payload,bytes(data) if data else b'')
        elif kind=='pickup_notice':self.pickup_notice(payload,bytes(data) if data else b'')
        elif kind=='stats':self.skipped.update(payload.get('skipped',{}));self.hook_errors=payload.get('hookErrors',self.hook_errors)
        elif kind=='context_reset':self.reset('server_transfer',payload.get('packetId'))
        elif kind=='snapshot':
            if self.map is None:
                self.map=payload['map'];self.self_id=payload['selfId'];self.position=payload.get('position')
                self.write({'kind':'context_snapshot','source':'memory','selfId':self.self_id,'position':self.position})
        elif kind=='action':
            self.action(payload)
        elif kind=='check':self.write({'kind':'server_check','packetId':payload.get('packetId'),'code':payload['code']})
        elif kind=='seed':self.write({'kind':'handshake_seed','packetId':payload['packetId'],'seed':payload['seed']})
    def summary(self):
        return {'schema':2,'startedAt':self.started,'updatedAt':now(),'events':self.total,
                'currentMap':self.map,'position':self.position,'selfId':self.self_id,'stage':self.stage,
                'mapsObserved':len(self.maps),'objectPositionsObserved':len(self.spawns),'appearancesObserved':len(self.humans),
                'packetCounts':dict(self.counts),'skippedOpcodes':dict(self.skipped),'parseErrors':self.parse_errors,
                'frameCounts':dict(self.frame_counts),'frameMismatches':self.frame_mismatches,
                'hookErrors':self.hook_errors,'droppedEvents':self.dropped,
                'actionsObserved':self.action_id,'automaticStages':True,'coverage':sorted(self.coverage),
                'limitations':['Only observed areas/actions; not complete server data.','Object positions are sightings, not proven spawn points.',
                              'Unparsed allowlisted packets retained for later analysis.',
                              'Unknown packets retain headers only unless captured by a scoped game event.']}
    def flush(self):
        self.file.flush()
        failed=[]
        def publish(path,write):
            try:write()
            except PermissionError:failed.append(path)
        snapshots={
            'summary.json':self.summary(),
            'maps.json':list(self.maps.values()),
            'object-observations.json':list(self.spawns.values()),
            'appearance-presets.json':list(self.humans.values()),
        }
        unregistered={f'S:{op:02x}':self.frame_counts[f'S:{op:02x}'] for op in sorted(UNREGISTERED_SERVER_OPCODES)
                      if self.frame_counts[f'S:{op:02x}']}
        analysis={'generatedAt':now(),'mode':'deterministic observation summary','coverage':sorted(self.coverage),
                  'actions':dict(self.action_counts),'cameraOffsets':dict(self.camera_counts),'observedFloors':dict(self.floors),
                  'initializationSequences':self.initializations,'needsDecode':dict(self.raw_pending),'unregisteredV611':unregistered,
                  'correlation':'recentActionId is temporal proximity, not proof of causation',
                  'nextSuggestedStep':self.next_step()}
        snapshots['automatic-analysis.json']=analysis
        for name,value in snapshots.items():
            publish(name,lambda:dump(self.directory/name,value))
        lines=['자동 관찰 요약 (2초마다 갱신)',f'현재 단계: {self.stage}',f'관찰 행동: {self.action_id}회',
               f'방문 맵: {len(self.maps)}개 / 오브젝트 관찰 위치: {len(self.spawns)}개',
               '화면 기준값 관찰: '+(', '.join(f'{k}: {v}회' for k,v in self.camera_counts.items()) or '아직 없음'),
               '층 관찰: '+(', '.join(self.floors) or '아직 없음'),'다음 권장 관찰: '+self.next_step(),'',
               '원문 확보 후 추가 해독이 필요한 패킷:']
        lines += [f'{k}: {v}건' for k,v in self.raw_pending.items()] or ['없음']
        lines += ['', 'v611 수신 처리기 미등록 패킷:']
        lines += [f'{k}: {v}건' for k,v in unregistered.items()] or ['없음']
        lines += ['',f'프레임 {sum(self.frame_counts.values())}, 헤더 불일치 {self.frame_mismatches}',
                  f'해독 오류 {self.parse_errors}, 후크 오류 {self.hook_errors}, 누락 {self.dropped}',
                  '행동 ID는 시간상 연관을 나타내며 요청/응답 관계가 확정됐다는 뜻은 아닙니다.',
                  '이 파일은 규칙 기반 요약입니다. 서버 기능·게임 규칙의 복원 여부는 별도 검증합니다.']
        def publish_text():
            temp=self.directory/'analysis.txt.tmp'
            temp.write_text('\n'.join(lines)+'\n',encoding='utf-8')
            os.replace(temp,self.directory/'analysis.txt')
        publish('analysis.txt',publish_text)
        return tuple(failed)
    def next_step(self):
        if 'creation_appearance' not in self.coverage:return '캐릭터 생성 과정 (이미 생성했다면 다음 기록 때 수집 가능)'
        if not {'map','position','self_appearance','status'}<=self.coverage:return '첫 접속 후 초기 화면에서 잠시 대기'
        if len(self.maps)<2:return '방향 전환·이동 후 인접 맵 출입구 통과'
        if 'merchant_dialog' not in self.coverage:return 'NPC 대화와 상점 열기'
        if 'item_request' not in self.coverage:return '아이템 사용·장비 착용/해제'
        if 'combat_request' not in self.coverage:return '일반 공격과 스킬 사용'
        return '기본 단계 확보. 분석 결과에 따라 부족한 지역·행동을 추가 관찰'
    def close(self):self.write({'kind':'recording_stopped'});self.flush();self.file.close()
