"""Publish observed world data from the recorder to the local server."""
import argparse
import base64
from collections import Counter, defaultdict, deque
import ctypes
from ctypes import wintypes
import datetime
import functools
import hashlib
import json
import os
import sys
from pathlib import Path
import time

ROOT = Path(__file__).resolve().parents[1]
RECORDER = ROOT / "recorder" if (ROOT / "recorder" / "recorder_core.py").is_file() else ROOT.parent / "ElanciaRecorder"
sys.path.insert(0, str(RECORDER))
from recorder_core import decode, fishing_bait_remaining, item_stats as parse_item_stats, sanitized_packet

DELTAS = ((0,-1),(1,0),(0,1),(-1,0),(1,-1),(1,1),(-1,1),(-1,-1))
HUNTING_MAPS = {4, 5, 7, 9, 10, 232, 241, 258, 1004, 1500, 2004, 2006, 2008, 2500, 4003}
STATIONARY_CHEST_SPRITE = 0x4019
CHEST_SPRITES = {STATIONARY_CHEST_SPRITE, 0x440F}
FIELD_LOOT_ITEMS = {
    ('깨끗한물',0x8CDD), ('곰발톱',0x8D02), ('빛나는가루',0x8FC6), ('빛나는나뭇가지',0x8FC4),
    ('브라키디온',0x8CCA), ('목재(소나무)',0x8D49), ('세공지침서(6-8)',0x8C5A), ('사슴고기',0x8BE3),
    ('약초',0x8CE2), ('체력향상포션(3ml)',0x8F1A), ('마나포션(6ml)',0x8D28),
}
MONSTER_SPRITES = {
    '슬라임디':0x43EB,
    '토끼':0x43F0, '암탉':0x43F1, '수탉':0x43F2, '돼지':0x43F3, '사슴':0x43F4, '푸푸':0x43F5,
    '작은쥐':0x43F6, '큰쥐':0x43F8, '회색쥐':0x43F9, '얼룩쥐':0x43FB, '빨간거미':0x43FC, '거미':0x43FD,
    '아이스푸푸':0x4401, '페페':0x4402,
    '순록':0x4408, '울보요정':0x4410, '포이즌머쉬':0x4462, '트렌트':0x4465,
    '오리':0x440C, '달팽이':0x441A, '무서운달팽이':0x441B, '황금해골':0x47F2, '황금해골(가드)':0x47F3,
    '좀비':0x47D0, '오크':0x47D1, '오크(셔먼)':0x47D1, '오크(가드)':0x47D2, '오크(나이트)':0x47D2, '머맨':0x47D3,
    '실버고렘':0x47D8, '고블린':0x47DE, '곰':0x47E6, '스톤고렘':0x47EB, '오크(킹)':0x47EC,
    '유익인':0x47D6, '유익인(이글)':0x47D7,
    '다크오우거(영)':0x480A, '다크오우거':0x480B, '나무귀신':0x4810,
}
ALIASES = {
    ("소환서(용병)",0x8574):"mercenary-summon-scroll",
    ("슬라임조각(파란색)",0x884C):"blue-slime-fragment",
    ("큰쥐꼬리",0x8514):"large-rat-tail", ("회색쥐눈",0x8520):"gray-rat-eye",
    ("비늘",0x888C):"merman-scale",
    ("달팽이껍질",0x8884):"snail-shell", ("황금해골의뼈",0x88A8):"gold-skeleton-bone", ("천",0x85E4):"cloth",
    ("비늘방패",0x8227):"scale-shield",
    ("컨드의낚싯대",0x8228):"kond-fishing-rod",
    ("성스러운물",0x8CDD):"holy-water", ("스미스의링",0x80AD):"old-smith-ring",
    ("좀비의뼈",0x8888):"zombie-bone",
    ("사탕",0x8138):"nerua-candy",
    ("낡은아이언링",0x801A):"old-iron-ring", ("낡은실버링",0x801B):"old-silver-ring", ("낡은골드링",0x801C):"old-gold-ring",
    ("브리디움링",0x8021):"bridium-ring", ("다니움링",0x8022):"danium-ring", ("마하디움링",0x8023):"mahadium-ring",
    ("브라키디움링",0x8024):"brakidium-ring", ("엘사리아링",0x8025):"elsaria-ring", ("테사랏트링",0x8026):"tessarat-ring",
    ("유익인의날개",0x84BC):"uyikin-wing", ("낡은신발",0x804E):"old-shoes",
    ("테디의바지",0x8099):"teddy-pants", ("테디의슈즈",0x8055):"teddy-shoes",
    ("페어리아머",0xA7C1):"fairy-armor", ("아이스실드",0x8371):"ice-shield",
    ("해골의실드",0x8377):"bone-shield", ("키아의대거",0x8000):"kiah-dagger",
    ("마데이아의링",0x8118):"madeia-ring-left",
    ("시미터",0x82C0):"scimitar", ("달걀",0x8BD7):"egg", ("닭고기",0x8C30):"chicken-meat",
    ("힐링포션(1ml)",0x8BC1):"healing-potion-1ml",
    ("오란의깃",0x8BC7):"oran-feather", ("라스의깃",0x8F24):"ras-feather",
    ("아이언링",0x801A):"iron-ring", ("실버링",0x801B):"silver-ring", ("골드링",0x801C):"gold-ring",
    ("아이언네클리스",0x801D):"iron-necklace-basic", ("실버네클리스",0x801E):"silver-necklace",
    ("골드네클리스",0x801F):"gold-necklace", ("루비네클리스",0x8020):"ruby-necklace",
}

# These exact equipped variants supplied the existing local equipment profiles.
# Keep the local names when importing their verified packet descriptions.
EQUIPPED_OPTION_PROFILES = {
    ("알라븅자갸의수영모(군청색)(Lv1)",0x82F6):("swim-cap","수영모(군청색)"),
    ("신연비의아이언네클리스",0x801D):("iron-necklace","아이언네클리스"),
    ("벌자의울슈즈(하얀색)(Lv1)",0x8234):("wool-shoes","울슈즈(하얀색)"),
    ("딸기쭈니의반바지(하늘색)",0x806D):("sky-shorts","반바지(하늘색)"),
    ("섹시고양이의가니쉬(황토색)",0x807E):("ochre-garnish","가니쉬(황토색)"),
}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def observed_time(event):
    return datetime.datetime.fromisoformat(event["time"]).timestamp()


def matches_hourly_clock(hour, day):
    if not hour or hour.get('map') is None or hour.get('epoch') != day.get('epoch') or hour['map'] != day.get('map'):
        return False
    start, _, packet = (hour.get('packetId') or '').rpartition(':')
    end, _, following = (day.get('packetId') or '').rpartition(':')
    return (start and start == end and packet.isdecimal() and following.isdecimal()
            and int(following) == int(packet) + 1 and 0 <= observed_time(day) - observed_time(hour) <= .5
            and 1 <= day['day'] <= 31)


def matches_destination_choice(choice, arrival):
    return bool(choice and choice.get('mapId') is not None and choice['mapId'] == arrival.get('map')
                and choice.get('epoch') == arrival.get('epoch')
                and 0 <= observed_time(arrival) - observed_time(choice) <= 1)


# S37 slot numbers and S33 visual layers are independent.
EQUIPMENT_LAYERS = {1: 0, 2: 2, 3: 1, 4: 6, 13: 5, 15: 4, 16: 3}
ACCESSORY_SLOTS = {6, 7, 8}


class EquipmentAppearances:
    def __init__(self):
        self.action = self.pending = None

    def ingest(self, event):
        kind = event.get('kind')
        if kind in ('client_action', 'context_reset'):
            self.pending = None
            self.action = ((event.get('epoch'), event['recentActionId'])
                           if event.get('opcode') in (0x1C, 0x1E, 0x61) and event.get('recentActionId') is not None
                           else None)
            return
        if self.action is None or self.action != (event.get('epoch'), event.get('recentActionId')):
            return
        if kind == 'equipment_add':
            self.pending = event if event.get('slot') in EQUIPMENT_LAYERS else None
        elif kind == 'human' and event.get('isSelf') and self.pending:
            equipment, self.pending = self.pending, None
            layer = EQUIPMENT_LAYERS[equipment['slot']]
            word = event['equipment'][layer]
            if word and 0 <= observed_time(event) - observed_time(equipment) <= .25:
                return equipment, layer, word


def location(row):
    return row["Map"], row["X"], row["Y"], row["Floor"]


def training_target_baseline(world):
    return {location(target): target for target in (
        dict(row, Map=row['Map'] + dimension * 10000)
        for row in world.get('TrainingTargets', []) for dimension in range(3)
    )}


def npc_identity(row):
    return *location(row), row["Sprite"], row["Name"]


FREE_TIPONE_ROUTES = {
    '3901057D/3A010000000200/3A010000000200/3A01000000020101',
    '390104DA/3A01000000020102/3A01000000020104/3A01000000020101',
}


def pursuit_key(request, parent):
    opcode, length = request['opcode'], request['length']
    if opcode == 0x39 and length == 8 and 'menu' in request:
        return (bytes([opcode, request['mode']]) + request['menu'].to_bytes(2, 'big')).hex().upper()
    if opcode == 0x3A and parent and (request.get('dialogAction'), length) in ((0,11),(1,12)):
        tail = request['script'].to_bytes(2, 'big') + request['page'].to_bytes(2, 'big')
        tail += bytes([request['dialogAction']])
        if length == 12:
            tail += bytes([request['dialogChoice']])
        return parent + '/' + (bytes([opcode, request['mode']]) + tail).hex().upper()


def spawn_zone(row):
    sprite = row['Sprite'] if row['Name'] == '나무상자' else 0
    return row['Map'], row['X'] // 24, row['Y'] // 20, row['Floor'], sprite


def loot_identity(rule):
    return rule['Map'],rule['Name'],rule['Sprite'],tuple((d['Key'],d['Quantity']) for d in rule['Drops'])


class DropObservations:
    def __init__(self, extra_targets=None):
        self.context = None
        self.extra_targets = extra_targets or {}
        self.active = {}
        self.hits = {}
        self.seen_items = set()
        self.ground = deque(maxlen=64)
        self.last_manual_drop = None
        self.appearances = Counter()
        self.moves = Counter()

    def ingest(self, row):
        kind, map_id = row['kind'], row.get('map')
        current = row.get('epoch'), map_id
        if kind == 'context_reset' or current != self.context:
            self.context = current
            self.active.clear()
            self.hits.clear()
            self.seen_items.clear()
            self.ground.clear()
            self.last_manual_drop = None
        if map_id is None:
            return
        if kind == 'client_action' and row.get('opcode') == 8:
            self.last_manual_drop = observed_time(row)
        elif kind == 'objects':
            when = observed_time(row)
            for obj in row['objects']:
                oid, sprite, name = obj['oid'], obj['sprite'], obj['name']
                if obj['category'] == 0 and (MONSTER_SPRITES.get(name, self.extra_targets.get(name)) == sprite
                        or name == '나무상자' and sprite in CHEST_SPRITES):
                    if oid not in self.active:
                        self.appearances[name,sprite] += 1
                    self.active[oid] = {**obj, 'appearance':row.get('packetId')}
                elif obj['category'] == 3 and oid not in self.seen_items:
                    self.seen_items.add(oid)
                    if self.last_manual_drop is None or when-self.last_manual_drop > .5:
                        self.ground.append((when,row.get('packetId'),obj))
        elif kind == 'damage_effect' and row['target'] in self.active:
            if len(words := row.get('extraWords', [])) >= 2 and words[1] > 0 and row['attacker'] != row['target']:
                self.hits[row['target']] = observed_time(row), row.get('packetId'), row['attacker'], row.get('recentActionId')
        elif kind == 'object_move' and (obj := self.active.get(row['oid'])) is not None and row['direction'] < 8:
            dx,dy = DELTAS[row['direction']]
            obj.update(x=row['x']+dx,y=row['y']+dy,floor=row['floor'])
            self.moves[obj['name'],obj['sprite']] += 1
        elif kind in ('object_remove','death'):
            obj = self.active.pop(row['oid'],None)
            hit = self.hits.pop(row['oid'],None)
            if kind != 'death' or obj is None:
                return
            at = observed_time(row)
            tile = tuple(obj[key] for key in ('x','y','floor'))
            hit = hit if hit is not None and 0 <= at-hit[0] <= 5 else None
            matched = [(when,packet,item) for when,packet,item in self.ground
                       if 0 <= at-when <= .25 and (hit is None or when >= hit[0])
                       and tuple(item[key] for key in ('x','y','floor')) == tile]
            for entry in matched:
                self.ground.remove(entry)
            return {'map':map_id,'packetId':row.get('packetId'),'sprite':obj['sprite'],'name':obj['name'],
                    'tile':tile,'appearance':obj['appearance'],'hit':hit[1] if hit else None,
                    'hit_attacker':hit[2] if hit else None,'hit_at':hit[0] if hit else None,
                    'hit_action':hit[3] if hit else None,
                    'manual_drop_nearby':self.last_manual_drop is not None and at-self.last_manual_drop<=.5,
                    'nearby_drops':[{'packetId':packet,**{key:item[key] for key in ('sprite','name','quantity')}}
                                    for _,packet,item in matched]}


class LootConfirmation:
    def __init__(self):
        self.context = (None,None)
        self.player = self.pending = self.attack = None

    def ingest(self, row, defeated):
        kind = row['kind']
        context = row.get('epoch'),row.get('map')
        if context != self.context or kind == 'context_reset':
            if context[0] != self.context[0] or kind == 'context_reset':
                self.player = None
            self.context = context
            self.pending = self.attack = None
        if kind in ('self_identity','context_snapshot'):
            self.player = row.get('oid') if kind == 'self_identity' else row.get('selfId')
        if kind == 'recording_stopped':
            self.pending = self.attack = None
            return
        if kind == 'client_action':
            self.attack = ((row.get('recentActionId'), observed_time(row))
                           if row.get('opcode') == 0x13 and row.get('length') == 3 else None)
        elif kind in ('decode_error','privacy_parse_error'):
            self.attack = None

        completed = None
        if self.pending:
            time,sample,action,combo,direct = self.pending
            age = observed_time(row)-time
            if age > .25:
                if direct or combo:
                    completed = {**sample,'confirmation':'direct_attack' if direct else 'combo'}
                self.pending = None
            elif kind in ('death','decode_error','privacy_parse_error') or kind == 'objects' and any(
                obj['category']==3 and tuple(obj[k] for k in ('x','y','floor'))==sample['tile'] for obj in row['objects']
            ):
                self.pending = None
            elif kind == 'combo' and row.get('recentActionId') == action:
                self.pending = time,sample,action,True,direct
            elif kind == 'self_move' and direct:
                self.pending = time,sample,action,combo,False
        if (defeated and self.player and defeated['hit_attacker'] == self.player and defeated['name'] != '나무상자'
                and not defeated['manual_drop_nearby'] and (action := row.get('recentActionId')) is not None):
            time = observed_time(row)
            if 0 <= time-defeated['hit_at'] <= .25:
                direct = bool(self.attack and self.attack[0] == defeated['hit_action'] == action and
                              0 <= defeated['hit_at']-self.attack[1] <= 1)
                self.pending = time,defeated,action,False,direct
        return completed


MELEE_FIELDS = ('Name','Sprite','Armor','Damage','Variant','Motion','StatusMask','MotionPadded','StatusPadded')


def melee_identity(row):
    return tuple(row[key] for key in MELEE_FIELDS[:-2]) + tuple(row.get(key) for key in MELEE_FIELDS[-2:])


def trailing_zero(row, base_length):
    length = row.get('length')
    if length == base_length:
        return False
    if length == base_length + 1 and row.get('raw','').endswith('00'):
        return True
    return None


def recorded_defense(row, field, index):
    value = row.get(field)
    if value is None and row.get('mask', 0) & 0x40:
        # Older S08 captures only store the final three base words: capacity, AC, MR.
        words = row.get('remainingBaseWords', [])
        if len(words) == 3:
            value = words[index]
    return value if isinstance(value, int) and 0 <= value <= 0xFFFF else None


class MeleeObservations:
    def __init__(self):
        self.context = (None,None)
        self.player = self.armor = self.motion = self.status = None

    def ingest(self, row, monsters):
        kind, context = row['kind'], (row.get('epoch'),row.get('map'))
        if context != self.context or kind == 'context_reset':
            if context[0] != self.context[0] or kind == 'context_reset':
                self.player = self.armor = None
            self.context, self.motion, self.status = context, None, None
        if kind == 'context_snapshot':
            self.player = row.get('selfId')
        elif kind == 'self_identity':
            self.player = row['oid']
        elif kind == 'motion' and row['oid'] in monsters:
            self.motion, self.status = row, None
        elif kind == 'status':
            if row['mask'] & 0x40:
                self.armor = recorded_defense(row, 'physicalDefense', 1)
            if row['mask'] in (0x20,0x60) and row.get('currentValues'):
                self.status = row
        elif kind == 'damage_effect' and row['target'] == self.player:
            motion, status = self.motion, self.status
            self.motion = self.status = None
            monster = monsters.get(row['attacker'])
            words = row.get('extraWords',[])
            if (not monster or monster['sprite'] == STATIONARY_CHEST_SPRITE or not motion or motion['oid'] != row['attacker']
                    or motion['mode'] != 1 or motion['words'] != [20,4,9] or row['effect'] != 0
                    or row['animation'] != 0 or len(words) != 3 or words[0] != 0
                    or row['words'][0] not in (1,4) or any(row['words'][1:]) or not 0 <= row['extraByte'] <= 3):
                return None
            when = observed_time(row)
            if not 0 <= when - observed_time(motion) <= .25:
                return None
            damage = words[1]
            if damage > 0 and (words[2] != 1 or not status or not 0 <= when - observed_time(status) <= .25):
                return None
            if damage == 0 and (words[2] != 0xFFFF or status is not None):
                return None
            return dict(Name=monster['name'],Sprite=monster['sprite'],Armor=self.armor,Damage=damage,
                Variant=row['extraByte'],Motion=row['words'][0],StatusMask=status['mask'] if status else 0,
                Map=context[1],Strike=row.get('packetId'),MotionPacket=motion.get('packetId'),
                StatusPacket=status.get('packetId') if status else None,
                MotionPadded=trailing_zero(motion,12),
                StatusPadded=trailing_zero(status,19 if status['mask']==0x20 else 65) if status else None)


SPELL_DAMAGE_FIELDS = ('Name','Sprite','MagicDefense','Damage')


def spell_damage_identity(row):
    return tuple(row[key] for key in SPELL_DAMAGE_FIELDS)


class SpellDamageObservations:
    def __init__(self):
        self.context = (None,None)
        self.player = self.hp = self.vitals = self.status = None

    def ingest(self, row, monsters):
        kind, context = row['kind'], (row.get('epoch'),row.get('map'))
        if context != self.context or kind == 'context_reset':
            if context[0] != self.context[0] or kind == 'context_reset':
                self.player = None
            self.context, self.hp, self.vitals, self.status = context, None, None, None
        if kind in ('context_snapshot','self_identity'):
            player = row.get('selfId') if kind == 'context_snapshot' else row.get('oid')
            if self.player != player:
                self.player, self.hp, self.vitals, self.status = player, None, None, None
        elif kind == 'status':
            if row.get('currentValues'):
                hp = row['currentValues'][0]
                self.vitals = (row,self.hp,hp) if row['mask'] == 0x60 else None
                self.hp, self.status = hp, None
            elif row['mask'] == 0x40:
                self.status = row
        elif kind == 'damage_effect' and row['target'] == self.player:
            vitals, status = self.vitals, self.status
            self.vitals = self.status = None
            monster = monsters.get(row['attacker'])
            words = row.get('extraWords',[])
            if (not vitals or not status or not monster or monster['name'] != '오크(셔먼)'
                    or row['animation'] != 2 or row['effect'] != 9 or row['words'] != [19,61,0,1004]
                    or len(words) != 3 or words[0] != 6 or words[2] != 1 or not row.get('packetId')):
                return None
            hp_update, before, after = vitals
            mr = recorded_defense(status, 'magicDefense', 2)
            if (before is None or after == 0 or words[1] == 0 or before - after != words[1] or mr is None
                    or mr != recorded_defense(hp_update, 'magicDefense', 2)
                    or not observed_time(hp_update) <= observed_time(status) <= observed_time(row) <= observed_time(hp_update)+.25):
                return None
            return dict(Name=monster['name'],Sprite=monster['sprite'],MagicDefense=mr,Damage=words[1],Map=context[1],
                        Strike=row['packetId'],VitalsPacket=hp_update.get('packetId'),StatusPacket=status.get('packetId'),
                        HpBefore=before,HpAfter=after)


def status_fame(event):
    return event.get('fame',event.get('baseDword',event.get('characterExperience')))


class KillFameObservations:
    def __init__(self):
        self.context = (None,None)
        self.player = None
        self.baseline = self.death = self.confirmed = None
        self.combos = 0

    def ingest(self, row, defeated=None):
        kind, context = row['kind'], (row.get('epoch'),row.get('map'))
        if context != self.context or kind == 'context_reset':
            if context[0] != self.context[0] or kind == 'context_reset':
                self.player = None
            self.context = context
            self.baseline = self.death = self.confirmed = None
            self.combos = 0
        if kind in ('context_snapshot','self_identity'):
            player = row.get('selfId') if kind == 'context_snapshot' else row.get('oid')
            if player != self.player:
                self.player = player
                self.baseline = self.death = self.confirmed = None
                self.combos = 0
        elif kind == 'death':
            self.death = None
            if (self.player and defeated and defeated['name'] != '나무상자'
                    and defeated['hit_attacker'] == self.player and defeated['packetId']):
                when = observed_time(row)
                if 0 <= when - defeated['hit_at'] <= .25:
                    self.death = when, defeated
        elif kind == 'combo':
            self.combos += 1
            if self.death and self.baseline and self.combos == 1:
                when, defeated = self.death
                if 0 <= observed_time(row) - when <= .25 and 0 <= when - self.baseline[0] <= 15:
                    self.confirmed = when, defeated, row.get('packetId'), row.get('count'), self.baseline
            self.death = None
        elif kind in ('merchant_dialog','npc_pursuit') or kind == 'client_action' and row.get('opcode') == 0x1C:
            self.baseline = self.death = self.confirmed = None
            self.combos = 0
        elif kind == 'status' and row.get('mask',0) & 0x40:
            fame = status_fame(row)
            if fame is None and row.get('raw'):
                try:
                    fame = status_fame(decode(bytes.fromhex(row['raw'])))
                except (IndexError, ValueError):
                    return None
            if fame is None or not row.get('packetId'):
                return None
            when = observed_time(row)
            previous = self.baseline
            self.baseline = when, fame, row['packetId']
            if previous is None or fame == previous[1]:
                return None
            confirmed, combos = self.confirmed, self.combos
            self.death = self.confirmed = None
            self.combos = 0
            if confirmed and combos == 1:
                killed_at, defeated, combo, combo_count, before = confirmed
                gain = fame - before[1]
                if 0 <= when - killed_at <= 15 and 0 < gain <= 0xFFFF:
                    return dict(Name=defeated['name'],Sprite=defeated['sprite'],Experience=gain,Map=context[1],
                        Kill=defeated['packetId'],Hit=defeated['hit'],Combo=combo,ComboCount=combo_count,
                        Before=before[1],After=fame,BeforePacket=before[2],AfterPacket=row['packetId'])


def guild_house_door(source, target):
    town, house = sorted((source, target))
    local = house % 10000
    return town == house // 1000 * 1000 + 2 and (
        221 <= local <= 228 or local == 1221 or any(begin <= local <= begin+7 for begin in (2221,3221,4221)))


def adjacent_packets(first, second):
    first_connection, first_index = first.split(':')
    second_connection, second_index = second.split(':')
    return first_connection == second_connection and int(second_index) == int(first_index) + 1


def door_buff_prelude(prepare, row):
    return (row.get('raw') == '080801000000000000000082bf009600' and
            adjacent_packets(prepare,row['packetId']))


def door_option_prelude(prepare, row):
    raw = row.get('raw','')
    mask, flags = row.get('mask'),row.get('extraDword')
    if (mask not in (8,0x28) or flags not in (0x2823F,0x282BF) or row.get('state') not in (1,2)
            or len(raw)!=(64 if mask==0x28 else 32) or not raw.endswith(f'000000000000{flags:08x}006400')
            or not adjacent_packets(prepare,row['packetId'])):
        return None
    return dict(Packet=row['packetId'],Mask=mask,Flags=flags)


class DoorArrivalObservations:
    def __init__(self):
        self.step = self.prepare = self.buff_status = self.arrival = None

    def ingest(self, row, *, teleport=False):
        kind, opcode = row['kind'], row.get('opcode')
        action = row.get('epoch'), row.get('recentActionId')
        if kind in ('client_action','context_reset'):
            self.step = row if opcode == 0x33 and action[1] is not None else None
            self.prepare = self.buff_status = self.arrival = None
            return
        if not self.step or action != (self.step.get('epoch'),self.step.get('recentActionId')):
            return
        source, target = self.step.get('map'), row.get('map')
        if kind == 'map':
            self.arrival = None
            prepared, self.prepare = self.prepare, None
            buff_status, self.buff_status = self.buff_status, None
            if teleport or not prepared or source is None or target is None or source == target:
                return
            epiloria_bank = {source%10000,target%10000} == {1002,1209}
            if (source//10000 != target//10000 or source//10000 >= 3
                    or max(source%10000,target%10000)>=1000 and not (epiloria_bank or guild_house_door(source,target))):
                return
            raw = bytes.fromhex(row['raw'])
            suffix = raw[10+raw[9]:]
            if suffix in (b'', b'\0'):
                self.arrival = dict(Source=source,Destination=target,MapPadding=bool(suffix),Track=None,
                                    MapPacket=row['packetId'],opcodes=[])
                if buff_status:
                    field, status = buff_status
                    packet = status['Packet'] if field=='OptionStatus' else status
                    if adjacent_packets(packet,row['packetId']):
                        self.arrival[field] = status
        elif kind == 'status' and source == target and self.prepare:
            if door_buff_prelude(self.prepare,row):
                self.buff_status = ('BuffStatusPacket',row['packetId'])
            elif (option_status := door_option_prelude(self.prepare,row)) and self.step.get('direction') in range(8):
                self.buff_status = ('OptionStatus',dict(option_status,Direction=self.step['direction']))
        elif kind == 'sound' and self.arrival and row.get('bgm'):
            track = row['id']
            if row.get('raw') == (b'\x19'+(0x8000|track).to_bytes(2,'big')+b'\xff\0\0').hex():
                self.arrival['Track'] = track
        elif kind == 'frame_header' and row['direction'] == 'S':
            if opcode == 0x67:
                self.prepare = row['packetId'] if source == target and row.get('plainLength') == 7 else None
                self.buff_status = self.arrival = None
            elif self.arrival and target == self.arrival['Destination'] and opcode != 0x33:
                self.arrival['opcodes'].append(opcode)
                if opcode == 0x58:
                    result, self.arrival = self.arrival, None
                    sequence = result.pop('opcodes')
                    expected = [0x14,0x04,0x07,0x1F]+([0x19] if result['Track'] is not None else [])+[0x58]
                    if row.get('plainLength') not in (2,3):
                        return
                    if sequence == expected:
                        return result
                    if guild_house_door(result['Source'],result['Destination']) and sequence == expected[:2]+expected[3:]:
                        return dict(result,OmitEmptyObjects=True)


def door_arrival_identity(row):
    return row['Source'],row['Destination'],row['MapPadding'],row['Track'],row.get('OmitEmptyObjects',False)


class Snapshot:
    def __init__(self, path, client):
        self.path = path
        self.client = client
        self.base = read_json(ROOT / "world-recorded.json")
        live = read_json(path) if path.is_file() else {}
        self.maps = {m["Id"]: m for m in live.get("Maps", [])}
        self.portals = {location(p): p for p in live.get("Portals", [])}
        self.door_arrivals = {door_arrival_identity(row):row for row in live.get('DoorArrivals', [])}
        self.items = {i["Key"]: i for i in live.get("Items", [])}
        self.npcs = {npc_identity(n): n for n in live.get("Npcs", [])}
        self.monsters = {location(m): m for m in live.get('Monsters', [])}
        self.training_targets = {location(row): row for row in live.get('TrainingTargets', [])}
        self.monster_loot = {loot_identity(rule):rule for rule in live.get('MonsterLoot', [])}
        self.monster_melee = {melee_identity(rule):rule for rule in live.get('MonsterMelee', [])}
        self.melee_packets = {packet:key for key,rule in self.monster_melee.items() for packet in rule['Samples']}
        self.monster_spell_damage = {spell_damage_identity(rule):rule for rule in live.get('MonsterSpellDamage', [])}
        self.spell_damage_packets = {packet for rule in self.monster_spell_damage.values() for packet in rule['Samples']}
        self.monster_kill_experience = {(row['Name'],row['Sprite'],row['Experience']):row
                                        for row in live.get('MonsterKillExperience', [])}
        self.kill_experience_packets = {packet for row in self.monster_kill_experience.values() for packet in row['Samples']}
        self.clock = live.get("Clock")
        self.base_monsters = {location(m): m for m in self.base['Monsters']}
        self.field_counts = Counter(spawn_zone(m) for m in (self.base_monsters | self.monsters).values())
        self.base_maps = {m["Id"] for m in self.base["Maps"]}
        self.base_portals = {location(p) for p in self.base["Portals"]}
        self.portal_arrivals = {location(p['Destination']) for p in self.base['Portals'] + list(self.portals.values())}
        self.base_npcs = {npc_identity(n): n for n in read_json(ROOT / "world-npcs.json")}
        self.base_npc_ids = {n["Id"] for n in self.base_npcs.values()}
        self.base_items = {i["Key"]:i for i in self.base["Items"]}
        self.item_descriptions = {}
        equipped_profiles = {key for key, _ in EQUIPPED_OPTION_PROFILES.values()}
        self.item_keys = {
            (i["Name"], i["Sprite"]): i["Key"] for i in self.base["Items"] + list(self.items.values())
            if i["Key"] not in equipped_profiles and not i.get('VariantOf')
        } | ALIASES
        self.next_npc = max(self.base_npc_ids | {n["Id"] for n in self.npcs.values()}) + 1
        self.dirty = False

    @functools.lru_cache(maxsize=64)
    def map_bytes(self, map_id):
        file_id = map_id % 10000 if map_id < 60000 else map_id
        path = self.client / f"Maps/{file_id:05d}.map"
        return path.read_bytes() if path.is_file() else b""

    def tile(self, map_id, x, y, floor):
        data = self.map_bytes(map_id)
        if len(data) < 4:
            return None
        width, height = data[2:4]
        if 0 <= x < width and 0 <= y < height and 0 <= floor < 4:
            return data[4 + ((floor * height + y) * width + x) * 5 + 4]
        return None

    def replace(self, table, key, value):
        if table.get(key) != value:
            table[key] = value
            self.dirty = True

    def observe_training_target(self, event, obj, session, observed_at):
        map_id = event['map']
        if (not 0 <= map_id < 30000 or map_id % 10000 not in (297, 298, 299)
                or obj['sprite'] != 0x400A or obj['name'] != '수련인형' or obj['flags'] != 6
                or not 0 <= obj['type'] < 8 or obj['floor'] != 0 or not event.get('packetId')
                or self.tile(map_id, obj['x'], obj['y'], obj['floor']) != 0):
            return
        row = dict(Map=map_id, X=obj['x'], Y=obj['y'], Floor=obj['floor'], Direction=obj['type'],
                   ObservedAt=round(observed_at * 1000), Packet=f"{session}/{event['packetId']}")
        key = location(row)
        previous = self.training_targets.get(key)
        if previous is None or row['ObservedAt'] > previous.get('ObservedAt', 0):
            self.training_targets[key] = row
            # Advance the observation in memory without rewriting the world for each S07 redraw.
            self.dirty |= previous is None or previous['Direction'] != row['Direction']

    def add_map(self, event):
        map_id = event["map"]
        name = event["name"].rstrip("\0")
        data = self.map_bytes(map_id)
        if (map_id not in self.base_maps and map_id not in self.maps
                and data[2:4] == bytes((event["width"], event["height"]))
                and 0 < len(name.encode("cp949")) <= 255):
            self.replace(self.maps, map_id, dict(Id=map_id, Name=name, Flags=event["flags"], Track=None))

    def observe_clock(self, date, hour):
        if date.get('epoch') != hour.get('epoch') or not 0 <= observed_time(hour) - observed_time(date) <= 1:
            return
        month = date.get('month', date.get('season'))
        day = date['day']
        minute = hour.get('minute')
        if minute is None and len(hour.get('raw', '')) == 6:
            minute = int(hour['raw'][4:6], 16)
        if month is None or not 1 <= month <= 12 or not 1 <= day <= 31 or minute is None or not 0 <= minute < 60 or not 0 <= hour['hour'] < 24:
            return
        if self.clock is not None and datetime.datetime.fromisoformat(self.clock['ObservedAt']).timestamp() >= observed_time(hour):
            return
        phase = self.clock.get('HourlyPulseAt') if self.clock else None
        self.clock = dict(ObservedAt=hour['time'], Month=month, Day=day, Hour=hour['hour'], Minute=minute)
        if phase is not None:
            self.clock['HourlyPulseAt'] = phase
        self.dirty = True

    def observe_hourly_clock(self, hour, day):
        if self.clock is None or not matches_hourly_clock(hour, day):
            return
        previous = self.clock.get('HourlyPulseAt')
        if previous:
            elapsed = observed_time(hour) - datetime.datetime.fromisoformat(previous).timestamp()
            # Preserve a stable phase through capture and network jitter.
            if elapsed <= 0 or abs(elapsed - round(elapsed / 3600) * 3600) < 1:
                return
        self.clock['HourlyPulseAt'] = hour['time']
        self.dirty = True

    def add_portal(self, source, target):
        if (source not in self.base_portals and source not in self.portals
                and self.tile(*source) is not None and (flags := self.tile(*target)) is not None
                and not flags & 0x80 and flags & 0x0F != 0x0F):
            map_id, x, y, floor = source
            self.replace(self.portals, source, dict(Map=map_id, X=x, Y=y, Floor=floor,
                Destination=dict(zip(("Map","X","Y","Floor"), target))))
            self.portal_arrivals.add(target)

    def observe_door_arrival(self, sample, session):
        key = door_arrival_identity(sample)
        existing = self.door_arrivals.get(key)
        if existing is None and not all(self.map_bytes(m) for m in key[:2]):
            return
        row = existing if existing is not None else {
            **{k:v for k,v in sample.items() if k not in ('BuffStatusPacket','OptionStatus')},
            'MapPacket':f"{session}/{sample['MapPacket']}"
        }
        if (status := sample.get('BuffStatusPacket')) and not row.get('BuffStatusPacket'):
            row = dict(row,BuffStatusPacket=f"{session}/{status}")
        if option := sample.get('OptionStatus'):
            observed = row.get('OptionStatuses',[])
            key_fields = ('Mask','Flags','Direction')
            if not any(all(p.get(k)==option[k] for k in key_fields) for p in observed):
                row = dict(row,OptionStatuses=[*observed,dict(option,Packet=f"{session}/{option['Packet']}")])
        if row != existing:
            self.replace(self.door_arrivals,key,row)

    def field_monster(self, map_id, obj):
        name, sprite = obj['name'], obj['sprite']
        if not 0 <= map_id < 60000:
            return None
        chest = name == '나무상자' and sprite in CHEST_SPRITES
        if chest:
            if map_id % 10000 not in HUNTING_MAPS and map_id % 10000 != 239:
                return None
        elif map_id % 10000 not in HUNTING_MAPS or MONSTER_SPRITES.get(name) != sprite or name not in self.base['MonsterHealth']:
            return None
        x, y, floor = obj['x'], obj['y'], obj['floor']
        at = (map_id, x, y, floor)
        flags = self.tile(*at)
        if ((map_id % 10000, y, floor) == (5, 7, 1) or at in self.base_portals or at in self.portals
                or at in self.portal_arrivals or (flags != 0 and not (map_id % 10000 == 232 and flags == 0x40))):
            return None
        return dict(Name=name, Sprite=sprite, Map=map_id, X=x, Y=y, Floor=floor,
                    Flags=obj['flags'], Direction=obj['type'] if obj['type'] <= 7 else 0)

    def observe_field(self, zone, active, now):
        # A new pass can only raise the count in a zone. Repeated walks past the
        # same roaming object and historical respawns do not append new slots.
        for oid, (seen, _) in tuple(active.items()):
            if now - seen > 120:
                del active[oid]
        positions = {location(row): row for _, row in active.values()}
        needed = len(positions) - self.field_counts[zone]
        for at, row in positions.items():
            if needed <= 0:
                break
            if at in self.base_monsters or at in self.monsters:
                continue
            self.replace(self.monsters, at, row)
            self.field_counts[zone] += 1
            needed -= 1

    def observe_monster_loot(self, sample, session, *, confirmed=False):
        if (sample['name'] == '나무상자' or not sample['hit'] or not sample['packetId']
                or not sample['nearby_drops'] and not confirmed):
            return
        drops = []
        new_items = {}
        for item in sample['nearby_drops']:
            pair = item['name'],item['sprite']
            if pair == ('GALRID',0x8BB8):
                key = 'galrid'
            elif (key := self.item_keys.get(pair)) is None:
                if pair not in FIELD_LOOT_ITEMS:
                    return
                key = f"recorded-{pair[1]:04x}-{pair[0].encode('cp949').hex()}"
                definition = dict(Key=key,Name=pair[0],Sprite=pair[1])
                if pair == ('빛나는가루',0x8FC6):
                    definition.update(InventoryWord3=1,InventoryFlag1=1)
                new_items[pair] = definition
            if not 0 < item['quantity'] <= 0xFFFFFFFF:
                return
            drops.append(dict(Key=key,Quantity=item['quantity']))
        for pair, definition in new_items.items():
            self.item_keys[pair] = definition['Key']
            self.replace(self.items, definition['Key'], definition)
        rule = dict(Map=sample['map']%10000,Name=sample['name'],Sprite=sample['sprite'],Drops=drops)
        identity = loot_identity(rule)
        previous = self.monster_loot.get(identity, {})
        samples = previous.get('Samples', [])
        confirmed_samples = previous.get('ConfirmedSamples', [])
        packet = f"{session}/{sample['packetId']}"
        if packet not in samples or confirmed and packet not in confirmed_samples:
            rule['Samples'] = samples if packet in samples else [*samples,packet]
            if confirmed and packet not in confirmed_samples:
                confirmed_samples = [*confirmed_samples,packet]
            if confirmed_samples:
                rule['ConfirmedSamples'] = confirmed_samples
            self.replace(self.monster_loot, identity, rule)

    def observe_monster_melee(self, sample, session):
        if sample['Armor'] is None or not sample['Strike']:
            return
        packet = f"{session}/{sample['Strike']}"
        key = melee_identity(sample)
        if (previous := self.melee_packets.get(packet)) is not None:
            if previous == key or previous[:-2] != key[:-2] or any(
                    old is not None and old != new for old,new in zip(previous[-2:],key[-2:])):
                return
            legacy = self.monster_melee[previous]['Samples']
            legacy.remove(packet)
            if not legacy:
                del self.monster_melee[previous]
        self.melee_packets[packet] = key
        if key not in self.monster_melee:
            self.monster_melee[key] = dict(zip(MELEE_FIELDS,key),Samples=[])
        self.monster_melee[key]['Samples'].append(packet)
        self.dirty = True

    def observe_kill_experience(self, sample, session):
        packet = f"{session}/{sample['Kill']}"
        if packet in self.kill_experience_packets:
            return
        self.kill_experience_packets.add(packet)
        key = sample['Name'], sample['Sprite'], sample['Experience']
        if key not in self.monster_kill_experience:
            self.monster_kill_experience[key] = dict(Name=key[0],Sprite=key[1],Experience=key[2],Samples=[])
        self.monster_kill_experience[key]['Samples'].append(packet)
        self.dirty = True

    def observe_spell_damage(self, sample, session):
        packet = f"{session}/{sample['Strike']}"
        if packet in self.spell_damage_packets:
            return
        self.spell_damage_packets.add(packet)
        key = spell_damage_identity(sample)
        if key not in self.monster_spell_damage:
            self.monster_spell_damage[key] = dict(zip(SPELL_DAMAGE_FIELDS,key),Samples=[])
        self.monster_spell_damage[key]['Samples'].append(packet)
        self.dirty = True

    def npc(self, map_id, obj):
        row = dict(Map=map_id, X=obj["x"], Y=obj["y"], Floor=obj["floor"],
                   Sprite=obj["sprite"], Name=obj["name"], Flags=obj["flags"], Type=obj["type"])
        identity = npc_identity(row)
        current = self.npcs.get(identity) or self.base_npcs.get(identity)
        if current is not None:
            return identity
        if (not row["Name"] or len(row["Name"].encode("cp949")) > 255
                or self.tile(*location(row)) is None):
            return None
        row.update(Id=self.next_npc, Text=row["Name"])
        self.next_npc += 1
        self.replace(self.npcs, identity, row)
        return identity

    def item_stats(self, pair, description, equipment_slot=None, option_packet=None):
        if not description or not description.strip() or len(description.encode("cp949")) > 4096:
            return
        existing = self.item_descriptions.setdefault(pair, description)
        fields = parse_item_stats(description)
        if fields and existing != description and not parse_item_stats(existing):
            self.item_descriptions[pair] = description
        name, sprite = pair
        key, name = EQUIPPED_OPTION_PROFILES.get(pair, (self.item_keys.get(pair), name))
        if key == "madeia-ring-left" and equipment_slot == 8:
            key = "madeia-ring-right"
        if not key:
            return
        item = self.items.get(key) or self.base_items.get(key) or dict(Key=key,Name=name,Sprite=sprite)
        prior = item.get('StatDescription')
        prior_fields = parse_item_stats(prior) if prior else []
        if not prior or (fields and not prior_fields):
            self.replace(self.items,key,dict(item,StatDescription=description))
        elif option_packet and equipment_slot and item.get('EquipmentSlot') == equipment_slot and fields and fields != prior_fields:
            suffix = hashlib.sha256(description.encode('cp949')).hexdigest()[:16]
            variant = f'{key}-options-{suffix}'
            if variant not in self.items:
                self.replace(self.items,variant,dict(Key=variant,Name=name,Sprite=sprite,EquipmentSlot=equipment_slot,
                    VariantOf=key,StatDescription=description,OptionPacket=option_packet))

    def equipment_appearance(self, equipment, layer, word, human, session):
        pair = equipment["name"], equipment["wireSprite"]
        key, name = EQUIPPED_OPTION_PROFILES.get(pair, (self.item_keys.get(pair), pair[0]))
        if not key:
            key = f"recorded-{pair[1]:04x}-{pair[0].encode('cp949').hex()}"
            self.item_keys[pair] = key
        item = self.items.get(key) or self.base_items.get(key) or dict(Key=key,Name=name,Sprite=pair[1])
        if key in self.items and item.get("AppearancePacket") and item["AppearanceWord"] != word:
            return
        self.replace(self.items,key,dict(item,EquipmentSlot=equipment['slot'],AppearanceLayer=layer,AppearanceWord=word,
            AppearancePacket=(item.get('AppearancePacket') if key in self.items else None) or f"{session}/{human['packetId']}",
            EquipmentWord1=equipment["word1"],EquipmentWord2=equipment["word2"]))
        self.equipment_flag(equipment)
        self.item_stats(pair,equipment.get("description"),equipment_slot=equipment['slot'],
                        option_packet=f"{session}/{equipment['packetId']}" if equipment.get('packetId') else None)

    def equipment_flag(self, equipment):
        flag = equipment.get('flag')
        if flag not in (0,1):
            return
        pair = equipment['name'],equipment['wireSprite']
        slot = equipment['slot']
        key, name = EQUIPPED_OPTION_PROFILES.get(pair, (self.item_keys.get(pair), pair[0]))
        if key == 'madeia-ring-left' and slot == 8:
            key = 'madeia-ring-right'
        if key is None:
            # Necklaces and rings have no S33 layer to correlate with S37.
            if slot not in ACCESSORY_SLOTS:
                return
            key = f"recorded-{pair[1]:04x}-{pair[0].encode('cp949').hex()}"
            self.item_keys[pair] = key
        item = self.items.get(key) or self.base_items.get(key) or dict(Key=key,Name=name,Sprite=pair[1])
        updated = dict(item,Flag=flag)
        if slot in ACCESSORY_SLOTS and not item.get('EquipmentSlot'):
            updated.update(EquipmentSlot=slot,EquipmentWord2=equipment['word2'])
        self.replace(self.items,key,updated)

    def item_timer(self, inventory, timer):
        pair = inventory['name'], inventory['wireSprite']
        key = self.item_keys.get(pair)
        if key is None or not timer['duration']:
            return
        item = self.items.get(key) or self.base_items.get(key)
        if item:
            self.replace(self.items, key, dict(item, Timer=dict(Id=timer['id'], Name=timer['name'], Duration=timer['duration'])))

    def shop(self, identity, items, stock_samples=()):
        if len(items) > 200 or len({i["name"] for i in items}) != len(items):
            return
        npc = self.npcs.get(identity) or self.base_npcs[identity]
        # A configured merchant uses its current wire layout even if an older snapshot used another one.
        extended = self.base_npcs.get(identity,npc).get("ExtendedShop",False)
        if any(not item["name"] or item["sprite"] == 0 or
               any(len(item[field].encode("cp949")) > 255 for field in ("name", "displayName")) or
               len((item.get("description") or "").encode("cp949")) > (4096 if extended else 255)
               for item in items):
            return
        old = {i["Key"]: i for i in npc.get("Shop", [])}
        used = {i.get("Id", 0) for i in old.values()}
        observed_stock = {(row['name'],row['sprite']):row for row in stock_samples}
        stock = []
        for item in items:
            pair = item["name"], item["sprite"]
            key = self.item_keys.get(pair)
            if key is None:
                key = f"recorded-{pair[1]:04x}-{pair[0].encode('cp949').hex()}"
                self.replace(self.items, key, dict(Key=key, Name=pair[0], Sprite=pair[1]))
                self.item_keys[pair] = key
                self.item_stats(pair,self.item_descriptions.get(pair))
            previous = old.get(key, {})
            description = item.get("description") or ""
            if parse_item_stats(description):
                self.item_stats(pair,description)
            capacity = max(item["flags"], previous.get("RestockCapacity", previous.get("Flags", 0)))
            line = dict(Key=key, Price=item["price"], Flags=item["flags"], RestockCapacity=capacity)
            fixed = previous.get("FixedStock", False)
            if stock_sample := observed_stock.get(pair):
                fixed = stock_sample['before'] == stock_sample['after']
            if fixed:
                line["FixedStock"] = True
            if previous.get("SellPrice"):
                line["SellPrice"] = previous["SellPrice"]
            if extended:
                item_id = previous["Id"] if previous and previous.get("Id") else max(used, default=0) + 1
                used.add(item_id)
                line["Id"] = item_id
                if description.strip() or previous.get("Description"):
                    line["Description"] = description if description.strip() else previous["Description"]
            else:
                if item["displayName"] != item["name"]:
                    line["DisplayName"] = item["displayName"]
                if item.get('description') is not None:
                    line["Description"] = description
            stock.append(line)
        if stock != npc.get("Shop") or extended != npc.get("ExtendedShop",False):
            updated = dict(npc, Shop=stock)
            if extended:
                updated["ExtendedShop"] = True
            else:
                updated.pop("ExtendedShop",None)
            self.replace(self.npcs, identity, updated)

    def sell_prices(self, identity, offers, inventory):
        npc = self.npcs.get(identity) or self.base_npcs[identity]
        if not npc.get('Shop'):
            return
        quotes = defaultdict(set)
        for offer in offers:
            item = inventory.get(offer['slot'])
            if not item or item.get('byte3') not in (100, 255) or not offer['price']:
                continue
            if key := self.item_keys.get(((item.get('name') or '').rstrip(' '), item.get('wireSprite'))):
                quotes[key].add(offer['price'])
        prices = {key: next(iter(values)) for key, values in quotes.items() if len(values) == 1}
        if prices:
            stock = [dict(row, SellPrice=prices[row['Key']]) if row['Key'] in prices else row for row in npc['Shop']]
            self.replace(self.npcs, identity, dict(npc, Shop=stock))

    def dialog(self, identity, request, packet):
        if packet["flags"] not in (1,14) or packet["mode"] not in (0,1,12,13):
            return
        npc = self.npcs.get(identity) or self.base_npcs[identity]
        key = "OpenDialog" if request["opcode"] in (0x16,0x43) else "DialogReplies"
        fields = dict(Mode=packet["mode"], Flags=packet["flags"], PortraitKind=packet["unknown"],
                      Sprite=packet["sprite"], Color=packet["color"], HeaderData=packet["headerData"],
                      Text=packet["text"])
        for field in ("prompt","x","y","menuCodes","actionCode","actionText"):
            if field in packet:
                fields[field[0].upper() + field[1:]] = packet[field]
        if "options" in packet:
            fields["Options"] = [dict(Text=o["text"], Id=o["id"]) for o in packet["options"]]
        if key == "OpenDialog":
            if request["length"] == 6:
                self.replace(self.npcs, identity, dict(npc, OpenDialog=fields))
        elif "menu" in request and request["length"] in (8,10):
            signature = (request["mode"], request["menu"], request["length"])
            replies = [r for r in npc.get(key, [])
                       if (r["Flags"], r["Menu"], r["Length"]) != signature]
            replies.append(dict(Flags=signature[0], Menu=signature[1], Length=signature[2], Dialog=fields))
            self.replace(self.npcs, identity, dict(npc, DialogReplies=replies))

    def pursuit(self, identity, request, event, parent):
        key = pursuit_key(request, parent)
        if key is None:
            return
        packet = sanitized_packet(bytes.fromhex(event['raw']))
        if packet is None or packet[0] != 0x30:
            return
        # The runtime fills in the local NPC ID. Store only the native dialog bytes.
        packet = packet[:3] + bytes(4) + packet[7:]
        npc = self.npcs.get(identity) or self.base_npcs[identity]
        replies = {r['Request']:r for r in npc.get('PursuitReplies', [])}
        replies[key] = dict(Request=key, Packet=base64.b64encode(packet).decode('ascii'))
        self.replace(self.npcs, identity, dict(npc, PursuitReplies=list(replies.values())))
        return key

    def pursuit_travel(self, identity, key, position, sound):
        map_id, x, y, floor = (position[k] for k in ('map', 'x', 'y', 'floor'))
        flags = self.tile(map_id, x, y, floor)
        if flags is None or flags & 0x80 or flags & 0xf == 0xf:
            return
        npc = self.npcs.get(identity) or self.base_npcs[identity]
        routes = {r['Request']:r for r in npc.get('PursuitTravel', [])}
        routes[key] = dict(Request=key, Destination=dict(Map=map_id, X=x, Y=y, Floor=floor), Sound=sound)
        self.replace(self.npcs, identity, dict(npc, PursuitTravel=list(routes.values())))

    def publish(self):
        if not self.dirty:
            return
        data = dict(Maps=list(self.maps.values()), Portals=list(self.portals.values()),
                    DoorArrivals=list(self.door_arrivals.values()),
                    Items=list(self.items.values()), Npcs=list(self.npcs.values()), Monsters=list(self.monsters.values()),
                    TrainingTargets=[row for _, row in sorted(self.training_targets.items())],
                    MonsterLoot=list(self.monster_loot.values()), MonsterMelee=list(self.monster_melee.values()),
                    MonsterSpellDamage=list(self.monster_spell_damage.values()),
                    MonsterKillExperience=list(self.monster_kill_experience.values()))
        counts = ", ".join(f"{len(v)} {k.lower()}" for k,v in data.items())
        if self.clock is not None:
            data["Clock"] = self.clock
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temp, self.path)
        self.dirty = False
        print("Published " + counts, flush=True)


class ShopStockObservations:
    def __init__(self):
        self.catalog = self.request = None
        self.grants = []
        self.balance = None
        self.last_gold = None
        self.debit = None
        self.debit_pending = False
        self.refunded = False

    def ingest(self, event):
        kind = event['kind']
        scope = event.get('epoch'), event.get('map')
        if kind in ('map', 'context_reset'):
            self.catalog = self.request = None
            if kind == 'context_reset':
                self.last_gold = None
        elif kind == 'client_action':
            self.request = None
            self.grants.clear()
            self.balance = None
            self.debit = None
            self.debit_pending = False
            self.refunded = False
            if (event.get('opcode') == 0x39 and event.get('menu') in (0x40, 0x4B) and event.get('mode') == 1
                    and event.get('recentActionId') is not None):
                self.request = event
                if event['menu'] == 0x40:
                    self.catalog = None
            elif event.get('opcode') in (0x16, 0x39, 0x3A, 0x43):
                self.catalog = None
        gold = event.get('gold') if kind == 'status' else None
        debit = None
        if gold is not None:
            if self.last_gold is not None and self.last_gold[0]==scope[0]:
                debit = self.last_gold[1]-gold
            self.last_gold = scope[0],gold
        request = self.request
        if not request or scope != (request.get('epoch'), request.get('map')) or event.get('recentActionId') != request.get('recentActionId'):
            return None
        if not 0 <= observed_time(event) - observed_time(request) <= 2:
            self.request = None
            return None
        if gold is not None:
            if self.balance is not None and gold > self.balance:
                self.refunded = True
            self.balance = gold
            self.debit, self.debit_pending = debit, True
        elif kind == 'inventory_add' and self.balance is not None and event.get('wireSprite') != 0x8BB8:
            if self.refunded:
                self.request = None
                return None
            name, display, sprite = event.get('name'), event.get('displayName'), event.get('wireSprite')
            if (name and display and name!=display and self.catalog
                    and (name.rstrip(' ')==display or display=='지렁이병(대)' and fishing_bait_remaining(name,sprite) is not None)
                    and not any((item['name'],item['sprite'])==(name,sprite) for item in self.catalog['items'])):
                name = display
            pair = name,sprite
            if not self.grants or self.grants[-1][0]!=pair:
                self.grants.append((pair,self.debit if self.debit_pending else None))
            elif self.debit_pending:
                prior = self.grants[-1][1]
                total = prior+self.debit if prior is not None and self.debit is not None else None
                self.grants[-1] = pair,total
            self.debit_pending = False
        elif (kind == 'merchant_dialog' and event.get('mode') == 10 and event.get('flags') == 1
              and event['oid'] == request.get('targetId') and 'items' in event):
            before = self.catalog
            self.catalog = event
            self.request = None
            lines = request.get('orderLines', [])
            if (request['menu'] != 0x4B or not self.grants or len(self.grants)>len(lines)
                    or len({pair for pair,_ in self.grants})!=len(self.grants)
                    or not before or before['oid'] != event['oid']
                    or (before.get('epoch'),before.get('map'))!=scope):
                return None
            samples=[]
            gaps = len(self.grants)<len(lines) and not self.refunded
            remaining_lines = iter(lines)
            for (name,sprite), charge in self.grants:
                if not name:
                    return None
                try:
                    name_bytes = len(name.encode('cp949'))
                except UnicodeEncodeError:
                    return None
                old, new = ([i for i in catalog['items'] if (i['name'],i['sprite'])==(name,sprite)]
                            for catalog in (before,event))
                if len(old)!=1 or len(new)!=1:
                    return None
                old, new = old[0], new[0]
                price = old['price']
                if not price or price!=new['price']:
                    return None
                if gaps:
                    # An omitted line can only be bypassed when the exact debit proves the grant's quantity.
                    if charge is None or charge<=0:
                        return None
                    quantity, remainder = divmod(charge,price)
                    if remainder or not any(line.get('nameBytes')==name_bytes and line.get('quantity')==quantity
                                            for line in remaining_lines):
                        return None
                else:
                    line = next(remaining_lines)
                    if line.get('nameBytes',name_bytes)!=name_bytes:
                        return None
                    quantity = line.get('quantity',0)
                if not 0<quantity<=old['flags'] or old['flags']-new['flags'] not in (0,quantity):
                    return None
                samples.append(dict(name=name,sprite=sprite,quantity=quantity,price=price,
                                    before=old['flags'],after=new['flags'],previous_catalog=before.get('packetId'),
                                    order=request.get('packetId'),refreshed_catalog=event.get('packetId')))
                if gaps:
                    samples[-1]['debited'] = charge
            return samples


class Capture:
    def __init__(self, session=None):
        self.session = session
        self.offset = 0
        self.position = self.step = self.map = self.portal = self.destination_choice = None
        self.objects = {}
        self.npc_requests = {}
        self.pursuit_paths = {}
        self.npc_travel = None
        self.item_use = self.item_timer = None
        self.equipment_appearances = EquipmentAppearances()
        self.shop_stock = ShopStockObservations()
        self.shop_inventory = {}
        self.door_arrivals = DoorArrivalObservations()
        self.clock_date = self.clock_hour = None
        self.field_map = None
        self.field_ids = {}
        self.field_zones = defaultdict(dict)
        self.drop_observations = DropObservations()
        self.loot_confirmation = LootConfirmation()
        self.melee_observations = MeleeObservations()
        self.spell_damage_observations = SpellDamageObservations()
        self.kill_experience_observations = KillFameObservations()

    def observe_npc_travel(self, event, world):
        kind, map_id = event['kind'], event.get('map')
        if kind in ('client_action', 'context_reset'):
            self.npc_travel = None
            if event.get('opcode') == 0x3A and event.get('dialogChoice') == 1 and event.get('recentActionId') is not None:
                speaker = (map_id, event.get('targetId'))
                identity = self.objects.get(speaker)
                if identity and identity[-1] == '티포네' and map_id % 10000 == 210:
                    key = pursuit_key(event, self.pursuit_paths.get(speaker))
                    if key in FREE_TIPONE_ROUTES:
                        self.npc_travel = dict(request=event, identity=identity, key=key, sound=None, destination=None)
            return
        if not self.npc_travel or kind not in ('sound', 'map', 'self_position'):
            return
        travel = self.npc_travel
        request = travel['request']
        if (event.get('epoch') != request.get('epoch') or event.get('recentActionId') != request['recentActionId']
                or not 0 <= observed_time(event) - observed_time(request) <= 1):
            self.npc_travel = None
        elif kind == 'sound' and map_id == request['map'] and not event.get('bgm') and event.get('length') == 4:
            travel['sound'] = event['id']
        elif kind == 'map' and map_id != request['map']:
            travel['destination'] = map_id
        elif kind == 'self_position' and map_id == travel['destination'] and map_id is not None:
            world.pursuit_travel(travel['identity'], travel['key'], event, travel['sound'])
            self.npc_travel = None

    def ingest(self, event, world):
        kind, map_id = event["kind"], event.get("map")
        stock_samples = self.shop_stock.ingest(event)
        if kind == 'context_reset':
            self.shop_inventory.clear()
        elif kind == 'inventory_add' and 'slot' in event:
            self.shop_inventory[event['slot']] = event
        elif kind == 'inventory_remove':
            self.shop_inventory.pop(event.get('slot'), None)
        teleport = False
        if kind == 'client_action' and event.get('opcode') == 0x16:
            self.destination_choice = event
        elif kind in ('map', 'context_reset'):
            teleport = kind == 'map' and matches_destination_choice(self.destination_choice, event)
            self.destination_choice = None
        if (door := self.door_arrivals.ingest(event,teleport=teleport)) is not None:
            world.observe_door_arrival(door,self.session)
        if appearance := self.equipment_appearances.ingest(event):
            world.equipment_appearance(*appearance,event,self.session)
        self.observe_npc_travel(event, world)
        defeated = self.drop_observations.ingest(event)
        if defeated is not None:
            world.observe_monster_loot(defeated,self.session)
        if (confirmed := self.loot_confirmation.ingest(event,defeated)) is not None:
            world.observe_monster_loot(confirmed,self.session,confirmed=True)
        if (sample := self.melee_observations.ingest(event,self.drop_observations.active)) is not None:
            world.observe_monster_melee(sample,self.session)
        if (sample := self.spell_damage_observations.ingest(event,self.drop_observations.active)) is not None:
            world.observe_spell_damage(sample,self.session)
        if (sample := self.kill_experience_observations.ingest(event,defeated)) is not None:
            world.observe_kill_experience(sample,self.session)
        if kind == 'context_reset' or map_id != self.field_map:
            self.field_map = map_id
            self.field_ids.clear()
            self.field_zones.clear()
        if kind == 'death' and (zone := self.field_ids.get(event['oid'])) is not None:
            self.field_zones[zone].pop(event['oid'], None)
        if kind == "client_action" and event.get("opcode") == 0x33:
            self.step = event
        elif kind == 'client_action' and event.get('opcode') == 0x1C and 'slot' in event:
            self.item_use, self.item_timer = event, None
        elif kind == "client_action" and event.get("opcode") in (0x16,0x39,0x3A,0x43) and event.get("targetId"):
            key = (map_id,event["targetId"])
            self.npc_requests[key] = event
            if event['opcode'] != 0x3A:
                self.pursuit_paths.pop(key, None)
        elif kind == "context_snapshot" and event.get("position") and map_id is not None:
            self.position = dict(event["position"],map=map_id)
        elif kind == "context_reset":
            self.position = self.step = self.map = self.portal = None
            self.objects.clear()
            self.npc_requests.clear()
            self.pursuit_paths.clear()
            self.item_use = self.item_timer = None
            self.clock_date = self.clock_hour = None
        elif kind == 'world_day':
            self.clock_date = event
        elif kind == 'world_hour':
            if self.clock_date:
                world.observe_clock(self.clock_date, event)
                self.clock_date = None
            self.clock_hour = event
        elif kind == 'world_day_tick':
            world.observe_hourly_clock(self.clock_hour, event)
        elif kind == "map":
            if self.map is None or self.map["map"] != map_id:
                self.npc_requests.clear()
                self.pursuit_paths.clear()
            self.map = event
            world.add_map(event)
            self.portal = None
            if (not teleport and self.position and self.step and self.position["map"] == self.step["map"] != map_id
                    and 0 <= observed_time(event) - observed_time(self.step) < .3
                    and (not event.get("recentActionId") or self.step.get("recentActionId")==event["recentActionId"])):
                dx, dy = DELTAS[self.step["direction"]]
                self.portal = (self.position["map"], self.position["x"] + dx,
                               self.position["y"] + dy, self.position["floor"])
        elif kind == "self_position" and self.portal:
            world.add_portal(self.portal, (map_id, event["x"], event["y"], event["floor"]))
            self.portal = None
        elif kind == "sound" and event.get("bgm") and self.map and map_id == self.map["map"]:
            row = world.maps.get(map_id)
            if row and row["Track"] is None and observed_time(event) - observed_time(self.map) < 2:
                world.replace(world.maps, map_id, dict(row, Track=event["id"]))
        elif kind == "objects" and map_id is not None:
            changed = set()
            seen = observed_time(event)
            for obj in event["objects"]:
                if obj["category"] == 1:
                    identity = world.npc(map_id, obj)
                    if identity is not None:
                        self.objects[(map_id, obj["oid"])] = identity
                elif obj['category'] == 0:
                    if obj['sprite'] == 0x400A:
                        world.observe_training_target(event, obj, self.session, seen)
                    elif obj['oid'] not in self.field_ids and (row := world.field_monster(map_id, obj)) is not None:
                        zone = spawn_zone(row)
                        self.field_ids[obj['oid']] = zone
                        self.field_zones[zone][obj['oid']] = (seen, row)
                        changed.add(zone)
            for zone in sorted(changed):
                world.observe_field(zone, self.field_zones[zone], seen)
        elif kind in ('inventory_add','equipment_add'):
            if kind == 'equipment_add':
                world.equipment_flag(event)
            if event.get('description'):
                slot = event['slot'] if kind == 'equipment_add' else None
                packet = f"{self.session}/{event['packetId']}" if slot and event.get('packetId') else None
                world.item_stats((event['name'],event['wireSprite']),event['description'],slot,packet)
            if kind == 'inventory_add' and self.item_timer and self.item_use['slot'] == event.get('slot') and \
                    map_id == self.item_use.get('map') and 0 <= observed_time(event) - observed_time(self.item_timer) <= .5:
                world.item_timer(event, self.item_timer)
                self.item_use = self.item_timer = None
        elif kind == 'timed_spell' and self.item_use and map_id == self.item_use.get('map') and \
                0 <= observed_time(event) - observed_time(self.item_use) <= .5:
            self.item_timer = event
        elif kind == "merchant_dialog":
            key = (map_id,event["oid"])
            identity = self.objects.get(key)
            request = self.npc_requests.pop(key, None)
            if identity and event["mode"] == 10 and event["flags"] in (1,14) and identity[-1] == event["text"]:
                world.shop(identity, event["items"], stock_samples or ())
            elif (identity and request and event['mode'] == 11 and event['flags'] == 1 and identity[-1] == event['text']
                  and request['opcode'] == 0x39 and request.get('menu') in (0x41, 0x4E)
                  and request.get('recentActionId') == event.get('recentActionId')
                  and 0 <= observed_time(event) - observed_time(request) <= 1):
                packet = decode(bytes.fromhex(event['raw']))
                world.sell_prices(identity, packet['offers'], self.shop_inventory)
            elif identity and request and 0 <= observed_time(event) - observed_time(request) <= 1:
                packet = decode(bytes.fromhex(event["raw"]))
                world.dialog(identity, request, packet)
        elif kind == 'npc_pursuit':
            key = (map_id, event['oid'])
            identity = self.objects.get(key)
            request = self.npc_requests.pop(key, None)
            if identity and request and 0 <= observed_time(event) - observed_time(request) <= 1:
                self.pursuit_paths[key] = world.pursuit(identity, request, event, self.pursuit_paths.get(key))
        if kind in ("self_position", "self_move"):
            self.position = event
            if kind == "self_move" and map_id is not None and not event.get("correction"):
                flags = world.tile(map_id,event["x"],event["y"],event["floor"])
                if flags is not None and flags & 0x30:
                    floor = event["floor"] + (1 if flags & 0x10 else -1)
                    if world.tile(map_id,event["x"],event["y"],floor) is not None:
                        self.position = dict(event,floor=floor)

    def read(self, path, world, *, on_skip=None):
        self.session = path.parent.name
        if path.stat().st_size < self.offset:
            self.__init__(self.session)
        with path.open("rb") as stream:
            stream.seek(self.offset)
            while (line := stream.readline()).endswith(b"\n"):
                self.offset = stream.tell()
                try:
                    self.ingest(json.loads(line.decode("utf-8-sig")), world)
                except (ValueError, TypeError, KeyError, IndexError) as exc:
                    if on_skip is None:
                        print(f"Skipped a record in {path.parent.name} at byte {self.offset}: {type(exc).__name__}", flush=True)
                    else:
                        on_skip()


def excluded_capture(directory):
    return (directory / 'exclude-from-live-import').is_file()


def client_directory(explicit=None):
    configured = explicit or os.environ.get("ELANCIA_CLIENT_DIR")
    if configured:
        return Path(configured).expanduser().resolve()
    return Path(read_json(ROOT / "evidence/packet-catalog.json")["client"]).parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--captures", type=Path, default=RECORDER / "captures")
    parser.add_argument("--output", type=Path, default=ROOT / "data/world-live.json")
    parser.add_argument("--client-dir", type=Path)
    parser.add_argument("--session", action="append", help="Apply only the named capture directory; repeat as needed")
    parser.add_argument("--watch", action="store_true")
    parser.add_argument("--server-pid", type=int)
    args = parser.parse_args()
    if not args.captures.is_dir():
        parser.error("Recorder capture directory was not found")
    client = client_directory(args.client_dir)
    paths = sorted(args.captures.glob("*/events.jsonl"))
    if args.session:
        selected = set(args.session)
        paths = [path for path in paths if path.parent.name in selected]
        if missing := selected - {path.parent.name for path in paths}:
            parser.error("Capture session not found: " + ", ".join(sorted(missing)))
    world = Snapshot(args.output, client)
    cutoff = min(world.base.get("SourceSessions", ()), default="")
    readers = {}
    handle = None
    if args.server_pid:
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        handle = kernel.OpenProcess(0x00100000, False, args.server_pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
    try:
        while not handle or kernel.WaitForSingleObject(handle, 0) == 258:
            try:
                for path in sorted(args.captures.glob("*/events.jsonl")) if args.watch and not args.session else paths:
                    if (args.session or path.parent.name >= cutoff) and not excluded_capture(path.parent):
                        if path not in readers:
                            readers[path] = Capture()
                        readers[path].read(path, world)
                world.publish()
            except OSError as exc:
                if not args.watch:
                    raise
                print(f"Recorded world sync will retry: {exc}", flush=True)
            if not args.watch:
                return
            time.sleep(2)
    finally:
        if handle:
            kernel.CloseHandle(handle)


if __name__ == "__main__":
    main()
