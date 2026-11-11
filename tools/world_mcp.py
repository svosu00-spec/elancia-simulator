# /// script
# requires-python = ">=3.11"
# dependencies = ["mcp==2.3.0"]
# ///
"""Inspect Elancia captures and preview local world data imports over MCP."""

import datetime
import json
import os
import re
import zlib
from collections import Counter, deque
from pathlib import Path
from statistics import median
from typing import Annotated, Any

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from client_assets import equipment_layer_assets, metadata_index
from sync_recorded_world import (DELTAS, EQUIPMENT_LAYERS, MONSTER_SPRITES, RECORDER, ROOT, Capture, DropObservations, EquipmentAppearances, KillFameObservations, LootConfirmation, client_directory,
                                MeleeObservations, ShopStockObservations, Snapshot, SpellDamageObservations, adjacent_packets, door_buff_prelude, door_option_prelude, excluded_capture, location, matches_destination_choice, matches_hourly_clock, observed_time, read_json, status_fame, training_target_baseline)
from recorder_core import EXTENDED_PACKET_KINDS, MERCHANT_LIST_MODES, UNREGISTERED_SERVER_OPCODES, Reader, decode, decoration_catalog, fishing_bait_remaining, item_stats, metadata_blocks, sanitized_packet, valid_party_notice_affix

CAPTURES = Path(os.environ.get("ELANCIA_CAPTURE_DIR", str(RECORDER / "captures"))).expanduser().resolve()
LIVE = ROOT / "data" / "world-live.json"
SESSION_ID = re.compile(r"[0-9]{8}-[0-9]{6}-[0-9]{6}")
TABLES = ("Maps", "Portals", "DoorArrivals", "Items", "Npcs", "Monsters", "TrainingTargets")

server = MCPServer("elancia-local", description="Inspect recorder sessions and local world data.")
READ_ONLY = ToolAnnotations(read_only_hint=True, open_world_hint=False)


def capture_file(session, filename):
    if not SESSION_ID.fullmatch(session):
        raise ToolError("기록 ID는 YYYYMMDD-HHMMSS-ffffff 형식이어야 합니다.")
    directory = CAPTURES.resolve() / session
    path = directory / filename
    if path.resolve() != path:
        raise ToolError("기록 폴더 안의 일반 파일만 조회할 수 있습니다.")
    return path


def recent_sessions(*, require_packets=False):
    sessions = []
    for directory in sorted(CAPTURES.glob("*"), reverse=True):
        if SESSION_ID.fullmatch(directory.name):
            try:
                events = capture_file(directory.name, "events.jsonl")
            except ToolError:
                continue
            if events.is_file() and not excluded_capture(directory):
                summary = directory / 'summary.json'
                if require_packets and summary.is_file():
                    try:
                        report = read_json(summary)
                    except (OSError, ValueError):
                        continue
                    if not (report.get('frameCounts') or report.get('packetCounts')):
                        continue
                sessions.append(directory.name)
    return sessions


def session_events(session):
    if session == "latest":
        sessions = recent_sessions(require_packets=True)
        if not sessions:
            raise ToolError("이벤트를 기록한 세션이 없습니다.")
        session = sessions[0]
    path = capture_file(session, "events.jsonl")
    if not path.is_file():
        raise ToolError("해당 기록 세션을 찾을 수 없습니다.")
    if excluded_capture(path.parent):
        raise ToolError("로컬 테스트로 분류되어 본서버 분석에서 제외된 기록입니다.")
    return path


def utc_timestamp(seconds):
    return datetime.datetime.fromtimestamp(seconds, datetime.timezone.utc).isoformat()


def recorded_rows(path):
    with path.open("rb") as stream:
        while (line := stream.readline()).endswith(b"\n"):
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if (row.get('kind') == 'merchant_dialog' and row.get('mode') in MERCHANT_LIST_MODES
                    and 'retainedLength' not in row):
                try:
                    packet = bytes.fromhex(row['raw'])
                    safe = sanitized_packet(packet) if packet[:2] == bytes((0x2F,row['mode'])) else None
                except (KeyError, TypeError, ValueError, IndexError):
                    safe = None
                if safe is None:
                    row.pop('raw', None)
                    row.pop('trailingBytes', None)
                elif len(safe) != len(packet):
                    parsed = decode(safe)
                    row.update(raw=safe.hex(), nativeBytesConsumed=parsed['nativeBytesConsumed'],
                               trailingBytes=parsed['trailingBytes'], retainedLength=len(safe),
                               discardedSuffixBytes=len(packet)-len(safe))
            yield row


@server.tool(annotations=READ_ONLY)
def map_transition_observations(session: str = "latest", map_id: int | None = None,
                                limit: Annotated[int, Field(ge=1, le=50)] = 12) -> dict[str, Any]:
    """본섭 맵 전환의 요청, 개인방 입퇴실, 도착 좌표, 소모품과 맵 체크를 대조합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    action = transition = handoff = block = destination_choice = ras_open = ras_use = None
    door_prepare = key_removal = door_buff_status = None
    arrival_action = None
    prelude = deque(maxlen=4)
    count = 0
    for row in recorded_rows(path):
        kind = row.get('kind')
        if kind == 'context_reset':
            if handoff and row.get('reason') == 'server_transfer' and 'connect_packet' not in handoff:
                handoff['connect_packet'] = row['packetId']
            else:
                handoff = None
            action = transition = arrival_action = block = destination_choice = ras_open = ras_use = None
            door_prepare = key_removal = door_buff_status = None
            prelude.clear()
        elif kind == 'client_action':
            action = row
            if row.get('opcode') == 0x16:
                destination_choice = row
            elif row.get('opcode') == 0x1C:
                ras_use, ras_open = row, None
            block = None
            door_prepare = key_removal = door_buff_status = None
            prelude.clear()
            if transition and row.get('opcode') == 0x35 and row.get('mapId') == transition['map']:
                transition['check_request'] = row['packetId']
                transition['checksum'] = row.get('checksum')
        elif kind == 'teleport_menu' and ras_use and ras_use.get('slot') is not None:
            if ras_use.get('map')==row.get('map') and 0<=observed_time(row)-observed_time(ras_use)<=1:
                ras_open = dict(packet=ras_use['packetId'],menu=row['packetId'],slot=ras_use['slot'])
            ras_use = None
        elif kind == 'map':
            transition = None
            opened_by, ras_open = ras_open, None
            transfer, handoff = handoff, None
            prepare, door_prepare = door_prepare, None
            buff_status, door_buff_status = door_buff_status, None
            used_key, key_removal = key_removal, None
            choice, destination_choice = destination_choice, None
            intervening_step = None
            ras_choice = matches_destination_choice(choice,row)
            if ras_choice:
                if action and action.get('opcode') == 0x33:
                    intervening_step = action['packetId']
                action = {**choice,'recentActionId':row.get('recentActionId')}
                prepare = None
            if transfer and (not transfer.get('connect_packet') or
                             transfer.get('target') is not None and row['map'] != transfer['target']):
                transfer = None
            if transfer is None and (action is None or row.get('recentActionId') != action.get('recentActionId')):
                continue
            origin, destination = (transfer['origin'] if transfer else action.get('map')), row['map']
            if map_id is not None and map_id not in (origin, destination):
                continue
            transition = dict(origin=origin, map=destination, map_packet=row['packetId'],
                              flags=row['flags'], arrival_sequence=['S15'])
            if raw := row.get('raw'):
                packet = bytes.fromhex(raw)
                if len(packet) >= 10:
                    transition['map_length'] = len(packet)
                    suffix = packet[10 + packet[9]:]
                    if suffix in (b'', b'\0'):
                        transition['map_padding'] = bool(suffix)
            if transfer:
                transition.update(transfer)
                if transfer.get('request_opcode')==0x16 and transfer.get('target')==destination:
                    transition['ras_destination'] = destination
            else:
                transition.update(request=action['packetId'], request_opcode=action['opcode'], prelude=list(prelude)[:-1])
                if ras_choice:
                    transition['ras_destination'] = destination
                    if opened_by:
                        transition['ras_open'] = opened_by
                if intervening_step is not None:
                    transition['intervening_step'] = intervening_step
                if prepare is not None:
                    transition['door_prepare'] = prepare
                    if buff_status and adjacent_packets(buff_status['packet'],row['packetId']):
                        field = 'door_option_status' if buff_status['flags']&0x20000 else 'door_buff_status'
                        transition[field] = buff_status
                if destination == 9801 and used_key:
                    transition['room_key_remove'] = used_key
                if destination == 9801 and action.get('action') == 'merc_dialog' and action.get('selector') in (0, 4):
                    transition['room_sign_action'] = {key: action[key] for key in ('selector', 'targetId') if key in action}
            arrival_action = row.get('recentActionId')
            results.append(transition)
            count += 1
        elif (kind == 'frame_header' and row['direction'] == 'C' and
              (row.get('map') == 9801 and row['opcode'] == 0x6A and row.get('plainLength') == 5 or
               row['opcode'] == 0x54 and row.get('plainLength') == 7)):
            if not action or action.get('packetId') != row['packetId']:
                # Older captures have only this frame, so defer destination identification to S15.
                action = {**row, 'action':'room_minigame' if row['opcode'] == 0x6A else 'merc_dialog'}
                block = door_prepare = key_removal = door_buff_status = None
                prelude.clear()
        elif kind == 'frame_header' and row['direction'] == 'S':
            opcode = f"S{row['opcode']:02X}"
            prelude.append(opcode)
            same_action = action and row.get('recentActionId') == action.get('recentActionId')
            if opcode == 'S67' and same_action and action['opcode'] == 0x33:
                door_prepare = {'packet':row['packetId'], 'plain_length':row.get('plainLength'), 'client_registered':False}
            if (opcode == 'S58' and transition and 'door_prepare' in transition
                    and same_action and arrival_action == action.get('recentActionId')):
                transition['door_finish'] = {'packet':row['packetId'], 'plain_length':row.get('plainLength'), 'client_registered':False}
            if opcode == 'S51' and same_action:
                block = dict(packetId=row['packetId'],recentActionId=row['recentActionId'],wire_length=row['plainLength'])
            if opcode == 'S03' and same_action and (action.get('action') == 'destination_choice' or
                    action.get('action') == 'room_minigame' and action.get('selector', 2) == 2 and
                    action.get('length', action.get('plainLength')) == 5):
                handoff = dict(origin=action['map'], request=action['packetId'], request_opcode=action['opcode'],
                               prelude=list(prelude)[:-1], transfer_packet=row['packetId'],transfer_length=row['plainLength'])
                if action['action'] == 'destination_choice':
                    handoff['target'] = action['mapId']
                    if ras_open:
                        handoff['ras_open'] = ras_open
                elif action.get('selector') == 2 and 'value' in action:
                    handoff['room_selection_id'] = action['value']
                if block and block.get('recentActionId') == action.get('recentActionId'):
                    handoff['client_block'] = {k:block[k] for k in ('selector','effectIndex','packetId','wire_length') if k in block}
            if transition and row.get('recentActionId') == arrival_action and len(transition['arrival_sequence']) < 20:
                transition['arrival_sequence'].append(opcode)
        elif kind == 'status' and door_prepare and door_prepare['plain_length'] == 7 and action and (
                row.get('recentActionId') == action.get('recentActionId') and row.get('map') == action.get('map') and
                (door_buff_prelude(door_prepare['packet'],row) or door_option_prelude(door_prepare['packet'],row))):
            door_buff_status = dict(packet=row['packetId'], wire_hex=row['raw'], flags=row['extraDword'],
                                    speed_percent=row['extraWord'],mask=row['mask'],state=row.get('state'),
                                    vitals=row.get('currentValues'))
        elif kind == 's58_probe' and transition and (finish := transition.get('door_finish')) and finish['packet']==row.get('packetId'):
            finish['opaque_bytes'] = row.get('opaqueBytes')
        elif kind == 'client_block':
            block = {**block,**row} if block and block.get('packetId') == row.get('packetId') else row
        elif (kind == 'inventory_remove' and (row.get('byte2'), row.get('word')) == (9, 0xA6) and
              action and action['opcode'] == 0x1C and action.get('slot') == row.get('slot') and
              row.get('recentActionId') == action.get('recentActionId')):
            key_removal = {'packet':row['packetId'], 'slot':row['slot'], 'wire_hex':row.get('raw')}
        elif (kind in ('inventory_add', 'inventory_remove') and action and action.get('action') == 'destination_choice'
              and row.get('recentActionId') == action.get('recentActionId')):
            if handoff and 'connect_packet' not in handoff and row.get('map') == handoff['origin']:
                travel = handoff
            else:
                travel = transition if transition and row.get('map') == transition['map'] else None
            if travel is not None and travel['request'] == action['packetId']:
                if row.get('wireSprite') == 0x8F24 or (row.get('byte2'), row.get('word')) == (9, 0x41):
                    travel['ras_stack'] = dict(slot=row['slot'],quantity=row.get('quantity',0),packetId=row['packetId'],
                                               length=row['length'],trailing_bytes=row.get('trailingBytes'))
        elif transition and row.get('map') == transition['map']:
            if kind == 'map_check' and row.get('phase') == 2 and 'check_request' in transition:
                transition['check_reply'] = row['packetId']
                transition['check_reply_hex'] = row.get('raw')
                transition = None
            elif row.get('recentActionId') != arrival_action:
                continue
            elif kind == 'self_position' and 'position' not in transition:
                transition['position'] = {k:row[k] for k in ('x', 'y', 'floor', 'packetId')}
            elif kind == 'sound' and row.get('bgm'):
                transition['bgm'] = {'id': row['id'], 'packet': row['packetId'], 'wire_hex': row.get('raw')}
    pairs = {(entry['origin'],entry['map']) for entry in results}
    profiles = read_json(LIVE).get('DoorArrivals', []) if LIVE.is_file() else []
    return {'session':path.parent.name, 'count':count, 'observations':list(results),
            'local_door_arrivals':[p for p in profiles if (p['Source'],p['Destination']) in pairs]}


@server.tool(annotations=READ_ONLY)
def map_check_observations(session: str = "latest", map_id: int | None = None,
                           limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """S14 지도 검사, C35 체크섬, S14 승인과 끝 바이트를 연결합니다."""
    path = session_events(session)
    samples, unanswered = deque(maxlen=limit), deque(maxlen=limit)
    forms = Counter()
    challenges, requests = deque(), deque()
    scope = scene = None
    request_count = reply_count = unmatched = 0

    def discard_requests():
        unanswered.extend(request for _,request in requests)
        requests.clear()

    for row in recorded_rows(path):
        kind = row['kind']
        op = row.get('opcode')
        if kind not in ('context_reset','map','map_check') and not (kind=='client_action' and op==0x35):
            continue
        packet = row.get('packetId') or ''
        current = row.get('epoch'), packet.partition(':')[0]
        area = row.get('map')
        if kind=='context_reset' or current!=scope:
            discard_requests()
            challenges.clear()
            scope, scene = current, None
        if kind=='map':
            origin = scene['map'] if scene else None
            if origin!=area:
                discard_requests()
                challenges.clear()
            scene = dict(map=area,origin=origin,map_packet=packet)
            continue
        if not packet or map_id not in (None,area):
            continue
        if kind=='map_check' and row.get('phase')==1:
            challenges.append(dict(scene or {'map':area},challenge=packet))
        elif kind=='client_action' and row.get('length')==5 and row.get('checksum') is not None:
            request_count += 1
            same_map = row.get('mapId')==area
            fields = challenges.popleft() if same_map and challenges and challenges[0]['map']==area else dict(scene or {'map':area})
            fields.update(request=packet,requested_map=row.get('mapId'),checksum=row['checksum'])
            if same_map:
                requests.append((observed_time(row),fields))
            else:
                unanswered.append(fields)
        elif kind=='map_check' and row.get('phase')==2:
            reply_count += 1
            forms[row.get('raw') or f"length:{row.get('length')}"] += 1
            if requests and requests[0][1]['requested_map']==area:
                requested_at, request = requests.popleft()
                samples.append(dict(request,reply=packet,length=row.get('length'),wire_hex=row.get('raw'),
                                    elapsed_ms=round((observed_time(row)-requested_at)*1000)))
            else:
                unmatched += 1
    discard_requests()
    return dict(session=path.parent.name,requests=request_count,replies=reply_count,unmatched_replies=unmatched,
                formats=dict(forms),observations=list(samples),unanswered_requests=list(unanswered))


@server.tool(annotations=READ_ONLY)
def world_entry_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=50)] = 12) -> dict[str, Any]:
    """접속 시 S17 주문, S2C 어빌, S39 나이, S05 방향, 첫 S04 좌표와 초기 S08 상태을 대조합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    spell, abilities, panel, entry = None, None, None, None
    status_entry = None
    entry_time = 0
    count = 0
    for row in recorded_rows(path):
        kind = row['kind']
        if kind=='context_reset':
            spell, abilities, panel, entry, status_entry = None, None, None, None, None
            continue
        raw = row.get('raw')
        if not raw:
            continue
        if row.get('opcode')==0x17 and raw in ('170300', '17030000'):
            spell = dict(packet=row.get('packetId'),epoch=row.get('epoch'),wire_hex=raw)
        elif kind=='ability' and row.get('mode')==3:
            abilities = row
        elif kind=='self_look' and row.get('mode')==0 and 'byte1' in row:
            panel = row
        elif kind=='self_identity':
            fields = decode(bytes.fromhex(raw))
            entry = dict(packet=row.get('packetId'),epoch=row.get('epoch'),length=len(raw)//2,
                direction=fields['direction'],flags=fields['flags'],trailing_bytes=fields['trailingBytes'])
            status_entry, entry_time = entry, observed_time(row)
            if spell and spell['epoch']==entry['epoch']:
                entry['empty_spell_list']=spell
            if abilities and abilities.get('epoch')==entry['epoch'] and 0<=entry_time-observed_time(abilities)<=1:
                entry['ability_list'] = dict(packet=abilities.get('packetId'),length=abilities.get('length'),entries=[
                    dict(slot=skill.get('slot'),name=skill.get('name'),mastery=skill.get('headerWords',[None,None])[1],
                         progress=skill.get('additionalWords',[None,None])[0],
                         reference=skill.get('additionalWords',[None,None])[1])
                    for skill in abilities.get('entries',[])])
            if panel and panel.get('epoch')==entry['epoch'] and 0<=entry_time-observed_time(panel)<=1:
                entry['character_panel'] = dict(packet=panel.get('packetId'),age=panel['byte1'],
                    level=panel.get('characterPanelLevel'),length=panel['length'],trailing_bytes=panel.get('trailingBytes'))
            spell, abilities, panel = None, None, None
        elif kind=='self_position' and entry and entry['epoch']==row.get('epoch'):
            fields = decode(bytes.fromhex(raw))
            entry.update(map=row.get('map'),position=dict(packet=row.get('packetId'),
                x=fields['x'],y=fields['y'],floor=fields['floor'],camera=[fields['cameraX'],fields['cameraY']],
                length=len(raw)//2,trailing_bytes=fields['trailingBytes']))
            results.append(entry)
            count += 1
            entry = None
        elif kind=='status' and status_entry and row['mask'] in (0x70,0x78):
            if row.get('epoch')==status_entry['epoch'] and 0<=observed_time(row)-entry_time<=1:
                maximum, current = row['maximumValues'], row['currentValues']
                status_entry['initial_vitals'] = dict(packet=row.get('packetId'),mask=row['mask'],
                    maximum_hp=maximum[0],maximum_mp=maximum[1],hp=current[0],mp=current[1])
                if len(words := row.get('baseWords', []))==6:
                    status_entry['initial_vitals']['base_words'] = words
            status_entry = None
    return dict(session=path.parent.name,count=count,observations=list(results))


@server.tool(annotations=READ_ONLY)
def movement_ack_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """자기 이동 S0B의 좌표, 보정, 후행 바이트와 직전 개체 갱신을 조회합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    layouts = Counter()
    updates_before_ack = Counter()
    updates = deque(maxlen=50)
    request = None
    for row in recorded_rows(path):
        kind = row['kind']
        if kind in ('context_reset', 'map'):
            request = None
        elif kind == 'frame_header' and row.get('direction') == 'C':
            if request and request.get('packetId') != row.get('packetId'):
                request = None
        elif kind == 'client_action':
            request = row if row.get('action') == 'move_request' else None
            updates.clear()
        elif request and (kind in ('objects', 'object_remove') or kind == 'human' and not row.get('isSelf')):
            if all(row.get(key) == request.get(key) for key in ('epoch', 'map', 'recentActionId')):
                updates.append(row)
        elif kind == 'self_move' and (raw := row.get('raw')):
            tail = raw[24:]
            correction = row['correction']
            tag = row['sequence']
            layouts[correction, len(raw)//2, tag, tail] += 1
            reply = dict(packet=row.get('packetId'), map=row.get('map'), direction=row['direction'],
                         origin=[row['wireX'], row['wireY'], row['floor']], correction=correction, wire_hex=raw)
            if request and all(row.get(key) == request.get(key) for key in ('epoch', 'map', 'recentActionId')):
                elapsed = observed_time(row) - observed_time(request)
                if 0 <= elapsed <= .5 and (correction or row['direction'] == request['direction']):
                    reply['request'] = dict(packet=request.get('packetId'), direction=request['direction'],
                                            mode=request.get('sequence'))
                    adjacent = [dict(packet=event.get('packetId'), kind=event['kind']) for event in updates
                                if 0 <= observed_time(row) - observed_time(event) <= .004]
                    if adjacent:
                        reply['adjacent_object_updates_before_ack'] = adjacent
                        updates_before_ack.update(event['kind'] for event in adjacent)
            results.append(reply)
            request = None
    return dict(session=path.parent.name, count=sum(layouts.values()), adjacent_object_updates_before_ack=dict(updates_before_ack), layouts=[
        dict(correction=correction, length=length, movement_tag=tag, trailing_bytes=tail, count=count)
        for (correction, length, tag, tail), count in layouts.most_common()], observations=list(results))


@server.tool(annotations=READ_ONLY)
def turn_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """C11 요청과 자기 S11 응답을 연결하고 접속 차원과 맵별 패딩을 집계합니다."""
    path = session_events(session)
    samples, layouts = deque(maxlen=limit), Counter()
    self_id = dimension = request = None
    for row in recorded_rows(path):
        kind = row['kind']
        if kind=='context_reset':
            self_id = dimension = request = None
        elif kind in ('self_identity','context_snapshot'):
            self_id = row.get('oid' if kind=='self_identity' else 'selfId')
        elif kind=='status' and dimension is None and row.get('baseBytes'):
            dimension = row['baseBytes'][0] >> 4
        elif kind=='map':
            request = None
        elif kind=='client_action':
            request = row if row.get('action')=='turn_request' else None
        elif kind=='direction' and request is not None and self_id is not None and row.get('oid')==self_id:
            if any(row.get(key)!=request.get(key) for key in ('epoch','map','direction','recentActionId')):
                continue
            if not 0<=observed_time(row)-observed_time(request)<=.5:
                continue
            raw = row.get('raw','')
            length = row.get('length')
            if length not in (6,7) or len(raw)!=length*2 or raw[12:] not in ('','00'):
                continue
            map_id = row.get('map')
            map_dimension = map_id//10000 if type(map_id) is int and map_id>=0 else None
            layouts[dimension,map_dimension,length] += 1
            samples.append(dict(request=request.get('packetId'),reply=row.get('packetId'),map=map_id,
                                connection_dimension=dimension,direction=row['direction'],length=length,padding=raw[12:]))
            request = None
    return dict(session=path.parent.name, matched=sum(layouts.values()),
                layouts=[dict(connection_dimension=connection,map_dimension=area,length=length,count=count)
                         for (connection,area,length),count in layouts.most_common()],observations=list(samples))


@server.tool(annotations=READ_ONLY)
def status_option_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """C1B 요청과 직후 S08 확장 상태의 플래그 및 이동 비율을 대조합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    request = None
    for row in recorded_rows(path):
        kind = row['kind']
        if kind == 'context_reset':
            request = None
        elif kind == 'client_action' or kind == 'frame_header' and row.get('direction')=='C':
            if row.get('opcode')==0x1B and row.get('length',row.get('plainLength'))==3:
                if request is None or request.get('packetId')!=row.get('packetId') or kind=='client_action':
                    request = row
            elif request and row.get('packetId')!=request.get('packetId'):
                request = None
        elif request and kind=='status' and row.get('mask')==8 and row.get('epoch')==request.get('epoch'):
            latency = (datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(request['time'])).total_seconds()
            if 0<=latency<=.25 and 'extraDword' in row and 'extraWord' in row:
                flags = row['extraDword']
                results.append(dict(request=request.get('packetId'),reply=row.get('packetId'),mode=request.get('mode'),
                                    flags=flags,option_bit=bool(flags&1),speed_percent=row['extraWord'],wire_hex=row.get('raw')))
            request = None
    return dict(session=path.parent.name,observations=list(results))


@server.tool(annotations=READ_ONLY)
def swimming_observations(session: str = "latest", map_id: int | None = None,
                          limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """S0B·S0C 이동 좌표를 클라이언트 맵과 대조해 입수, 수영, 상륙을 확인합니다."""
    path = session_events(session)
    client = client_directory()
    world = Snapshot(LIVE, client)
    counts, samples = Counter(), deque(maxlen=limit)
    for row in recorded_rows(path):
        area, kind = row.get('map'), row['kind']
        if area is None or (map_id is not None and area != map_id) or kind not in ('self_move','object_move'):
            continue
        direction = row['direction']
        if not 0 <= direction < len(DELTAS):
            continue
        dx, dy = DELTAS[direction]
        x, y, floor = row['x'], row['y'], row['floor']
        if kind == 'self_move':
            x, y = row.get('wireX', x-dx), row.get('wireY', y-dy)
        source = world.tile(area, x, y, floor)
        target = world.tile(area, x+dx, y+dy, floor)
        if source is None or target is None:
            continue
        next_floor = floor + (1 if target&0x10 else -1 if target&0x20 else 0)
        target = world.tile(area, x+dx, y+dy, next_floor)
        if target is None or 0x80 not in (source, target):
            continue
        stage = 'swim' if source==target else 'enter' if target==0x80 else 'leave'
        counts[stage] += 1
        samples.append(dict(map=area, packet=row.get('packetId'), kind=kind, stage=stage, source=[x,y,floor],
                            target=[x+dx,y+dy,next_floor], flags=[source,target], animation=row.get('animation')))
    return {'session':path.parent.name,'counts':dict(counts),'samples':list(samples)}


@server.tool(annotations=READ_ONLY)
def object_movement_observations(session: str = "latest", map_id: int | None = None,
                                 limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """S0C 형식, 다른 층 개체, 경계의 S0C·S0E와 C0C 재요청을 대조합니다."""
    path = session_events(session)
    client = client_directory()
    world = Snapshot(LIVE, client)
    layouts, floor_pairs = Counter(), Counter()
    samples, departures, refreshes = (deque(maxlen=limit) for _ in range(3))
    actors, edge_steps, turns, combat_modes, speech, refresh_effects = {}, {}, {}, {}, {}, {}
    observer = None
    legacy_refresh = None
    pending_refreshes = deque(maxlen=64)
    departure_counts = Counter()

    def match_refresh(row, actor, actor_kind, allow_legacy=True):
        nonlocal legacy_refresh
        now = observed_time(row)
        while pending_refreshes and now - observed_time(pending_refreshes[0][0]) > .5:
            pending_refreshes.popleft()
        exact = next((entry for entry in pending_refreshes
                      if entry[0]['targetId'] == actor['oid'] and now >= observed_time(entry[0])), None)
        if exact:
            request, request_observer, request_mode = exact
            pending_refreshes.remove(exact)
        elif allow_legacy and legacy_refresh and 0 <= now - observed_time(legacy_refresh) <= .5:
            request, request_observer, request_mode = legacy_refresh, None, None
            legacy_refresh = None
        else:
            return
        turn = turns.get(actor['oid'])
        turn_matches = turn and 0 <= observed_time(request) - observed_time(turn) <= .25
        position = [actor['x'], actor['y'], actor['floor']]
        offset = [a - b for a,b in zip(position,observer[1:4])] if observer else None
        request_offset = [a - b for a,b in zip(position,request_observer[1:4])] if request_observer else None
        result = dict(map=row.get('map'), request=request.get('packetId'), appearance=row.get('packetId'),
                      actor_kind=actor_kind, match='object_id' if exact else 'turn_object_id' if turn_matches else 'adjacent_header',
                      objectId=actor['oid'], position=position, offset=offset, request_offset=request_offset,
                      appearanceLength=row.get('length'),
                      turn=turn.get('packetId') if turn_matches else None)
        if 'sprite' in actor:
            result['sprite'] = actor['sprite']
        if actor_kind == 'player' and request_mode and 0 <= observed_time(request) - observed_time(request_mode) <= .25:
            result.update(recent_combat_mode=request_mode['mode'], combat_mode_packet=request_mode.get('packetId'))
        if actor_kind == 'hired_merchant':
            if (spoken := speech.get(actor['oid'])) and 0 <= observed_time(request) - observed_time(spoken) <= .25:
                result['speech_packet'] = spoken.get('packetId')
            effects = [effect['packetId'] for effect in refresh_effects.get(actor['oid'], [])
                       if 0 <= observed_time(request) - observed_time(effect) <= .25]
            if effects:
                result['effect_packets'] = effects
            suffix = row.get('trailingBytes')
            if suffix is None and row.get('raw'):
                try:
                    suffix = decode(bytes.fromhex(row['raw'])).get('trailingBytes')
                except (ValueError, IndexError):
                    pass
            if suffix in ('','00'):
                result['appearance_suffix'] = suffix
        refreshes.append(result)

    for row in recorded_rows(path):
        kind, area = row['kind'], row.get('map')
        if kind in ('context_reset', 'map'):
            actors.clear()
            edge_steps.clear()
            turns.clear()
            combat_modes.clear()
            speech.clear()
            refresh_effects.clear()
            observer = None
            legacy_refresh = None
            pending_refreshes.clear()
        if area is None or (map_id is not None and area != map_id):
            continue
        if kind == 'object_refresh_request':
            pending_refreshes.append((row, observer, combat_modes.get(row['targetId'])))
            if legacy_refresh and legacy_refresh.get('packetId') == row.get('packetId'):
                legacy_refresh = None
        elif kind == 'frame_header' and row.get('direction') == 'C':
            legacy_refresh = None
            if row.get('opcode') == 0x0C and row.get('plainLength') == 5 and (
                not pending_refreshes or pending_refreshes[-1][0].get('packetId') != row.get('packetId')
            ):
                legacy_refresh = row
        elif kind == 'frame_header' and (row.get('direction'),row.get('opcode'),row.get('messageMode')) == ('S',0x0D,4):
            if speaker := row.get('speech',{}).get('speaker'):
                speech[speaker] = row
        elif kind == 'context_snapshot':
            position = row.get('position')
            if position:
                observer = (area, position['x'], position['y'], position['floor'], row.get('packetId'))
        elif kind in ('self_position', 'self_move'):
            floor = row['floor']
            if kind == 'self_move' and not row['correction']:
                tile = world.tile(area, row['x'], row['y'], floor)
                if tile is None:
                    observer = None
                    continue
                floor += 1 if tile & 0x10 else -1 if tile & 0x20 else 0
            observer = (area, row['x'], row['y'], floor, row.get('packetId'))
        elif kind == 'human' and not row.get('isSelf'):
            actors[row['oid']] = ('player', row.get('packetId'))
            match_refresh(row, row, 'player')
        elif kind == 'objects':
            for actor in row['objects']:
                if actor.get('category') == 0:
                    actors[actor['oid']] = ('monster', row.get('packetId'))
                    match_refresh(row, actor, 'monster', allow_legacy=len(row['objects']) == 1)
                elif actor.get('category') == 2:
                    match_refresh(row, actor, 'hired_merchant', allow_legacy=False)
                elif actor.get('category') == 6 and 0x40C3 <= actor['sprite'] <= 0x40C7:
                    match_refresh(row, actor, 'room_sign', allow_legacy=len(row['objects']) == 1)
        elif kind == 'direction':
            turns[row['oid']] = row
        elif kind == 'effect' and (row.get('effect'),row.get('parameter')) == (2,0):
            refresh_effects.setdefault(row['oid'],deque(maxlen=2)).append(row)
        elif kind == 'combat_mode':
            combat_modes[row['oid']] = row
        elif kind in ('object_remove', 'death'):
            turns.pop(row['oid'], None)
            actors.pop(row['oid'], None)
            edge = edge_steps.pop(row['oid'], None)
            if kind == 'object_remove' and edge and observer == edge[0] and 0 <= observed_time(row) - edge[1] <= .003:
                departure_counts[edge[2]['actor_kind']] += 1
                departures.append(dict(**edge[2], removal=row.get('packetId')))
        elif kind == 'object_move' and (raw := row.get('raw')):
            length, tail = len(raw)//2, raw[24:]
            layouts[length, tail] += 1
            if observer is None or observer[0] != area or row['oid'] not in actors:
                continue
            _, x, y, floor, position = observer
            actor_kind, appearance = actors[row['oid']]
            edge_steps.pop(row['oid'], None)
            dx, dy = row['x'] - x, row['y'] - y
            if abs(dx) <= 16 and abs(dy) <= 9 and 0 <= row['direction'] < len(DELTAS):
                vx, vy = DELTAS[row['direction']]
                if abs(dx+vx) == 17 or abs(dy+vy) == 10:
                    edge_steps[row['oid']] = (observer, observed_time(row), dict(map=area, observer_packet=position,
                        actor_kind=actor_kind, appearance=appearance, movement=row.get('packetId'),
                        origin_offset=[dx,dy], destination_offset=[dx+vx,dy+vy]))
            if actor_kind!='player' or floor == row['floor'] or abs(dx) > 16 or abs(dy) > 9:
                continue
            floor_pairs[area, floor, row['floor']] += 1
            samples.append(dict(map=area, packet=row.get('packetId'), appearance=appearance,
                                observer_packet=position, observer=[x,y,floor],
                                origin=[row['x'],row['y'],row['floor']], wire_hex=raw))
    return dict(session=path.parent.name, count=sum(layouts.values()), layouts=[
        dict(length=length, trailing_bytes=tail, count=count) for (length, tail), count in layouts.most_common()],
        cross_floor_counts=[dict(map=area, observer_floor=observer_floor, mover_floor=mover_floor, count=count)
            for (area, observer_floor, mover_floor), count in floor_pairs.most_common()], cross_floor_samples=list(samples),
        edge_departure_count=sum(departure_counts.values()), edge_departures_by_kind=dict(departure_counts),
        edge_departures=list(departures), refresh_appearances=list(refreshes))


@server.tool(annotations=READ_ONLY)
def list_captures(limit: Annotated[int, Field(ge=1, le=30)] = 8) -> dict[str, Any]:
    """최근 일랜시아 기록 세션의 ID, 파일 크기와 마지막 기록 시간을 조회합니다."""
    sessions = []
    for session in recent_sessions()[:limit]:
        stat = capture_file(session, "events.jsonl").stat()
        sessions.append(dict(session=session, bytes=stat.st_size, updated_at=utc_timestamp(stat.st_mtime)))
    return {"sessions": sessions}


@server.tool(annotations=READ_ONLY)
def capture_summary(session: str = "latest") -> dict[str, Any]:
    """한 세션의 기록 범위와 오류를 조회하고 이전 미해석 패킷을 현재 디코더로 다시 확인합니다."""
    path = session_events(session)
    session = path.parent.name
    summary_file = capture_file(session, "summary.json")
    summary = read_json(summary_file) if summary_file.is_file() else None
    fields = ("updatedAt", "events", "actionsObserved", "currentMap", "mapsObserved", "coverage",
              "parseErrors", "frameMismatches", "hookErrors", "droppedEvents")
    summary = {key: summary[key] for key in fields if key in summary} if summary is not None else None
    pending, redecoded, errors, decoded_counts = Counter(), Counter(), Counter(), Counter()
    ignored = Counter()
    coverage = set()
    for row in recorded_rows(path):
        if row.get("opcode") in UNREGISTERED_SERVER_OPCODES:
            if row["kind"] == "frame_header" and row["direction"] == "S":
                ignored[f"S:{row['opcode']:02x}"] += 1
                continue
            if row.get("layout") == "raw_pending_decode":
                continue
        if not row.get("raw"):
            continue
        packet_kind = EXTENDED_PACKET_KINDS.get(row.get("opcode"))
        if row.get("layout") != "raw_pending_decode":
            if packet_kind and row.get("kind") == packet_kind:
                decoded_counts[f"S:{row['opcode']:02x}"] += 1
                coverage.add(packet_kind)
            continue
        opcode = f"S:{row['opcode']:02x}"
        try:
            raw = sanitized_packet(bytes.fromhex(row["raw"]))
            if raw is None or raw[0] != row["opcode"]:
                raise ValueError("invalid recorded opcode")
            event = decode(raw)
        except (ValueError, IndexError):
            errors[opcode] += 1
            continue
        if event.get("layout") == "raw_pending_decode":
            pending[opcode] += 1
        else:
            redecoded[opcode] += 1
            if packet_kind:
                decoded_counts[opcode] += 1
            coverage.add(event["kind"])
    if summary is not None and coverage:
        summary["coverage"] = sorted(set(summary.get("coverage", [])) | coverage)
    return {
        "session": session,
        "summary": summary,
        "needs_decode": dict(pending),
        "ignored_by_v611_client": dict(ignored),
        "tracked_decoded_counts": dict(decoded_counts),
        "redecoded": dict(redecoded),
        "redecode_errors": dict(errors),
    }


@server.tool(annotations=READ_ONLY)
def progression_observations(session: str = "latest") -> dict[str, Any]:
    """S08 레벨·명성·무게 한도, S39 장비·경험치, S2C 카운트, S55 잡포인트를 조회합니다."""
    paths = [session_events(name) for name in recent_sessions()] if session == "all" else [session_events(session)]
    states = {}
    character_status = {}
    character_panel = None
    panel_samples = Counter()
    panel_counters = Counter()
    panel_experience, panel_experience_examples = Counter(), {}
    panel_fate, panel_fate_examples = Counter(), {}
    panel_battle, panel_battle_examples = Counter(), {}
    fame_changes = Counter()
    weight_formula, sixth_base_word = Counter(), Counter()
    level_changes = []
    level_ranges = {}
    requirements, grades = Counter(), Counter()
    grade_examples = {}
    slot_counts, slot_examples = Counter(), {}
    modes, progress_changes, proficiency_changes = Counter(), Counter(), Counter()
    latest_meter, pending_growth = {}, {}
    growth_sequences, growth_examples = Counter(), deque(maxlen=20)
    job_lists = deque(maxlen=20)
    errors = 0
    rows = ((path.parent.name, row) for path in reversed(paths) for row in recorded_rows(path))
    for session_name, row in rows:
        # Initialization may send S2C before S05. The recorder already starts a
        # new epoch on a transfer, so both packets belong to the same epoch.
        epoch = session_name, row.get("epoch")
        opcode = row.get("opcode")
        if opcode not in (0x08,0x2C,0x39,0x55) or not row.get("raw"):
            continue
        try:
            raw = bytes.fromhex(row["raw"])
            if not raw or raw[0] != opcode:
                raise ValueError("invalid progression packet")
            event = decode(raw)
        except (IndexError, ValueError):
            errors += 1
            continue
        if opcode == 0x55:
            job_lists.append(dict(session=session_name, map=row.get("map"), packet_id=row.get("packetId"),
                                  jobs=[dict(name=entry["name"], points=entry["code"]) for entry in event["entries"]]))
            continue
        if opcode == 0x39:
            level, percent = event['characterPanelLevel'], event['characterPanelPercent']
            panel_samples[level, percent] += 1
            panel_counters[level,percent,event['dword']] += 1
            character_panel = dict(level=level, percent=percent, packet_id=row.get("packetId"),
                                   panel_dword=event['dword'], preceding_status=character_status.get(epoch))
            if 'characterPanelExperience' in event:
                experience = event['characterPanelExperience']
                character_panel['experience'] = experience
                battle_points, battle_target = event['dwords']
                character_panel['battle_points'] = dict(current=battle_points, target=battle_target)
                battle_sample = battle_points, battle_target
                panel_battle[battle_sample] += 1
                panel_battle_examples.setdefault(battle_sample,dict(session=session_name, packet_id=row.get('packetId')))
                sample = level, percent, experience
                panel_experience[sample] += 1
                if sample not in panel_experience_examples or (not panel_experience_examples[sample]['packet_id'] and row.get('packetId')):
                    panel_experience_examples[sample] = dict(session=session_name, packet_id=row.get('packetId'))
                fate = event['fatePoints']
                order = event['finalBytes'][0]
                character_panel['fate_points'] = dict(fate, order=order)
                fate_sample = level, order, fate['total'], tuple(fate['allocated']), fate['available']
                panel_fate[fate_sample] += 1
                if fate_sample not in panel_fate_examples or (not panel_fate_examples[fate_sample]['packet_id'] and row.get('packetId')):
                    panel_fate_examples[fate_sample] = dict(session=session_name, packet_id=row.get('packetId'))
                character_panel['equipment_durability'] = [dict(slot=slot, percent=durability)
                                                          for slot, durability in event['bytePairs']]
            continue
        if opcode == 0x08:
            if "characterLevel" in event:
                current = dict(level=event["characterLevel"], fame=event["fame"],
                               packet_id=row.get("packetId"), time=row.get("time"))
                if len(words := event.get('baseWords', [])) == 6 and (remaining := event.get('remainingBaseWords')):
                    current.update(strength=words[0],carry_capacity=remaining[0])
                    if event.get('maximumValues', [0,0,0,0])[3] == 1000:
                        weight_formula[remaining[0]-min(65535,15*words[0])] += 1
                        sixth_base_word[words[5]] += 1
                level, fame = current["level"], current["fame"]
                bounds = level_ranges.setdefault(level, dict(minimum=fame, maximum=fame, observations=0))
                bounds["minimum"] = min(bounds["minimum"], fame)
                bounds["maximum"] = max(bounds["maximum"], fame)
                bounds["observations"] += 1
                previous = character_status.get(epoch)
                if previous:
                    delta = current["fame"] - previous["fame"]
                    if delta:
                        fame_changes[str(delta)] += 1
                    if current["level"] != previous["level"]:
                        level_changes.append(dict(session=session_name, before=previous, after=current, packet_id=row.get("packetId")))
                character_status[epoch] = current
            continue
        mode = event["mode"]
        modes[str(mode)] += 1
        skills = states.setdefault(epoch, {})
        if mode == 3:
            skills.clear()
        if mode in (1, 3):
            for entry in event["entries"]:
                slot = entry["slot"]
                assignment = entry["name"], slot
                slot_counts[assignment] += 1
                if assignment not in slot_examples:
                    slot_examples[assignment] = dict(session=session_name, packet_id=row.get("packetId"), mode=mode)
                skill = skills.get(slot, {})
                proficiency = entry["headerWords"][1]
                if skill.get("name") == entry["name"]:
                    delta = proficiency - skill["proficiency_hundredths"]
                    if delta:
                        proficiency_changes[str(delta)] += 1
                    if mode == 1 and delta > 0:
                        key = epoch, row.get('map'), slot
                        meter = latest_meter.get(key)
                        if meter and meter['action'] == row.get('recentActionId'):
                            pending_growth[key] = (meter, row, delta)
                else:
                    skill = {}
                grade = entry['flags'] if entry['category']==0 else None
                skill.update(name=entry["name"], proficiency_hundredths=proficiency)
                if grade is not None:
                    skill['grade'] = grade
                    sample = entry['name'], grade
                    grades[sample] += 1
                    grade_examples.setdefault(sample,dict(session=session_name,packet_id=row.get('packetId'),
                                                          proficiency_hundredths=proficiency))
                else:
                    skill.pop('grade',None)
                if mode == 3:
                    skill["progress"], skill["reference"] = entry["additionalWords"]
                    requirements[skill["name"], proficiency, skill["reference"]] += 1
                skills[slot] = skill
        if mode == 2:
            slot = event["slot"]
            key = epoch, row.get('map'), slot
            skill = skills.setdefault(slot, {})
            meter = dict(action=row.get('recentActionId'), packet=row.get('packetId'), time=row['time'],
                         counter=event['value'], required=event['referenceWord'])
            latest_meter[key] = meter
            pending = pending_growth.pop(key, None)
            if pending and skill.get('name'):
                before, mastery, increase = pending
                elapsed = datetime.datetime.fromisoformat(meter['time']) - datetime.datetime.fromisoformat(before['time'])
                if (0 <= elapsed.total_seconds() <= .25 and before['action'] == meter['action']
                        and (before['counter'],before['required']) == (meter['counter'],meter['required'])):
                    growth_sequences[skill['name']] += 1
                    growth_examples.append(dict(session=session_name, ability=skill['name'], slot=slot, increase=increase,
                        counter=meter['counter'], required=meter['required'], before=before['packet'],
                        mastery=mastery.get('packetId'), after=meter['packet']))
            if skill.get("reference") == event["referenceWord"]:
                delta = event["value"] - skill["progress"]
                if delta:
                    progress_changes[str(delta)] += 1
            skill.update(progress=event["value"], reference=event["referenceWord"])
            if skill.get("name"):
                requirements[skill["name"], skill["proficiency_hundredths"], skill["reference"]] += 1
    last = next(reversed(states.values()), {})
    return {"session": "all" if session == "all" else paths[0].parent.name, "sessions_scanned": len(paths),
            "carry_capacity_delta_from_strength_times_15": dict(weight_formula),
            "sixth_base_word_counts": dict(sixth_base_word),
            "modes": dict(modes), "progress_deltas": dict(progress_changes),
            "proficiency_deltas_hundredths": dict(proficiency_changes), "decode_errors": errors,
            "meter_mastery_meter_counts": dict(growth_sequences), "meter_mastery_meter_examples": list(growth_examples),
            "ability_slot_assignments": [dict(name=name, slot=slot, observations=count, example=slot_examples[name, slot])
                                         for (name, slot), count in sorted(slot_counts.items())],
            "ability_grades": [dict(name=name,grade=grade,observations=count,example=grade_examples[name,grade])
                               for (name,grade),count in sorted(grades.items())],
            "job_lists": list(job_lists),
            "character_status": next(reversed(character_status.values()), None),
            "character_panel": character_panel,
            "character_panel_samples": [dict(level=level, percent=percent, observations=n)
                                        for (level, percent), n in sorted(panel_samples.items())],
            "character_panel_dwords": [dict(level=level, percent=percent, value=value, observations=n)
                                       for (level,percent,value), n in sorted(panel_counters.items())],
            "character_panel_experience_samples": [
                dict(level=level, percent=percent, experience=experience, observations=n,
                     example=panel_experience_examples[level, percent, experience])
                for (level, percent, experience), n in sorted(panel_experience.items())],
            "character_panel_fate_samples": [
                dict(level=level, order=order, total=total, allocated=list(allocated), available=available,
                     observations=n, example=panel_fate_examples[level, order, total, allocated, available])
                for (level, order, total, allocated, available), n in sorted(panel_fate.items())],
            "character_panel_battle_samples": [
                dict(current=current, target=target, observations=n, example=panel_battle_examples[current, target])
                for (current, target), n in sorted(panel_battle.items())],
            "character_fame_deltas": dict(fame_changes), "character_level_changes": level_changes[-20:],
            "character_fame_observed_by_level": [dict(level=level, **values) for level, values in sorted(level_ranges.items())],
            "ability_count_requirements": [dict(name=name, proficiency_hundredths=proficiency, required_count=reference, observations=n)
                                           for (name, proficiency, reference), n in sorted(requirements.items())],
            "last_observed": [dict(slot=slot, **skill,
                                   remaining_count=max(0, skill["reference"] - skill["progress"])
                                   if "reference" in skill else None) for slot, skill in sorted(last.items())]}


@server.tool(annotations=READ_ONLY)
def mastery_experience_observations(session: str = "latest",
                                    limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """같은 연결의 연속 S39 EXP 표본 사이에서 S2C 숙련도와 S5F 사망을 대조합니다."""
    path = session_events(session)
    windows = deque(maxlen=limit)
    skills = {}
    epoch = identity = panel = None
    milestones = []
    deaths = unresolved = 0
    map_changed = False
    for row in recorded_rows(path):
        kind, op = row['kind'], row.get('opcode')
        if row.get('epoch') != epoch or kind == 'context_reset':
            epoch = row.get('epoch')
            identity = panel = None
            skills.clear()
        if kind in ('self_identity', 'context_snapshot'):
            player = row.get('oid' if kind == 'self_identity' else 'selfId')
            if identity is not None and player != identity:
                panel = None
                skills.clear()
            identity = player
        if panel and kind == 'frame_header' and row.get('direction') == 'S' and op == 0x5F:
            deaths += 1
        if panel and kind == 'map' and row.get('map') != panel['map']:
            map_changed = True
        if kind not in ('ability', 'self_look') or not row.get('raw'):
            continue
        try:
            event = decode(bytes.fromhex(row['raw']))
            if event.get('kind') != kind:
                raise ValueError('unexpected growth packet')
        except (ValueError, IndexError):
            if panel:
                unresolved += 1
            continue
        if kind == 'ability' and event.get('mode') in (1, 3):
            refresh = event['mode'] == 3
            for entry in event.get('entries', []):
                slot, name = entry['slot'], entry['name']
                current = entry['headerWords'][1]
                prior = skills.get(slot)
                skills[slot] = name, current
                if not panel:
                    continue
                if prior is None or prior[0] != name or refresh and prior[1] != current or current < prior[1]:
                    unresolved += 1
                    continue
                before = prior[1]
                first, last = (before // 10 + 1) * 10, current // 10 * 10
                if first <= last:
                    milestones.append(dict(packet=row.get('packetId'), ability=name, slot=slot,
                                           before=before, after=current, first=first, last=last))
            continue
        xp = event.get('characterPanelExperience')
        if xp is None:
            continue
        sample = dict(packet=row.get('packetId'), map=row.get('map'), experience=xp,
                      level=event['characterPanelLevel'], percent=event['characterPanelPercent'])
        if panel and identity is not None:
            delta = xp - panel['experience']
            if delta or milestones or deaths:
                isolated = (delta > 0 and deaths == 0 and unresolved == 0 and len(milestones) == 1
                            and milestones[0]['first'] == milestones[0]['last']
                            and not map_changed and sample['map'] == panel['map'])
                windows.append(dict(before=panel, after=sample, experience_delta=delta,
                                    mastery_milestones=milestones, death_packets=deaths,
                                    unresolved_mastery_updates=unresolved, map_changed=map_changed,
                                    single_milestone_candidate=isolated))
        panel, milestones = sample, []
        deaths = unresolved = 0
        map_changed = False
    return dict(session=path.parent.name, observations=list(windows))


@server.tool(annotations=READ_ONLY)
def npc_dialog_observations(session: str = "latest", map_id: int | None = None,
                            limit: Annotated[int, Field(ge=1, le=100)] = 25) -> dict[str, Any]:
    """NPC 대화와 길드기 앞 이동에 따른 S30의 본문·선택지를 조회합니다."""
    path = session_events(session)
    counts, pages = Counter(), []
    visible, navigation = {}, deque(maxlen=limit)
    header_only, pending_header = deque(maxlen=limit), None
    recoveries, cross_floor = deque(maxlen=limit), deque(maxlen=limit)
    guild_flag_steps = deque(maxlen=limit)
    guild_flag_refreshes = deque(maxlen=limit)
    flag_move = flag_dialog = None
    flag_refresh = None
    npc_positions, requests = {}, {}
    position = None
    last_vitals = recovery = recovery_action = pending_recovery_next = None
    for row in recorded_rows(path):
        area, kind = row.get('map'), row['kind']
        action = row.get('epoch'), area, row.get('recentActionId')
        if kind in ('context_reset', 'map'):
            npc_positions.clear()
            requests.clear()
            position = None
        elif kind in ('self_position', 'self_move', 'context_snapshot'):
            tile = row.get('position', {}) if kind == 'context_snapshot' else row
            if tile and {'x', 'y', 'floor'} <= tile.keys():
                position = tile['x'], tile['y'], tile['floor']
        elif kind == 'objects':
            for obj in row['objects']:
                if obj['category'] in (1, 2) and {'x', 'y', 'floor'} <= obj.keys():
                    npc_positions[obj['oid']] = obj
        elif kind == 'object_remove':
            npc_positions.pop(row.get('oid'), None)
        elif kind == 'client_action' and row.get('opcode') in (0x39, 0x43) and position and row.get('packetId'):
            if (npc := npc_positions.get(row.get('targetId'))) and npc['floor'] != position[2]:
                requests[row['packetId']] = (action[:2], position, npc, row['opcode'], row.get('menu'))
                if len(requests) > 32:
                    requests.pop(next(iter(requests)))
        elif kind in ('merchant_dialog', 'npc_pursuit'):
            request = row.get('npcRequest', {}).get('packetId')
            if matched := requests.pop(request, None):
                location, actor, npc, opcode, menu = matched
                if location == action[:2] and npc['oid'] == row.get('oid') and map_id in (None, area):
                    cross_floor.append(dict(map=area, request=request, response=row.get('packetId'), opcode=opcode, menu=menu,
                        actor=actor, target=(npc['x'], npc['y'], npc['floor']), sprite=npc['sprite'], category=npc['category']))
        guild_refusal = (kind == 'npc_pursuit' and
                         (row.get('mode'), row.get('flags'), row.get('script')) == (0, 4, 0) and
                         '길드마스터가 와야지만 이 길드집을 점령' in row.get('text', ''))
        if kind == 'context_reset' or flag_refresh and action[:2] != flag_refresh['scope']:
            flag_refresh = None
        if kind == 'frame_header':
            opcode, direction = row.get('opcode'), row.get('direction')
            if direction == 'C':
                flag_refresh = (dict(scope=action[:2], request=row['packetId'], position=position, packets=[])
                                if opcode == 0x38 and row.get('plainLength') == 1 and position and map_id in (None, area)
                                else None)
            elif flag_refresh and (opcode == 0x15 or flag_refresh['packets']):
                flag_refresh['packets'].append(dict(packet=row['packetId'], opcode=opcode, length=row.get('plainLength')))
                if len(flag_refresh['packets']) > 16:
                    flag_refresh = None
        if guild_refusal and flag_refresh:
            packets = flag_refresh['packets']
            if (position == flag_refresh['position'] and
                [p['opcode'] for p in packets[:3]] == [0x15,0x14,0x04] and packets[-1]['packet'] == row['packetId']):
                flag = next((npc for npc in npc_positions.values() if npc.get('sprite') == 0x4061 and
                             npc.get('name') == '길드기' and position == (npc['x'],npc['y']-1,npc['floor'])), None)
                if flag:
                    guild_flag_refreshes.append(dict(map=area, request=flag_refresh['request'], dialog=row['packetId'],
                                                     position=position, flag_oid=flag['oid'], packets=packets))
            flag_refresh = None
        if kind in ('context_reset', 'map', 'client_action'):
            flag_move = flag_dialog = None
            if kind == 'client_action' and row.get('opcode') == 0x33 and position and map_id in (None, area):
                flag_move = action[:2], row.get('packetId'), row.get('direction'), position
        elif guild_refusal and flag_move and action[:2] == flag_move[0]:
            flag_dialog = row
        elif kind == 'self_move' and flag_move:
            scope, request, direction, origin = flag_move
            if (flag_dialog and action[:2] == scope and direction == row.get('direction') and
                not row.get('correction') and (row.get('wireX'), row.get('wireY'), row.get('floor')) == origin):
                for npc in npc_positions.values():
                    if (npc.get('sprite') == 0x4061 and npc.get('name') == '길드기' and
                        position == (npc['x'], npc['y'] - 1, npc['floor'])):
                        guild_flag_steps.append(dict(map=area, request=request, dialog=flag_dialog['packetId'],
                            movement=row['packetId'], origin=origin, destination=position, flag_oid=npc['oid'],
                            dialog_oid=flag_dialog.get('oid'), text=flag_dialog['text']))
                        break
            flag_move = flag_dialog = None
        if kind == 'context_reset':
            last_vitals = recovery = recovery_action = pending_recovery_next = None
        elif kind == 'client_action':
            if pending_recovery_next is not None:
                epoch, result, request = pending_recovery_next
                if action[0] == epoch:
                    result['terminal_next'] = dict(request=request, next_action=row.get('packetId'))
                pending_recovery_next = None
            recovery = recovery_action = None
            if action[2] is not None and row.get('opcode') == 0x39 and row.get('menu') == 0x6F:
                recovery_action = action
                recovery = dict(map=area, request=row.get('packetId'), before=last_vitals, statuses=[], effects=[], sequence=[])
        elif kind == 'status':
            if row.get('currentValues'):
                last_vitals = dict(packet=row.get('packetId'), mask=row['mask'], values=row['currentValues'])
                if action == recovery_action:
                    recovery['statuses'].append(last_vitals)
            if action == recovery_action:
                recovery['sequence'].append(dict(packet=row.get('packetId'), opcode=8, mask=row['mask'], length=row.get('length')))
        elif recovery is not None and action == recovery_action:
            if kind == 'effect' and row.get('effect') in (0x000E, 0x800B):
                effect = dict(packet=row.get('packetId'), effect=row['effect'])
                recovery['effects'].append(effect)
                recovery['sequence'].append(dict(effect, opcode=0x29, length=row.get('length')))
            elif kind == 'damage_effect' and row.get('effect') == 9 and row.get('words') == [24, 27, 0, 1002]:
                recovery['sequence'].append(dict(packet=row.get('packetId'), opcode=0x13, length=row.get('length')))
            elif kind == 'frame_header' and row.get('direction') == 'S' and row.get('opcode') == 0x30:
                effects = {item['effect'] for item in recovery['effects']}
                if recovery['statuses'] and effects == {0x000E, 0x800B} and map_id in (None, area):
                    recovery['sequence'].append(dict(packet=row['packetId'], opcode=0x30, length=row.get('plainLength')))
                    recoveries.append(dict(recovery, dialog=row['packetId']))
                recovery = recovery_action = None
        if kind=='frame_header' and row.get('direction')=='S' and row.get('opcode')==0x30:
            pending_recovery_next = None
        if map_id is not None and area != map_id:
            continue
        if kind=='frame_header':
            if pending_header is not None:
                header_only.append(pending_header)
                pending_header = None
            if row.get('direction')=='S' and row.get('opcode')==0x30:
                pending_header = dict(map=area,packetId=row['packetId'],length=row.get('plainLength'))
        elif kind=='npc_pursuit' and pending_header and pending_header['packetId']==row['packetId']:
            pending_header = None
        scope = row.get('epoch'), area
        if kind=='client_action' and row.get('opcode')==0x3A and row.get('dialogAction')==0:
            shown = visible.get((*scope,row.get('targetId')))
            requested = row.get('page')
            if shown and requested is not None and row.get('script')==shown.get('script') and row.get('mode')==shown.get('flags'):
                delta = requested-shown['page']
                direction = {-1:'back',0:'close',1:'next'}.get(delta)
                if direction:
                    navigation.append(dict(map=area,request=row['packetId'],shown=shown['packetId'],direction=direction,
                                           shown_page=shown['page'],requested_page=requested))
                if direction=='next' and recoveries and recoveries[-1]['dialog']==shown.get('packetId'):
                    pending_recovery_next = action[0], recoveries[-1], row.get('packetId')
                if direction=='close':
                    visible.pop((*scope,row.get('targetId')),None)
        if kind not in ('merchant_dialog','npc_pursuit'):
            continue
        if kind=='npc_pursuit' and 'page' in row:
            visible[*scope,row.get('oid')] = row
        counts[kind] += 1
        if row.get("mode") == 10:
            continue
        if row.get("npcRequest", {}).get("dialogAction") == 2:
            continue
        fields = ("map", "oid", "packetId", "mode", "flags", "text", "options", "prompt", "menuCodes", "script", "page", "back", "next", "npcRequest")
        pages.append({key: row[key] for key in fields if key in row})
    if pending_header is not None:
        header_only.append(pending_header)
    return {"session": path.parent.name, "counts": dict(counts), "pages": pages[-limit:], "navigation":list(navigation),
            "pursuit_headers_without_body":list(header_only), "recovery_sequences":list(recoveries),
            "cross_floor_interactions":list(cross_floor), "guild_flag_steps":list(guild_flag_steps),
            "guild_flag_refreshes":list(guild_flag_refreshes)}


@server.tool(annotations=READ_ONLY)
def web_board_observations(session: str = "latest",
                           limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """게시판 상호작용과 S62 응답의 모드 및 문자열 길이를 대조합니다."""
    path = session_events(session)
    boards, requests = {}, deque()
    results = deque(maxlen=limit)
    last_response = None
    for row in recorded_rows(path):
        area, kind = row.get('map'), row['kind']
        if kind in ('context_reset','map'):
            requests.clear()
            last_response = None
            if kind=='context_reset':
                boards.clear()
            continue
        if kind not in ('objects','client_action','frame_header','web_board'):
            continue
        scope, op = (row.get('epoch'),area), row.get('opcode')
        if kind=='objects':
            for obj in row['objects']:
                if obj.get('category')==1 and obj.get('sprite')==0x4037 and obj.get('name')=='게시판':
                    boards[scope,obj['oid']] = [obj['x'],obj['y'],obj['floor']]
        elif kind=='client_action' and op in (0x43,0x73):
            target = row.get('targetId')
            position = boards.get((scope,target))
            if op==0x43 and position is None:
                continue
            at = observed_time(row)
            while requests and at-requests[0][0]>1:
                requests.popleft()
            requests.append((at,scope,dict(packet=row.get('packetId'),opcode=op,mode=row.get('mode'),
                                           target_id=target,position=position)))
        elif kind=='frame_header' and op==0x62 and row.get('direction')=='S':
            at = observed_time(row)
            candidates = [r for sent,context,r in requests if context==scope and 0<=at-sent<=1]
            last_response = dict(map=area,packet=row.get('packetId'),length=row.get('plainLength'),
                                 request_candidates=candidates)
            results.append(last_response)
            requests.clear()
        elif kind=='web_board' and last_response and row.get('packetId')==last_response['packet']:
            last_response.update(mode=row['mode'],string_bytes=row['stringBytes'],
                                 trailing_bytes=row['trailingBytes'])
    return dict(session=path.parent.name,observations=list(results))


@server.tool(annotations=READ_ONLY)
def npc_input_observations(session: str = "latest", map_id: int | None = None,
                           limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """C3A 텍스트 입력의 길이와 뒤따른 S30 입력창·확인창 구조를 조회합니다."""
    path = session_events(session)
    pending, results = {}, deque(maxlen=limit)
    for row in recorded_rows(path):
        area = row.get('map')
        if map_id is not None and area != map_id:
            continue
        scope = row.get('epoch'), area, row.get('recentActionId')
        if row['kind']=='client_action' and row.get('opcode')==0x3A and row.get('dialogAction')==2:
            request = {key:row[key] for key in ('packetId','map','targetId','mode','script','page','length') if key in row}
            if row.get('length',0)>=12:
                request['input_bytes']=row.get('dialogInputBytes',row['length']-12)
            results.append(request)
            pending[scope] = request
        elif row['kind']=='npc_pursuit' and scope in pending and row.get('oid')==pending[scope]['targetId']:
            request = pending.pop(scope)
            request['reply'] = {key:row[key] for key in ('packetId','mode','script','page','back','next','length','inputLimit') if key in row}
            if 'options' in row:
                request['reply']['option_count'] = len(row['options'])
    return {'session':path.parent.name,'observations':list(results)}


@server.tool(annotations=READ_ONLY)
def barber_observations(session: str = "latest") -> dict[str, Any]:
    """미용바니의 C3A 확인, 갈리드 차감과 S33 스타일·색상 바이트를 연결합니다."""
    path = session_events(session)
    states, pending, observations = {}, {}, []
    gold = epoch = None
    for row in recorded_rows(path):
        area, kind = row.get('map'), row['kind']
        if row.get('epoch')!=epoch or kind=='context_reset':
            epoch = row.get('epoch')
            gold = None
        if kind=='status' and 'gold' in row:
            gold = row['gold']
        if area not in (210,10210,20210):
            continue
        scope = row.get('epoch'), area
        state = states.setdefault(scope,{})
        attempt = pending.get(scope)
        same_action = attempt and attempt['action_id']==row.get('recentActionId')
        if kind=='status' and 'gold' in row:
            if same_action:
                attempt.update(gold_after=row['gold'],status=row['packetId'])
        elif kind=='human' and row.get('isSelf'):
            state['appearance'] = row['appearance']
            state['appearance_packet'] = row['packetId']
            if same_action:
                attempt.update(appearance_after=row['appearance'],appearance_packet=row['packetId'])
        elif kind in ('self_position','self_move'):
            state['position']=[row[k] for k in ('x','y','floor')]
        elif kind=='client_action' and row.get('opcode')==0x3A and row.get('script')==196:
            service = None
            if row.get('page') in (93,115) and row.get('dialogAction')==0:
                service = {'style':'펑크' if row['page']==93 else '레게'}
            elif row.get('page')==160 and row.get('dialogAction')==1 and 1<=row.get('dialogChoice',0)<=14:
                service = {'dye_choice':row['dialogChoice']}
            if service is not None:
                pending[scope]=dict(service,request=row['packetId'],map=area,
                    action_id=row.get('recentActionId'),gold_before=gold,
                    appearance_before=state.get('appearance'),before_packet=state.get('appearance_packet'),
                    position=state.get('position'))
        elif kind=='inventory_add' and same_action and row.get('wireSprite')==0x8BB8:
            attempt['wallet']=row['packetId']
        elif kind=='npc_pursuit' and same_action and row.get('script')==196 and row.get('page') in (103,122,246):
            if row.get('npcRequest',{}).get('packetId')!=attempt['request']:
                continue
            attempt.update(result=row['packetId'],outcome='already_style' if row['page']==103 else 'changed')
            if attempt.get('gold_before') is not None and 'gold_after' in attempt:
                attempt['charge']=attempt['gold_before']-attempt['gold_after']
            before, after = attempt.get('appearance_before'),attempt.get('appearance_after')
            if before and after:
                attempt['changed_byte_offsets']=[i for i,(a,b) in enumerate(zip(bytes.fromhex(before),bytes.fromhex(after))) if a!=b]
            del attempt['action_id']
            observations.append(pending.pop(scope))
    return dict(session=path.parent.name,observations=observations[-30:])


@server.tool(annotations=READ_ONLY)
def online_fame_observations(session: str = "latest") -> dict[str, Any]:
    """접속 명성 S08의 주기, 차원별 응답 형식과 조작·사망 없이 명성이 6 오른 표본을 조회합니다."""
    path = session_events(session)
    previous = entered = last_action = last_activity = epoch = None
    first, periodic = deque(maxlen=20), deque(maxlen=30)
    cadence, mixed = deque(maxlen=20), deque(maxlen=30)
    quiet_double = deque(maxlen=30)
    single_gains, combined_gains = [], []
    periodic_count = quiet_double_count = 0
    periodic_formats = Counter()

    def finish_connection():
        if len(single_gains) < 3:
            return
        period, tolerance = 180, 1

        def offset(at, anchor):
            return (at-anchor+period/2) % period-period/2

        best = []
        for anchor, _ in single_gains:
            matches = {round((at-anchor)/period): (at,sample) for at,sample in single_gains
                       if abs(offset(at,anchor)) <= tolerance}
            if len(matches) > len(best):
                best = list(matches.values())
        if len(best) < 3:
            return
        anchor = best[0][0]
        phase = anchor+median(offset(at,anchor) for at,_ in best)
        cadence.append(dict(epoch=epoch, period_seconds=period, anchor_packets=[sample['after'] for _,sample in best],
                            max_phase_error_seconds=round(max(abs(offset(at,phase)) for at,_ in best),3)))
        for at, sample in combined_gains:
            error = offset(at,phase)
            if best[0][0] <= at <= best[-1][0] and abs(error) <= tolerance and sample['interval_seconds'] < period-tolerance:
                mixed.append(dict(sample,epoch=epoch,phase_offset_seconds=round(error,3),online_fame_candidate=3,
                                  remaining_delta=sample['delta']-3))

    for row in recorded_rows(path):
        kind = row['kind']
        if row.get('epoch') != epoch or kind == 'context_reset':
            finish_connection()
            single_gains.clear()
            combined_gains.clear()
            epoch = row.get('epoch')
            previous = entered = last_action = last_activity = None
        if kind == 'self_identity':
            entered = last_activity = observed_time(row)
        elif kind == 'client_action':
            last_action = last_activity = observed_time(row)
        elif kind in ('death','map'):
            last_activity = observed_time(row)
        elif kind == 'status' and (fame := status_fame(row)) is not None and row.get('mask',0)&0x40:
            if previous and row.get('characterLevel')==previous.get('characterLevel') and (delta := fame-status_fame(previous)) >= 3:
                when = observed_time(row)
                interval = when-observed_time(previous)
                base = row.get('baseBytes') or []
                prefix = base[0] if base else None
                length = row.get('length')
                sample = dict(before=previous.get('packetId'),after=row.get('packetId'),map=row.get('map'),fame=fame,
                              level=row.get('characterLevel'),mask=row['mask'],state=row['state'],
                              length=length,base_prefix=base[:3] or None,
                              connection_dimension=prefix >> 4 if prefix is not None else None,
                              base_low_nibble=prefix & 0x0F if prefix is not None else None,
                              interval_seconds=round(interval,3),
                              idle_seconds=None if last_action is None else round(when-last_action,3))
                if row['mask']==0x40 and length in (49,50) and (raw := row.get('raw')) and len(raw)==2*length:
                    sample['trailing_bytes'] = raw[98:]
                if delta > 3:
                    combined_gains.append((when,dict(sample,delta=delta)))
                    if (delta == 6 and row['mask'] == previous['mask'] == 0x40 and 178 <= interval <= 182
                            and sample['map'] is not None and sample['map'] == previous.get('map')
                            and (last_activity is None or last_activity < observed_time(previous))):
                        quiet_double.append(dict(sample,delta=delta))
                        quiet_double_count += 1
                else:
                    single_gains.append((when,sample))
                    if entered is not None and 0 <= when-entered <= 1:
                        first.append(sample)
                    elif 178 <= interval <= 182:
                        periodic.append(sample)
                        periodic_count += 1
                        periodic_formats[prefix,row['mask'],length,sample.get('trailing_bytes')] += 1
            previous = row
    finish_connection()
    return dict(session=path.parent.name,entry=list(first),periodic_count=periodic_count,periodic=list(periodic),
                quiet_double_count=quiet_double_count,quiet_double=list(quiet_double),
                periodic_formats=[dict(base_first_byte=prefix,mask=mask,length=length,trailing_bytes=suffix,count=count)
                                  for (prefix,mask,length,suffix),count in periodic_formats.most_common()],
                cadence=list(cadence),mixed_updates=list(mixed))


@server.tool(annotations=READ_ONLY)
def revival_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=30)] = 10) -> dict[str, Any]:
    """자기 캐릭터의 스턴 효과 간격, 쓰러진 위치, 파란 화면과 부활 직후 체력·마력을 조회합니다."""
    path = session_events(session)
    episodes = deque(maxlen=limit)
    active = last_zero = downed_oid = None
    for row in recorded_rows(path):
        kind, area = row['kind'], row.get('map')
        if kind=='context_reset':
            active = last_zero = downed_oid = None
        if active and (row.get('epoch')!=active[0] or observed_time(row)-active[1]>35):
            active = None
        if kind=='timed_spell' and row.get('id')==22 and row.get('name')=='Stun':
            if row.get('duration'):
                at = observed_time(row)
                start = dict(packet=row.get('packetId'), map=area, duration=row['duration'])
                if last_zero and last_zero.get('epoch')==row.get('epoch') and last_zero.get('map')==area and 0<=at-observed_time(last_zero)<=3:
                    start['zero_hp_packet'] = last_zero.get('packetId')
                    start['zero_hp_status'] = dict(mode=last_zero.get('state'),mask=last_zero.get('mask'),
                                                   aux=last_zero['currentValues'][3],length=last_zero.get('length'))
                active = (row.get('epoch'),at,dict(stun_start=start,stun_effects=[],normal_status=[],blessing_clear=[]))
                downed_oid = None
            elif active and area==active[2]['stun_start']['map']:
                active[2]['stun_end'] = row.get('packetId')
                active[2]['stun_clear_after_ms'] = round((observed_time(row)-active[1])*1000)
        if kind=='status' and row.get('currentValues') and row['currentValues'][0]==0:
            last_zero = row
        if not active:
            continue
        _,start_time,event = active
        if kind=='human' and row.get('isSelf') and area==event['stun_start']['map']:
            appearance = bytes.fromhex(row['appearance'])
            if len(appearance)>1:
                downed = bool(appearance[1]&0x40)
                key = 'downed_appearance' if downed else 'standing_appearance'
                if downed:
                    downed_oid = row.get('oid')
                event.setdefault(key,dict(packet=row.get('packetId'),position=[row['x'],row['y'],row['floor']],
                                          after_ms=round((observed_time(row)-start_time)*1000)))
        elif (kind=='effect' and area==event['stun_start']['map'] and 'stun_end' not in event
              and downed_oid is not None and row.get('oid')==downed_oid and row.get('effect')==22
              and row.get('parameter')==100 and len(event['stun_effects'])<32):
            event['stun_effects'].append(dict(packet=row.get('packetId'),length=row.get('length'),
                                               after_ms=round((observed_time(row)-start_time)*1000)))
        elif kind=='frame_header' and row.get('direction')=='S' and row.get('opcode')==0x0A and (
                area==event['stun_start']['map'] and 'stun_end' in event and 'arrival' not in event):
            length, mode = row.get('plainLength'), row.get('messageMode')
            key = ('unconscious_notice' if length==22 and mode in (3,None) else
                   'transition_notice' if length==81 and mode in (8,None) else None)
            if key:
                event.setdefault(key,dict(packet=row.get('packetId'),mode=mode,length=length))
        elif (kind=='timed_spell' and area==event['stun_start']['map'] and 'stun_end' in event
              and 'arrival' not in event and row.get('id')==22 and row.get('duration')==0
              and row.get('name','').startswith('엘의축복')):
            event['blessing_clear'].append(dict(packet=row.get('packetId'),length=row.get('length')))
        elif kind=='map' and 'stun_end' in event:
            event['arrival'] = dict(packet=row.get('packetId'),map=area)
        elif kind=='self_position' and (arrival := event.get('arrival')) and area==arrival['map']:
            arrival['position'] = [row['x'],row['y'],row['floor']]
        elif kind=='status':
            state, values = row.get('state'), row.get('currentValues')
            if area==event['stun_start']['map'] and state==0x20 and 'arrival' not in event:
                if values and values[0]==1:
                    event.setdefault('blue_vitals',dict(packet=row.get('packetId'),hp=1,mp=values[1],aux=values[3],length=row['length']))
                elif values and values[0]==0 and 'stun_end' in event:
                    event.setdefault('unconscious_vitals',dict(packet=row.get('packetId'),values=values,length=row.get('length')))
                elif not values and row.get('mask')==0:
                    event.setdefault('blue_screen',row.get('packetId'))
            elif area==event['stun_start']['map'] and state==1 and 'stun_end' in event and 'arrival' not in event:
                if row.get('mask')==0:
                    event['normal_status'].append(dict(packet=row.get('packetId'),length=row.get('length')))
                elif values and row.get('mask')==0x28 and 'transition_notice' in event:
                    event.setdefault('pre_transfer_vitals',dict(packet=row.get('packetId'),values=values,
                        flags=row.get('extraDword'),movement_percent=row.get('extraWord'),length=row.get('length')))
            elif (arrival := event.get('arrival')) and area==arrival['map'] and values and state==1:
                arrival.update(vitals_packet=row.get('packetId'),hp=values[0],mp=values[1],satiety=values[2],aux=values[3],length=row['length'])
                episodes.append(event)
                active = None
    return dict(session=path.parent.name,revivals=list(episodes))


@server.tool(annotations=READ_ONLY)
def fame_exchange_observations(session: str = "latest") -> dict[str, Any]:
    """세르니카 명성 환전의 견적, 확인, S08·S0F 잔액과 레벨을 대조합니다."""
    path = session_events(session)
    statuses, balances, pending = {}, {}, {}
    results = []
    for row in recorded_rows(path):
        area = row.get("map")
        if area not in (2229,12229,22229):
            continue
        scope = row.get("epoch"), area
        kind = row["kind"]
        active = pending.get(scope)
        if kind == "status":
            if row.get("mask",0)&0x40 and (fame := status_fame(row)) is not None:
                statuses[scope] = dict(fame=fame,level=row["characterLevel"],packet=row["packetId"])
            if "gold" in row:
                balances[scope] = dict(gold=row["gold"],packet=row["packetId"])
        if kind == "npc_pursuit" and row.get("oid")==0x8F46:
            content = row.get("text","")
            if content.startswith("받으실 돈은") and row.get("npcRequest",{}).get("dialogAction")==2:
                amount = [int(value) for value in re.findall(r"\{=j([0-9]+)",content)]
                if len(amount)==2 and amount[1]>0:
                    credit, spent = amount
                    pending[scope] = dict(quote=row["packetId"],map=area,fame_cost=spent,galrid_credit=credit,
                        fame_before=statuses.get(scope),gold_before=balances.get(scope),wallets=[])
                continue
        if not active:
            continue
        if kind == "client_action" and row.get("opcode")==0x3A and row.get("targetId")==0x8F46:
            if (row.get("mode"),row.get("page"),row.get("dialogAction"),row.get("dialogChoice"))==(4,2,1,1):
                active.update(confirm=row["packetId"],action=row.get("recentActionId"))
            else:
                pending.pop(scope,None)
            continue
        if "confirm" not in active or active["action"]!=row.get("recentActionId"):
            continue
        if kind=="status" and row.get("mask")==0x48:
            active["fame_after"] = statuses.get(scope)
        elif kind=="status" and row.get("mask")==0x10:
            active["gold_after"] = balances.get(scope)
        elif kind=="inventory_add" and row.get("wireSprite")==0x8BB8:
            active["wallets"].append(dict(packet=row["packetId"],gold=row["quantity"],position=[row["word1"],row["word2"]],
                                          context=row.get("word3")))
        elif kind=="sound" and not row.get("bgm"):
            active["sound"] = dict(id=row["id"],packet=row["packetId"])
        elif kind=="npc_pursuit" and row.get("oid")==0x8F46 and "갈리드를 얻으셨습니다" in row.get("text",""):
            if row.get("npcRequest",{}).get("packetId")!=active["confirm"]:
                continue
            active["result"] = row["packetId"]
            before, after = active.get("fame_before"), active.get("fame_after")
            gold_before, gold_after = active.get("gold_before"), active.get("gold_after")
            active["verified"] = bool(before and after and gold_before and gold_after and
                before["fame"]-after["fame"]==active["fame_cost"] and gold_after["gold"]-gold_before["gold"]==active["galrid_credit"] and
                before["level"]==after["level"] and len(active["wallets"])==2 and
                all(wallet["gold"]==gold_after["gold"] for wallet in active["wallets"]))
            del active["action"]
            results.append(active)
            pending.pop(scope)
    return dict(session=path.parent.name,exchanges=results)


@server.tool(annotations=READ_ONLY)
def guild_observations(session: str = "latest") -> dict[str, Any]:
    """로랜시아 길드 창립 조건과 거절 응답, 탈퇴 비용의 지갑·상태 패킷을 조회합니다."""
    path = session_events(session)
    findings, confirmations, settlement = [], {}, {}
    for row in recorded_rows(path):
        area, kind = row.get("map"), row["kind"]
        if area not in (2, 10002, 20002):
            continue
        scope = row.get("epoch"), area
        if kind == "client_action":
            shown = confirmations.get(scope)
            if shown and row.get("opcode") == 0x3A and row.get("targetId") == shown["oid"]:
                if shown["result"] == "awaiting_choice":
                    if row.get("dialogAction") == 1 and row.get("dialogChoice") == 1:
                        shown.update(request=row["packetId"], result="no_linked_response")
                    else:
                        shown["result"] = "cancelled"
        if kind == "inventory_add" and row.get("wireSprite") == 0x8BB8:
            settlement[scope] = {"action": row.get("recentActionId"), "wallet": row["packetId"]}
        elif kind == "status" and row.get("mask") == 0x50 and scope in settlement:
            payment = settlement[scope]
            if payment["action"] == row.get("recentActionId"):
                payment.update(status=row["packetId"], gold=row.get("gold"))
        if kind != "npc_pursuit":
            continue
        body = row.get("text", "")
        event = {"packet": row["packetId"], "map": area, "opcode": "S30"}
        if match := re.search(r"길드\{=a는 \{=j(\d+)\{=a명.*기부금 \{=j(\d+)\{=a 갈리드", body):
            event.update(kind="founding_cost", founders=int(match[1]), galrid=int(match[2]))
        elif match := re.search(r"길드헌납금\] \{=g(\d+)\{=a 갈리드", body):
            event.update(kind="departure_cost", galrid=int(match[1]))
        elif body.startswith("자네가 만들고자 하는 길드의 이름이"):
            event.update(kind="name_confirmation", oid=row["oid"], result="awaiting_choice")
            confirmations[scope] = event
        elif body.startswith("자네는 이미 소속 길드가"):
            event["kind"] = "founder_in_guild"
        elif body.startswith("자네와 같이 길드를 만드려는 사람 중에"):
            event["kind"] = "cofounder_in_guild"
        elif body.startswith("자네까지 세명이 모두 여기"):
            event["kind"] = "missing_cofounders"
        elif body.startswith("그 이름의 길드는 만들 수가 없다네"):
            event["kind"] = "name_rejected"
        elif body.startswith("[길드헌납금]을 받고 당신의 탈퇴신청"):
            event["kind"] = "departed"
            payment = settlement.get(scope, {})
            if payment.get("action") == row.get("recentActionId") and "status" in payment:
                event.update({key: payment[key] for key in ("wallet", "status", "gold")})
        else:
            continue
        confirmed = confirmations.get(scope)
        if confirmed and confirmed.get("request") == row.get("npcRequest", {}).get("packetId") and "request" in confirmed:
            confirmed.update(result=event["kind"], response=row["packetId"])
        findings.append(event)
    return {"session": path.parent.name, "observations": findings}


@server.tool(annotations=READ_ONLY)
def bank_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 24) -> dict[str, Any]:
    """은행의 설정·인증·보관 동작을 비밀번호 본문 없이 조회합니다."""
    path = session_events(session)
    rows = deque(maxlen=limit)
    sprites = Counter()
    count = 0
    open_request = None
    last_header = None
    for row in recorded_rows(path):
        kind, opcode, map_id = row.get('kind'), row.get('opcode'), row.get('map')
        if kind == 'frame_header' and opcode == 0x61 and row.get('direction') == 'S':
            last_header = row
        if kind in ('client_action','context_reset'):
            open_request = row if opcode == 0x39 and row.get('menu') == 0x87 and row.get('targetId') else None
            last_header = None
        if kind == 'bank_open':
            fields = ('openingWord','count','entries','request_packet','requested_banker','matches_request')
            sprites.update(item['wireSprite'] for item in row.get('entries',[]))
            if (open_request and row.get('recentActionId') is not None
                    and all(open_request.get(key)==row.get(key) for key in ('epoch','map','recentActionId'))):
                row = dict(row,request_packet=open_request['packetId'],requested_banker=open_request['targetId'],
                           matches_request=row['openingWord']==open_request['targetId'])
                open_request = None
        elif kind == 'bank_slot':
            fields = ('slot','wireSprite','quantity','word1','word2','stackFlag','durabilityByte','remainingMinutes','itemFlag')
            sprites[row['wireSprite']] += 1
        elif kind == 'bank_remove':
            fields = ('slot',)
        elif kind == 'bank_quote':
            fields = ('slot', 'quoteWord', 'fee')
        elif kind == 'client_action' and row.get('action') == 'bank_move':
            fields = ('mode', 'slot', 'destinationSlot', 'dropX', 'dropY', 'dropQuantity')
        elif map_id is not None and map_id % 10000 == 209:
            if kind == 'client_action' and opcode == 0x39 and row.get('menu') in (0x82, 0x84, 0x87, 0x96):
                fields = ('mode', 'menu', 'length')
            elif kind == 'merchant_dialog' and row.get('mode')==12:
                if 'actionCode' not in row and row.get('raw'):
                    try:
                        row = dict(row, actionCode=decode(bytes.fromhex(row['raw'])).get('actionCode'))
                    except (ValueError,IndexError):
                        continue
                if row.get('actionCode') not in (0x82, 0x83, 0x84, 0x85, 0x86):
                    continue
                fields = ('mode', 'actionCode', 'length')
            elif kind == 'frame_header' and opcode == 0x61 and row.get('direction') == 'S':
                fields = ('plainLength',)
            else:
                continue
        else:
            continue
        result = {k:row[k] for k in ('kind','opcode','map','packetId','recentActionId','length','trailingBytes',
                                    'nativeBytesConsumed',*fields) if k in row}
        if kind in ('bank_open','bank_slot','bank_remove','bank_quote') and last_header and all(
                last_header.get(key)==row.get(key) for key in ('epoch','packetId')):
            result['plainLength'] = last_header.get('plainLength')
        rows.append(result)
        count += 1
    catalog = {}
    if sprites:
        base = read_json(ROOT/'world-recorded.json').get('Items',[])
        live = read_json(LIVE).get('Items',[]) if LIVE.is_file() else []
        for item in {item['Key']:item for item in base+live}.values():
            if item['Sprite'] in sprites:
                catalog.setdefault(item['Sprite'],[]).append(dict(key=item['Key'],name=item['Name']))
    return {'session':path.parent.name,'count':count,'observations':list(rows),
            'catalog_matches':[dict(sprite=sprite,observations=total,items=catalog.get(sprite,[]))
                               for sprite,total in sorted(sprites.items())]}


@server.tool(annotations=READ_ONLY)
def party_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """C2E 파티 요청, S5D 명단과 체력 갱신을 이름과 메시지 본문 없이 조회합니다."""
    path = session_events(session)
    requests, roster_changes, vitals = (deque(maxlen=limit) for _ in range(3))
    modes, repeated_vitals = Counter(), Counter()
    previous_values = {}
    fields = ('mode','slot','memberValue','nameBytes','leader','enabled','nativeBytesConsumed','trailingBytes')
    pending = notice = None
    for row in recorded_rows(path):
        kind, opcode = row['kind'], row.get('opcode')
        if kind == 'context_reset':
            previous_values.clear()
            notice = None
        if kind == 'frame_header' and row['direction']=='S' and opcode==0x0A and row.get('messageMode')==3:
            notice = row
        if kind in ('context_reset','client_action'):
            pending = None
            if kind == 'client_action' and opcode == 0x2E:
                entry = dict(request=row['packetId'], mode=row['mode'], map=row.get('map'), replies=[])
                if 'slot' in row:
                    entry['slot'] = row['slot']
                requests.append(entry)
                pending = (row,entry)
        if kind == 'party':
            mode, slot = row['mode'], row.get('slot')
            modes[mode] += 1
            update = dict(packet=row['packetId'], map=row.get('map'), length=row.get('length'),
                          **{key:row[key] for key in fields if key in row})
            if mode == 1 and 'memberValue' in row:
                value = row['memberValue']
                if slot in previous_values:
                    update['same_value_as_previous'] = previous_values[slot] == value
                    if update['same_value_as_previous']:
                        repeated_vitals[slot] += 1
                previous_values[slot] = value
                vitals.append(update)
            else:
                if (mode in (0,2) and notice and row.get('map')==notice.get('map')
                        and row.get('epoch')==notice.get('epoch') and adjacent_packets(notice['packetId'],row['packetId'])
                        and 0<=observed_time(row)-observed_time(notice)<=.1):
                    update['preceding_notice'] = dict(packet=notice['packetId'], mode=3, length=notice['plainLength'])
                    template = row.get('noticeTemplate')
                    if isinstance(template,dict) and template.get('packetId')==notice['packetId']:
                        prefix,suffix=template.get('prefix'),template.get('suffix')
                        if valid_party_notice_affix(prefix) and valid_party_notice_affix(suffix) and '파티' in prefix+suffix:
                            update['preceding_notice']['template']=prefix+'{member}'+suffix
                if mode == 0 and 'memberValue' in row:
                    previous_values[slot] = row['memberValue']
                elif mode == 2:
                    previous_values.pop(slot, None)
                elif mode == 3:
                    previous_values.clear()
                roster_changes.append(update)
        if not pending or row.get('recentActionId') != pending[0].get('recentActionId'):
            continue
        request, entry = pending
        elapsed = datetime.datetime.fromisoformat(row['time']) - datetime.datetime.fromisoformat(request['time'])
        if not 0 <= elapsed.total_seconds() <= 1:
            pending = None
            continue
        if kind == 'frame_header' and row['direction'] == 'S' and opcode in (0x0A,0x5D):
            entry['replies'].append(dict(packet=row['packetId'], opcode=f'S{opcode:02X}', length=row['plainLength']))
        elif kind == 'party':
            reply = next((p for p in reversed(entry['replies']) if p['packet'] == row['packetId']),None)
            if reply is not None:
                reply.update({key:row[key] for key in fields if key in row})
    return dict(session=path.parent.name, roster_modes=dict(sorted(modes.items())), requests=list(requests),
                roster_changes=list(roster_changes), vitals=list(vitals), repeated_vitals_by_slot=dict(sorted(repeated_vitals.items())))


def friend_list_fields(row):
    fields = {key:row[key] for key in ('mode','count','status') if key in row}
    event = row
    if ('trailingBytes' not in event or event.get('mode')==0 and 'nameLengths' not in event) and row.get('raw'):
        try:
            event = decode(bytes.fromhex(row['raw']))
        except (ValueError,IndexError):
            return fields
    if 'trailingBytes' in event:
        fields.update(native_bytes=event.get('nativeBytesConsumed'),padding=event['trailingBytes'])
    if 'nameLengths' in event:
        fields['name_bytes_by_position'] = event['nameLengths']
    for source, target in (('bytewiseAscending','bytewise_ascending'),('targetIndex','target_index'),
                           ('otherEntriesKeptOrder','other_entries_kept_order')):
        if source in row:
            fields[target] = row[source]
    return fields


@server.tool(annotations=READ_ONLY)
def friend_request_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """C77 요청과 직후 S0A·S6A의 형태를 이름과 메시지 본문 없이 조회합니다."""
    path = session_events(session)
    requests = deque(maxlen=limit)
    pending = None
    for row in recorded_rows(path):
        kind, opcode, packet = row['kind'], row.get('opcode'), row.get('packetId')
        if kind=='context_reset':
            pending = None
        elif kind=='client_action' or kind=='frame_header' and row.get('direction')=='C':
            if pending and packet==pending[1]['request']:
                if kind=='client_action':
                    pending[1].update(mode=row.get('mode'),input_bytes=row.get('inputBytes'))
                    if 'targetPreviouslyListed' in row:
                        pending[1]['target_previously_listed']=row['targetPreviouslyListed']
                continue
            pending = None
            if opcode==0x77 and packet:
                entry = dict(request=packet, mode=row.get('mode'), length=row.get('length',row.get('plainLength')),
                             input_bytes=row.get('inputBytes'), replies=[])
                if 'targetPreviouslyListed' in row:
                    entry['target_previously_listed']=row['targetPreviouslyListed']
                requests.append(entry)
                pending = (row,entry)
        elif pending:
            request, entry = pending
            elapsed = (datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(request['time'])).total_seconds()
            if row.get('epoch')!=request.get('epoch') or not 0<=elapsed<=.25:
                pending = None
            elif kind=='frame_header' and row.get('direction')=='S' and opcode in (0x0A,0x6A):
                entry['replies'].append(dict(packet=packet,opcode=f'S{opcode:02X}',length=row.get('plainLength'),
                                             message_mode=row.get('messageMode')))
            elif kind=='friend_list' and entry['replies'] and entry['replies'][-1]['packet']==packet:
                entry['replies'][-1].update(friend_list_fields(row))
    return dict(session=path.parent.name,requests=list(requests))


@server.tool(annotations=READ_ONLY)
def private_chat_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """1:1 채팅·귓말 프레임과 기록된 숫자 필드를 조회합니다. 이름과 본문은 제외합니다."""
    path = session_events(session)
    frames = deque(maxlen=limit)
    header = None
    for row in recorded_rows(path):
        kind = row['kind']
        if kind=='context_reset':
            header = None
        elif kind=='frame_header':
            header = None
            if not (row.get('lengthMatches') and row.get('opcodeMatches')):
                continue
            direction,opcode,mode = row.get('direction'),row.get('opcode'),row.get('messageMode')
            if (direction=='C' and (opcode in (0x19,0x65) or opcode==0x0E and mode in (None,5)) or
                    direction=='S' and (opcode==0x57 or opcode==0x0D and mode==5 or opcode==0x0A and mode==0)):
                entry = dict(packet=row.get('packetId'),opcode=f'{direction}{opcode:02X}',mode=mode,
                             length=row.get('plainLength'),time=row.get('time'))
                header = (row.get('epoch'),direction,opcode,entry)
                if opcode!=0x0A:
                    frames.append(entry)
        elif kind in ('private_chat','chat_notice') and header:
            epoch,direction,opcode,entry = header
            if (row.get('epoch'),row.get('direction'),row.get('opcode'),row.get('packetId'))!=(
                    epoch,direction,opcode,entry['packet']):
                continue
            if kind=='chat_notice' and direction=='S' and opcode==0x0A:
                frames.append(entry)
            elif kind!='private_chat' or opcode==0x0A:
                continue
            for field,maximum in (('color',255),('trailingBytes',516),('trailingByte',255)):
                value = row.get(field)
                if type(value) is int and 0<=value<=maximum:
                    entry[field] = value
            header = None
    return dict(session=path.parent.name,frames=list(frames))


@server.tool(annotations=READ_ONLY)
def public_speech_observations(session: str = "latest",
                               limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """일반 채팅 C0E 04와 S0D 04의 색상, 문자열 길이, 패딩을 이름과 본문 없이 조회합니다."""
    path = session_events(session)
    frames, variants = deque(maxlen=limit), Counter()
    unknown_shapes = 0
    for row in recorded_rows(path):
        if row['kind']!='frame_header' or row.get('messageMode')!=4 or not (
                row.get('lengthMatches') and row.get('opcodeMatches')):
            continue
        direction,opcode = row.get('direction'),row.get('opcode')
        if (direction,opcode) not in (('C',0x0E),('S',0x0D)):
            continue
        fields = row.get('speech')
        entry = dict(packet=row.get('packetId'),opcode=f'{direction}{opcode:02X}',length=row['plainLength'],map=row.get('map'))
        if isinstance(fields,dict):
            color,size,padding = (fields.get(key) for key in ('color','textBytes','padding'))
            if (type(color) is int and 0<=color<=255 and type(size) is int and 0<=size<=255
                    and type(padding) is int and padding in (0,1) and size+padding+(8 if direction=='S' else 4)==entry['length']):
                entry.update(color=color,text_bytes=size,padding=padding)
                speaker = fields.get('speaker')
                if direction=='S' and type(speaker) is int and 0<=speaker<=0xffffffff:
                    entry['speaker_oid'] = speaker
                variants[direction,color,padding] += 1
        if 'color' not in entry:
            unknown_shapes += 1
        frames.append(entry)
    return dict(session=path.parent.name,header_only=unknown_shapes,
                variants=[dict(direction=side,color=color,padding=padding,count=count)
                          for (side,color,padding),count in variants.most_common()],frames=list(frames))


def panel_observations(session,limit,request_opcode,reply_opcode,action,fields):
    path = session_events(session)
    requests = deque(maxlen=limit)
    pending = None
    for row in recorded_rows(path):
        kind, opcode, packet = row['kind'], row.get('opcode'), row.get('packetId')
        if kind=='context_reset':
            pending = None
        elif opcode==request_opcode and packet and (
            kind=='frame_header' and row.get('direction')=='C' or row.get('action')==action
        ):
            if pending and pending[1]['request']==packet:
                if 'mode' in row:pending[1]['mode']=row['mode']
                continue
            entry = dict(request=packet,mode=row.get('mode'),length=row.get('plainLength',row.get('length')),nearby_replies=[])
            requests.append(entry)
            pending = (row,entry)
        elif pending and opcode==reply_opcode:
            request,entry = pending
            elapsed = (datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(request['time'])).total_seconds()
            if row.get('epoch')!=request.get('epoch') or not 0<=elapsed<=.5:
                continue
            if kind=='frame_header' and row.get('direction')=='S':
                entry['nearby_replies'].append(dict(packet=packet,length=row.get('plainLength')))
            elif row.get('raw'):
                try:
                    safe=sanitized_packet(bytes.fromhex(row['raw']))
                    if not safe or safe[0]!=reply_opcode:continue
                    decoded=decode(safe)
                except (IndexError,ValueError):
                    continue
                replies=entry['nearby_replies']
                if replies and replies[-1]['packet']==packet:
                    reply=replies[-1]
                    reply.update({key:decoded[key] for key in fields if key in decoded})
                    if reply_opcode==0x3D and type(reply['length']) is int and len(safe)<=reply['length']:
                        reply.update(retained_length=len(safe),omitted_bytes=reply['length']-len(safe))
    return dict(session=path.parent.name,requests=list(requests))


@server.tool(annotations=READ_ONLY)
def cash_shop_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """C6C 요청과 가까운 S45 응답의 모드 및 가려진 상품 목록을 조회합니다."""
    return panel_observations(session,limit,0x6C,0x45,'cash_shop_request',('mode','entries'))


@server.tool(annotations=READ_ONLY)
def clan_status_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """길드창 C36·S3D 쌍과 등급·레벨·길드 갈리드·가려진 명단을 조회합니다."""
    return panel_observations(session,limit,0x36,0x3D,'clan_info_request',
                              ('mode','grade','level','galrid','nameBytes','masterBytes','roster'))


@server.tool(annotations=READ_ONLY)
def pet_slot_observations(session: str = "latest") -> dict[str, Any]:
    """C85 펫 슬롯 요청과 S82 슬롯 제거를 조회합니다. 펫 이름은 수집하지 않습니다."""
    path = session_events(session)
    requests, removals = Counter(), Counter()
    for row in recorded_rows(path):
        if row.get('action') == 'pet_slot_request':
            requests[row['slot'], row['petAction']] += 1
        if row.get('opcode') != 0x82 or not row.get('raw'):
            continue
        try:
            raw = sanitized_packet(bytes.fromhex(row['raw']))
            if raw is None or raw[0] != 0x82:
                continue
            packet = decode(raw)
        except (IndexError, ValueError):
            continue
        if packet['kind'] == 'pet_slot_remove':
            removals[packet['slot'], len(raw)] += 1
    return {
        'session': path.parent.name,
        'requests': [dict(slot=slot, action=action, observations=n) for (slot, action), n in sorted(requests.items())],
        'removals': [dict(slot=slot, length=length, observations=n) for (slot, length), n in sorted(removals.items())],
    }


@server.tool(annotations=READ_ONLY)
def combat_observations(session: str = "latest") -> dict[str, Any]:
    """본섭 기록에서 전투 모드, AC·MR, 피격 전후 체력 차이, 장비, 내구력과 콤보를 조회합니다."""
    path = session_events(session)
    totals, equipment, durability, combos, hits, sightings, player_effects, combo_values = (Counter() for _ in range(8))
    combo_wire_shapes = Counter()
    combat_requests, combat_modes = Counter(), Counter()
    visible_peers, appearance_modes = {}, deque(maxlen=24)
    mode_sequences = deque(maxlen=48)
    pending_mode = None
    visibility_scope = None
    self_ids, monsters, statuses, attacks = {}, {}, {}, {}
    equipped_weapons, pending_melee, auxiliary_status, effect_status = {}, {}, {}, {}
    melee_samples = {}
    previous_melee = None
    weapon_effects, melee_requests, matched_melee = Counter(), Counter(), Counter()
    errors = 0
    for row in recorded_rows(path):
        epoch = row.get("epoch")
        map_id = row.get("map")
        location = epoch, map_id
        if visibility_scope != location or row.get("kind") == "context_reset":
            visible_peers.clear()
            visibility_scope = location
            pending_mode = previous_melee = None
            pending_melee.pop(epoch, None)
        if row.get("kind") == "context_reset":
            effect_status.pop(epoch, None)
            auxiliary_status.pop(location, None)
        if row.get("kind") == "context_snapshot" and row.get("selfId"):
            self_ids[epoch] = row["selfId"]
        op = row.get("opcode")
        if row.get('kind') == 'client_action':
            if op == 0x51 and row.get('action') == 'combat_mode_request':
                combat_requests[row['mode']] += 1
                pending_mode = (datetime.datetime.fromisoformat(row['time']),
                                dict(map=map_id,request=row.get('packetId'),mode=row['mode']))
            elif pending_mode and op in (0x13,0x33) and 'following_input' not in pending_mode[1]:
                elapsed = (datetime.datetime.fromisoformat(row['time'])-pending_mode[0]).total_seconds()
                if 0 <= elapsed <= .75:
                    pending_mode[1]['following_input'] = dict(packet=row.get('packetId'),opcode=op,
                                                              after_reply='reply' in pending_mode[1])
        if row.get('kind') == 'client_action' and op == 0x13 and row.get('length') == 3 and 'attackMode' in row and 'attackParam' in row:
            mode = row['attackMode'],row['attackParam']
            melee_requests[mode] += 1
            if epoch in pending_melee:
                previous_melee = None
            if row.get('recentActionId'):
                pending_melee[epoch] = (row['recentActionId'], mode, location, row.get('packetId'),
                                        auxiliary_status.get(location), effect_status.get(epoch))
            else:
                pending_melee.pop(epoch, None)
        if op not in (5, 7, 8, 0x0E, 0x0F, 0x13, 0x27, 0x33, 0x37, 0x38, 0x85) or not row.get("raw"):
            continue
        try:
            raw = bytes.fromhex(row["raw"])
            if not raw or raw[0] != op:
                raise ValueError("invalid recorded opcode")
            event = decode(raw)
        except (IndexError, ValueError):
            errors += 1
            continue
        if op == 5:
            self_ids[epoch] = event["oid"]
        if op == 0x33 and epoch in self_ids and event['oid'] != self_ids[epoch] and row.get('packetId'):
            visible_peers.setdefault(event['oid'], (row['packetId'], row['time']))
        if op == 0x0E:
            visible_peers.pop(event['oid'], None)
        if op == 0x27:
            is_self = event['oid'] == self_ids.get(epoch)
            combat_modes[event['mode'], is_self, len(raw)] += 1
            if is_self and pending_mode and pending_mode[1]['mode']==event['mode'] and 'reply' not in pending_mode[1]:
                elapsed = (datetime.datetime.fromisoformat(row['time'])-pending_mode[0]).total_seconds()
                if 0 <= elapsed <= .5:
                    pending_mode[1]['reply'] = dict(packet=row.get('packetId'),length=len(raw),
                                                    padding=event['trailingBytes'],delay_ms=round(elapsed*1000))
                    mode_sequences.append(pending_mode[1])
            if row.get('packetId') and (first := visible_peers.get(event['oid'])):
                stream, sequence = row['packetId'].split(':')
                first_stream, first_sequence = first[0].split(':')
                elapsed = datetime.datetime.fromisoformat(row['time']) - datetime.datetime.fromisoformat(first[1])
                if stream == first_stream and 0 < int(sequence)-int(first_sequence) <= 5 and 0 <= elapsed.total_seconds() <= .25:
                    appearance_modes.append(dict(map=map_id, oid=event['oid'], appearance_packet=first[0],
                                                 mode_packet=row['packetId'], mode=event['mode'], length=len(raw)))
        if op == 0x37 and event['slot'] == 1:
            equipped_weapons[epoch] = event['name'],event['wireSprite']
            previous_melee = None
        if op == 0x38 and event['slot'] == 1:
            equipped_weapons.pop(epoch,None)
            previous_melee = None
        if op == 7:
            for obj in event["objects"]:
                if obj["category"] == 0:
                    monsters[location, obj["oid"]] = obj["sprite"]
                    sightings[map_id, obj['name'], obj['sprite'], obj['x'], obj['y'], obj['floor']] += 1
        if op == 0x13 and event["target"] == self_ids.get(epoch):
            sprite = monsters.get((location, event["attacker"]))
            if sprite is not None:
                count = attacks[location][0] + 1 if location in attacks else 1
                attacks[location] = count, sprite, event["effect"], row["time"]
        if op == 0x13 and event["attacker"] == self_ids.get(epoch) and "displayedDamage" in event:
            player_effects[event["motionId"], event["swingSoundId"], event["displayedDamage"], event["impactSoundId"]] += 1
            if event['effect'] == 1 and event['animation'] == 0:
                if (request := pending_melee.get(epoch)) and request[0] == row.get('recentActionId') and request[2] == location:
                    _, mode, _, packet, status, effect_flags = pending_melee.pop(epoch)
                    matched_melee[mode] += 1
                    weapon_name, weapon_sprite = equipped_weapons.get(epoch, (None, None))
                    status_packet, auxiliary = status or (None, None)
                    flag_packet, flags = effect_flags or (None, None)
                    sample = dict(map=map_id, request=packet, response=row.get('packetId'), target=event['target'],
                                  weapon=weapon_name, weapon_sprite=weapon_sprite, displayed_damage=event['displayedDamage'],
                                  gauge_packet=status_packet, gauge_before=auxiliary,
                                  effect_flags_packet=flag_packet, effect_flags_before=flags)
                    # The normal client can send mode 5 with or without the highest timing grade.
                    if 1 <= mode[0] <= 4:
                        sample['timing_grade'] = mode[0]
                    if previous_melee is not None:
                        previous_mode, previous = previous_melee
                        if previous['target'] == sample['target'] and previous['weapon_sprite'] == weapon_sprite:
                            fields = ('request', 'response', 'displayed_damage')
                            if 'timing_grade' in sample and previous_mode == (5, 0):
                                sample['mode5_before'] = {field: previous[field] for field in fields}
                            if 'timing_grade' in previous and mode == (5, 0):
                                previous['mode5_after'] = {field: sample[field] for field in fields}
                    previous_melee = mode, sample
                    melee_samples.setdefault(mode, deque(maxlen=3)).append(sample)
                if weapon := equipped_weapons.get(epoch):
                    name,sprite = weapon
                    weapon_effects[name,sprite,event['motionId'],event['swingSoundId'],event['impactSoundId']] += 1
        if op == 8:
            previous_hp, previous_defense = statuses.get(location, (None, None))
            defense = previous_defense
            if "physicalDefense" in event:
                defense = event["physicalDefense"], event["magicDefense"]
                totals[defense] += 1
            if "extraDword" in event:
                effect_status[epoch] = row.get('packetId'), event['extraDword']
            current = event.get("currentValues", [])
            if len(current) >= 4:
                auxiliary_status[location] = row.get('packetId'), current[3]
            hp = current[0] if current else None
            if hp is not None:
                attack = attacks.pop(location, None)
                if attack and attack[0] == 1 and defense and defense == previous_defense and previous_hp is not None and hp < previous_hp:
                    elapsed = datetime.datetime.fromisoformat(row["time"]) - datetime.datetime.fromisoformat(attack[3])
                    if 0 <= elapsed.total_seconds() <= .75:
                        hits[map_id, attack[1], *defense, previous_hp - hp, attack[2]] += 1
            statuses[location] = (previous_hp if hp is None else hp, defense)
        if op == 0x85:
            combos[event["count"]] += 1
            combo_values[event["count"], event["bonusCounter"], event["skillLevel"], event["monsterLevel"], event["secondsLeft"]] += 1
            combo_wire_shapes[len(raw),event['levelBonus'],event['secondsLimit'],event.get('trailingBytes','')] += 1
        if op == 0x0F and event["byte1"] != 0 and "durabilityPercent" in event:
            durability[event["name"], event["durabilityPercent"], event["byte1"]] += 1
        for field in event.get("statFields", []):
            if field["name"] not in ("AC", "MR") or field["condition"] not in ("증가", "감소"):
                continue
            value = field["base"] + field["modifier"]
            if field["condition"] == "감소":
                value = -abs(value)
            equipment[event["name"], field["name"], value] += 1
    return {
        "session": path.parent.name,
        "combat_mode_requests": dict(sorted(combat_requests.items())),
        "self_combat_mode_sequences": list(mode_sequences),
        "combat_modes": [dict(mode=mode, self_actor=self_actor, length=length, observations=n)
                         for (mode, self_actor, length), n in sorted(combat_modes.items())],
        "peer_appearance_modes": list(appearance_modes),
        "defense_totals": [{"physical_defense": ac, "magic_defense": mr, "observations": n}
                           for (ac, mr), n in totals.most_common(16)],
        "observed_hp_loss": [{"map": map_id, "monster_sprite": sprite, "physical_defense": ac,
                              "magic_defense": mr, "hp_lost": hp, "effect": effect, "observations": n}
                             for (map_id, sprite, ac, mr, hp, effect), n in hits.most_common(12)],
        "gear_defense": [{"item": item, "stat": stat, "value": value, "observations": n}
                         for (item, stat, value), n in equipment.most_common(24)],
        "weapon_durability": [{"item": item, "percent": pct, "observations": n}
                              for (item, pct, slot), n in sorted(durability.items(), key=lambda row: (row[0][2]!=1,row[0][1],row[0][0])) if slot==1][:24],
        "armor_durability": [{"item": item, "percent": pct, "slot": slot, "observations": n}
                             for (item, pct, slot), n in durability.most_common() if slot!=1][:24],
        "combo_counts": dict(sorted(combos.items())),
        "combo_values": [dict(count=count, bonus_counter=bonus, skill_level=skill, monster_level=level, seconds_left=seconds, observations=n)
                         for (count, bonus, skill, level, seconds), n in sorted(combo_values.items())],
        "combo_wire_shapes": [dict(length=length, level_bonus=bonus, seconds_limit=seconds, trailing_hex=tail, observations=n)
                              for (length,bonus,seconds,tail), n in combo_wire_shapes.most_common()],
        "player_attack_effects": [dict(motion=motion, swing_sound=swing, displayed_damage=damage, impact_sound=impact, observations=n)
                                  for (motion, swing, damage, impact), n in player_effects.most_common(30)],
        "weapon_melee_effects": [dict(weapon=name, sprite=sprite, motion=motion, swing_sound=swing, impact_sound=impact, observations=n)
                                 for (name,sprite,motion,swing,impact), n in weapon_effects.most_common(30)],
        "melee_request_modes": [dict(mode=mode, parameter=parameter, requests=n, matched_effects=matched_melee[mode,parameter],
                                     samples=list(melee_samples.get((mode,parameter), ())))
                                for (mode,parameter), n in melee_requests.most_common(24)],
        "monster_positions": [dict(map=map_id, name=name, sprite=sprite, x=x, y=y, floor=floor, observations=count)
                              for (map_id,name,sprite,x,y,floor), count in sightings.most_common(30)],
        "decode_errors": errors,
    }


@server.tool(annotations=READ_ONLY)
def attack_timing_observations(session: str = "latest", weapon: str | None = None,
                               agility: int | None = None,
                               limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """자기 근접 C13·S13의 프레임, 장비와 어빌, 다음 공격 요청까지의 간격을 조회합니다."""
    path = session_events(session)
    scope = player = equipped = current_agility = None
    pending = previous = None
    samples, strikes = deque(maxlen=limit), deque(maxlen=limit)
    mastery = {}
    combat_abilities = {"격투", "검", "대검", "단검", "도끼", "도", "대도", "창, 특수창", "봉, 해머", "현금"}
    for row in recorded_rows(path):
        kind = row["kind"]
        location = row.get("epoch"), row.get("map")
        if scope != location or kind == "context_reset":
            if scope is None or scope[0] != location[0] or kind == "context_reset":
                player = equipped = current_agility = None
                mastery.clear()
            scope = location
            pending = previous = None
        if kind in ("self_identity", "context_snapshot"):
            identity = row.get("oid" if kind == "self_identity" else "selfId")
            if player is not None and identity != player:
                equipped = current_agility = pending = previous = None
                mastery.clear()
            player = identity
        elif kind == "status" and len(words := row.get("baseWords", [])) >= 5:
            if current_agility != words[4]:
                current_agility = words[4]
                pending = previous = None
        elif kind in ("equipment_add", "equipment_remove") and row.get("slot") == 1:
            equipped = (row["name"], row["wireSprite"]) if kind == "equipment_add" else ("맨손", 0)
            pending = previous = None
        elif kind == "human" and player is not None and row.get("oid") == player and (layers := row.get("equipment")):
            if layers[0] == 0 and equipped != ("맨손", 0):
                equipped = ("맨손", 0)
                pending = previous = None
            elif layers[0] != 0 and equipped == ("맨손", 0):
                equipped = None
                pending = previous = None
        elif kind == "ability":
            if row.get("mode") == 3:
                mastery.clear()
            for entry in row.get("entries", []):
                if entry["name"] in combat_abilities and len(words := entry.get("headerWords", [])) == 2:
                    mastery[entry["name"]] = dict(value=words[1], grade=entry.get("flags"))
        elif kind == "client_action" and row.get("opcode") == 0x13:
            if pending is not None or not row.get("recentActionId"):
                previous = None
            pending = row if row.get("recentActionId") else None
        elif kind == "damage_effect" and player is not None and row.get("attacker") == player:
            if (pending is None or pending["recentActionId"] != row.get("recentActionId") or
                    (row["animation"], row["effect"]) != (0, 1) or not row.get("words")):
                pending = previous = None
                continue
            frames = row["words"][0]
            if not 1 <= frames <= 255:
                pending = previous = None
                continue
            sent_at = observed_time(pending)
            name, sprite = equipped or (None, None)
            if not 0 <= observed_time(row) - sent_at <= .75:
                pending = previous = None
                continue
            if weapon in (None, name) and agility in (None, current_agility):
                strikes.append(dict(map=scope[1], weapon=name, sprite=sprite, agility=current_agility,
                                    mastery=dict(mastery), request=pending.get("packetId"), response=row.get("packetId"),
                                    frames=frames, client_delay_ms=frames * 1000 // 6,
                                    damage=row["extraWords"][1] if len(row.get("extraWords", [])) == 3 else None))
            if previous is not None and weapon in (None, name) and agility in (None, current_agility):
                sent_before, frames_before, request_before, response_before = previous
                interval = round((sent_at - sent_before) * 1000)
                if 0 < interval <= 10000:
                    samples.append(dict(map=scope[1], weapon=name, sprite=sprite, agility=current_agility,
                                        previous_request=request_before, previous_response=response_before,
                                        request=pending.get("packetId"), response=row.get("packetId"),
                                        previous_frames=frames_before, client_delay_ms=frames_before * 1000 // 6,
                                        request_interval_ms=interval, frames=frames))
            previous = sent_at, frames, pending.get("packetId"), row.get("packetId")
            pending = None
    return dict(session=path.parent.name, observations=list(samples), strikes=list(strikes))


@server.tool(annotations=READ_ONLY)
def weapon_ability_observations(session: str = "latest", weapon: str | None = None,
                                limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """같은 조작에 기록된 S2C 어빌리티 등록·필요 카운트와 S37 무기 장착을 조회합니다."""
    path = session_events(session)
    pending, progress, registration, seen, samples = {}, {}, {}, set(), deque(maxlen=limit)
    scope = request = notice = None
    for row in recorded_rows(path):
        action = row.get('epoch'), row.get('recentActionId')
        if action != scope:
            scope = action
            pending.clear()
            progress.clear()
            registration.clear()
            seen.clear()
            request = notice = None
        if action[1] is None:
            continue
        kind = row['kind']
        if kind == 'client_action' and row.get('opcode') == 0x1C:
            request = row.get('packetId')
        elif kind == 'frame_header' and row.get('direction') == 'S' and row.get('opcode') == 0x0A:
            notice = dict(packet=row.get('packetId'), length=row.get('plainLength'), mode=row.get('messageMode'))
        elif kind == 'ability' and row.get('mode') == 1:
            for entry in row['entries']:
                pending[entry['slot']] = entry,row.get('packetId')
                registration.setdefault(entry['slot'], []).append(dict(mode=1, packet=row.get('packetId'),
                    length=row.get('length'), mastery=entry['headerWords'][1]))
        elif kind == 'ability' and row.get('mode') == 2 and row['slot'] in pending:
            progress[row['slot']] = row
            registration[row['slot']].append(dict(mode=2, packet=row.get('packetId'),
                length=row.get('length'), count=row.get('value'), reference=row.get('referenceWord')))
        elif kind == 'equipment_add' and row['slot'] == 1 and weapon in (None,row['name']):
            when = datetime.datetime.fromisoformat(row['time'])
            for slot, meter in progress.items():
                entry, packet = pending[slot]
                key = row['wireSprite'],entry['name'],slot,meter['referenceWord']
                if key in seen or not 0 <= (when-datetime.datetime.fromisoformat(meter['time'])).total_seconds() <= .25:
                    continue
                seen.add(key)
                samples.append(dict(weapon=row['name'], sprite=row['wireSprite'], ability=entry['name'], slot=slot,
                    header_word=entry['headerWords'][0], icon=entry['value'], reference=meter['referenceWord'],
                    ability_packet=packet, progress_packet=meter.get('packetId'), equipment_packet=row.get('packetId'),
                    request_packet=request, preceding_notice=notice, registration=list(registration[slot])))
    return dict(session=path.parent.name, observations=list(samples))


@server.tool(annotations=READ_ONLY)
def item_option_observations(session: str = "latest", name: str | None = None,
                             limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """상점·소지품·장착 옵션과 외형을 대조하고 이름을 지정하면 클라이언트 기본값을 함께 조회합니다."""
    path = session_events(session)
    options, appearances, weapons = deque(maxlen=limit), deque(maxlen=limit), deque(maxlen=limit)
    variants = {}
    identities = {}
    equipment_appearances = EquipmentAppearances()

    def record(row, item_name, sprite, description):
        if name is not None and name not in item_name:
            return
        key = item_name,sprite
        if name and len(identities)<limit:
            identities.setdefault(key,dict(name=item_name,sprite=sprite,packet=row.get('packetId'),source=row['kind']))
        if not description or not description.strip():
            return
        variants.setdefault(key,set()).add(description)
        options.append(dict(name=item_name,sprite=sprite,packet=row.get("packetId"),source=row["kind"],
                            description=description,fields=item_stats(description)))

    for row in recorded_rows(path):
        kind = row["kind"]
        if kind in ("inventory_add","equipment_add"):
            record(row,row["name"],row["wireSprite"],row.get("description"))
        elif kind=="merchant_dialog" and row.get("mode")==10:
            for item in row.get("items",[]):
                record(row,item["name"],item["sprite"],item.get("description"))
        if match := equipment_appearances.ingest(row):
            item, layer, word = match
            if name is None or name in item['name']:
                sample = dict(name=item['name'], sprite=item['wireSprite'], slot=item['slot'], appearance_layer=layer,
                              appearance_word=word, model_assets=equipment_layer_assets(layer, word, row['equipment'][1]),
                              equipment_word1=item['word1'], equipment_word2=item['word2'],
                              equipped_weight=item['word1'], item_weight=item['word2'],
                              equipment_packet=item.get('packetId'), appearance_packet=row.get('packetId'))
                appearances.append(sample)
                if item['slot'] == 1:
                    weapons.append(sample)
    metadata = []
    if name:
        try:
            blocks = {}
            metadata_files = [ROOT/'evidence'/'original-Meta.dat']
            overlay = ROOT/'data'/'recorded-meta.dat'
            if overlay.exists():
                metadata_files.append(overlay)
            for metadata_file in metadata_files:
                for section,_,data in metadata_blocks(metadata_file.read_bytes()):
                    if metadata_file == overlay and section not in blocks:
                        raise ValueError(f'Unknown metadata overlay entry: {section}')
                    blocks[section] = data,metadata_file.name
            for section,(data,source) in blocks.items():
                equipment = re.fullmatch(r'CharicInfo\d+',section) is not None
                item_data = re.fullmatch(r'ItemInfo\d+',section) is not None
                if not (equipment or item_data or section=='ItemPreview'):
                    continue
                reader=Reader(data)
                reader.p=0
                for _ in range(reader.u16()):
                    key=reader.string()
                    values=[reader.string16() for _ in range(reader.u16())]
                    if section=='ItemPreview':
                        if len(values)<3 or name not in values[1] or values[0]!='10' or not values[2].isdigit():
                            continue
                        word=int(values[2])
                        metadata.append(dict(name=values[1],section=section,key=key,metadata_source=source,appearance_word=word,
                                             model_assets=equipment_layer_assets(0,word)))
                    elif name in key:
                        row=dict(name=key,section=section,metadata_source=source)
                        if equipment and len(values)>=21 and values[0].isdigit() and values[4].isdigit():
                            row.update(weight=int(values[0]),maximum_durability=int(values[4]))
                            if damage:=re.fullmatch(r'(\d+)m(\d+)',values[20]):
                                row['attack_range']=[int(part) for part in damage.groups()]
                        else:
                            row['fields']=values
                        metadata.append(row)
                    if len(metadata)>=limit:
                        break
                if len(metadata)>=limit:
                    break
        except (OSError,ValueError,zlib.error) as error:
            raise ToolError("클라이언트 아이템 메타데이터를 읽을 수 없습니다.") from error
    imported = read_json(LIVE).get('Items',[]) if LIVE.is_file() else []
    imported_variants = [dict(name=item['Name'],sprite=item['Sprite'],slot=item['EquipmentSlot'],catalog_key=item['Key'],
                              base_key=item['VariantOf'],packet=item.get('OptionPacket'),fields=item_stats(item['StatDescription']))
                         for item in imported if item.get('VariantOf') and (name is None or name in item['Name'])]
    return dict(session=path.parent.name,option_samples=list(options),weapon_appearances=list(weapons),
        equipment_appearances=list(appearances),client_metadata=metadata,recorded_item_identities=list(identities.values()),
        imported_option_variants=imported_variants[:limit],
        different_descriptions=[dict(name=item_name,sprite=sprite,variants=len(texts))
                                for (item_name,sprite),texts in variants.items() if len(texts)>1][:limit])


@server.tool(annotations=READ_ONLY)
def initial_inventory_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=20)] = 5) -> dict[str, Any]:
    """접속 초기화 순서와 은행 슬롯, 외형, 친구 목록, S5B 및 최초 맵 검사 응답을 대조합니다."""
    path = session_events(session)
    observations = deque(maxlen=limit)
    transfer = None
    arrival = None
    followup = None
    positioned = False
    arrival_status = False
    packets = []
    bank_header = None
    entry_check = None

    def status_fields(row):
        return dict(packet=row.get('packetId'),opcode=0x08,length=row.get('length'),
            mask=row.get('mask'),state=row.get('state'),base_bytes=row.get('baseBytes'),maximum_values=row.get('maximumValues'),
            current_values=row.get('currentValues'),gold=row.get('gold'),
            physical_defense=row.get('physicalDefense'),magic_defense=row.get('magicDefense'),
            effect_flags=row.get('extraDword'),movement_percent=row.get('extraWord'))

    for row in recorded_rows(path):
        kind = row['kind']
        if entry_check is not None and kind in ('frame_header','status'):
            started, observation, request_at = entry_check
            when = observed_time(row)
            if request_at is None:
                if not 0 <= when-started <= 2:
                    entry_check = None
                elif kind=='frame_header' and row.get('direction')=='C' and row.get('opcode')==0x35:
                    observation['entry_map_check'] = dict(request=row.get('packetId'),map=row.get('map'),statuses=[])
                    entry_check = started, observation, when
            elif not 0 <= when-request_at <= 1.25:
                entry_check = None
            elif kind=='status':
                observation['entry_map_check']['statuses'].append(status_fields(row))
            elif row.get('direction')=='S' and row.get('opcode')==0x14:
                observation['entry_map_check']['acknowledgement'] = dict(packet=row.get('packetId'),length=row.get('plainLength'))
                entry_check = None
        if kind == 'context_reset':
            transfer = row.get('packetId') if row.get('reason') == 'server_transfer' else None
            packets = []
            arrival = None
            followup = None
            positioned = False
            arrival_status = False
            bank_header = None
            entry_check = None
        elif transfer and kind == 'self_identity':
            arrival = []
            observations.append(dict(transfer_packet=transfer, identity_packet=row.get('packetId'), packets=packets,
                                     arrival=arrival, followup=[]))
            entry_check = observed_time(row), observations[-1], None
            transfer = None
        elif followup is not None:
            if kind=='client_action' or kind=='frame_header' and row.get('direction')=='C':
                followup = None
            elif kind=='frame_header' and row.get('direction')=='S' and row.get('opcode') in (0x3A,0x6A,0x5B):
                followup.append(dict(packet=row.get('packetId'),opcode=row['opcode'],length=row.get('plainLength')))
            elif followup and followup[-1]['packet']==row.get('packetId'):
                if kind=='friend_list' and row.get('mode')==0:
                    followup[-1].update(friend_list_fields(row))
                elif kind=='advertise_info':
                    followup[-1].update(text_bytes=row['textBytes'],words=row['words'],value=row['value'],
                                        padding=row['trailingBytes'])
                elif kind=='timed_spell':
                    followup[-1].update(timer_id=row['id'],name=row['name'],duration=row['duration'])
        elif arrival is not None and positioned and kind == 'client_action':
            arrival = None
        elif arrival is not None and positioned and kind == 'human':
            is_self = row.get('isSelf',False)
            arrival.append(dict(packet=row.get('packetId'),opcode=0x33,kind=kind,length=row.get('length'),
                                object_id=row.get('oid'),is_self=is_self))
            if is_self and arrival_status:
                arrival = None
                followup = observations[-1]['followup']
        elif arrival is not None and arrival_status and kind == 'frame_header' and row.get('direction')=='S' and row.get('opcode')==0x58:
            arrival.append(dict(packet=row.get('packetId'),opcode=0x58,kind='frame_header',length=row.get('plainLength')))
        elif (arrival and arrival_status and kind == 's58_probe' and arrival[-1]['opcode']==0x58 and
              arrival[-1]['packet']==row.get('packetId')):
            arrival[-1]['opaque_bytes'] = row.get('opaqueBytes')
        elif arrival is not None and kind == 'sound' and row.get('bgm'):
            arrival.append(dict(packet=row.get('packetId'),opcode=0x19,kind=kind,length=row.get('length'),track=row.get('id')))
        elif arrival is not None and kind in ('map','map_check','job_list','self_position','objects','weather','status'):
            if kind == 'status':
                arrival.append(status_fields(row))
                arrival_status |= positioned
            else:
                packet = dict(packet=row.get('packetId'),opcode=row.get('opcode'),kind=kind,length=row.get('length'))
                if kind == 'map' and row.get('raw'):
                    raw = bytes.fromhex(row['raw'])
                    if len(raw)>=10 and len(raw)>=10+raw[9]:
                        packet.update(map_id=row.get('map'),padding=raw[10+raw[9]:].hex())
                arrival.append(packet)
                positioned |= kind=='self_position'
        elif transfer and kind == 'frame_header' and row.get('direction')=='S' and row.get('opcode') in (0x1E,0x20,0x39):
            packets.append(dict(packet=row.get('packetId'),opcode=row['opcode'],length=row.get('plainLength')))
        elif transfer and kind == 'self_look' and packets and packets[-1]['packet']==row.get('packetId'):
            packets[-1].update(equipment_durability=row.get('bytePairs'),level=row.get('characterPanelLevel'),
                               experience=row.get('characterPanelExperience'))
        elif transfer and kind == 'frame_header' and row.get('direction')=='S' and row.get('opcode')==0x61:
            bank_header = row
        elif transfer and kind == 'bank_slot' and row.get('group')==2 and row.get('mode')==1:
            packet_id = row.get('packetId')
            length = bank_header.get('plainLength') if bank_header and bank_header.get('packetId')==packet_id else None
            packet = dict(packet=packet_id,opcode=0x61,group=2,mode=1,length=length,slot=row.get('slot'),
                          sprite=row.get('wireSprite'))
            packet.update({key:row[key] for key in ('quantity','word1','word2','stackFlag','durabilityByte','remainingMinutes') if key in row})
            packets.append(packet)
        elif transfer and kind in ('spell','ability') and row.get('mode')==3:
            packets.append(dict(packet=row.get('packetId'),opcode=row.get('opcode'),length=row.get('length'),mode=3))
        elif transfer and kind == 'status':
            packets.append(status_fields(row))
        elif transfer and kind in ('equipment_add','inventory_add') and {'slot','wireSprite','word1','word2'} <= row.keys():
            packet = dict(packet=row.get('packetId'), opcode=row.get('opcode'), slot=row['slot'],
                          sprite=row['wireSprite'], padding=row.get('trailingBytes'),
                          item_flag=row.get('flag' if kind=='equipment_add' else 'flag2'))
            if kind == 'equipment_add':
                packet.update(equipped_weight=row['word1'],item_weight=row['word2'])
            else:
                packet.update(position=[row['word1'],row['word2']],quantity=row.get('quantity'),
                              carried_weight=row.get('word3'),item_weight=row.get('word4'))
            packets.append(packet)
    return dict(session=path.parent.name,observations=list(observations))


def equipment_use_responses(path):
    action = None
    responses = []
    positions = {}
    epoch = None
    fields = {'status':{'mask'}, 'equipment_remove':{'slot','refreshWord'},
              'equipment_add':{'slot','name','word1','word2'}, 'inventory_add':{'slot','name','word1','word2','word3'},
              'inventory_remove':{'slot'}, 'human':set()}
    for row in recorded_rows(path):
        kind = row['kind']
        if epoch!=row.get('epoch') or kind=='context_reset':
            positions.clear()
            epoch=row.get('epoch')
        if kind in ('client_action','context_reset'):
            if action is not None:
                yield action, responses
            responses = []
            action = dict(row,bag_positions_before=positions.copy()) if (
                kind=='client_action' and row.get('opcode')==0x1C and row.get('slot') and row.get('recentActionId')) else None
        elif (action is not None and kind in fields and fields[kind] <= row.keys() and {'packetId','opcode'} <= row.keys() and
              row.get('epoch')==action.get('epoch') and
              row.get('recentActionId')==action['recentActionId'] and
              0 <= (datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(action['time'])).total_seconds() <= .25):
            responses.append(row)
        if kind=='inventory_add' and 'word1' in row and 'word2' in row:
            positions[row['slot']] = [row['word1'],row['word2']]
        elif kind=='inventory_remove' and row.get('byte2') not in (9,12):
            positions.pop(row['slot'],None)
    if action is not None:
        yield action, responses


@server.tool(annotations=READ_ONLY)
def equipment_swap_observations(session: str = "latest", slot: Annotated[int, Field(ge=1, le=255)] | None = None,
                                limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """장비 교체 C1C와 같은 행동의 S38/S37, 가방 위치, 상태 갱신 순서를 대조합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    for action, responses in equipment_use_responses(path):
        removed = next((r for r in responses if r['kind']=='equipment_remove' and (slot is None or r['slot']==slot)),None)
        if removed is None:
            continue
        after = responses[responses.index(removed)+1:]
        added = next((r for r in after if r['kind']=='equipment_add' and r['slot']==removed['slot']),None)
        if added is None:
            continue
        added_index = after.index(added)
        returns = [r for r in after[:added_index] if r['kind']=='inventory_add']
        if not returns:
            continue
        outgoing = returns[-1]
        source = action['slot']
        last = next((r for r in reversed(after[added_index+1:]) if r.get('slot')==source and
                     r['kind'] in ('inventory_add','inventory_remove')),None)
        results.append(dict(request=action['packetId'],equipment_slot=removed['slot'],source_slot=source,
            returned_slot=outgoing['slot'],returned_item=outgoing['name'],equipped_item=added['name'],
            removal_packet=removed['packetId'],equipment_packet=added['packetId'],
            remaining_weight=removed['refreshWord'],equipped_weight=added['word1'],item_weight=added['word2'],
            carried_weight=outgoing['word3'],
            returned_positions=[dict(packet=r['packetId'],x=r['word1'],y=r['word2']) for r in returns if r['slot']==outgoing['slot']],
            previous_returned_position=action['bag_positions_before'].get(outgoing['slot']),
            source_update=None if last is None else dict(packet=last['packetId'],opcode=last['opcode'],
                **({'position':[last['word1'],last['word2']]} if last['kind']=='inventory_add' else {})),
            status=[dict(packet=r['packetId'],mask=r['mask'],
                         **{key:r[key] for key in ('baseWords','physicalDefense','magicDefense','currentValues') if key in r})
                    for r in responses if r['kind']=='status'],
            sequence=[dict(packet=r['packetId'],opcode=r['opcode']) for r in responses]))

    return dict(session=path.parent.name,observations=list(results))


@server.tool(annotations=READ_ONLY)
def equipment_wear_observations(session: str = "latest", slot: Annotated[int, Field(ge=1, le=255)] | None = None,
                                limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """빈 슬롯에 착용하는 C1C의 장비·외형 갱신, 가방 차감과 상태 델타를 대조합니다."""
    def frame(row):
        padding = row.get('trailingBytes')
        if padding is None and row.get('raw') and row['opcode'] in (8,0x33):
            wire = bytes.fromhex(row['raw'])
            if row['opcode']==8:
                end = 3+sum(size for bit,size in ((0x40,46),(0x20,16),(0x10,4),(8,12)) if row['mask']&bit)
            else:
                # S33 ends with a length-prefixed name and its terminator.
                end = 41+wire[39] if len(wire)>=40 else len(wire)+1
            if end<=len(wire)==row.get('length'):
                padding = wire[end:].hex()
        return dict(packet=row['packetId'],opcode=row['opcode'],length=row.get('length'),padding=padding)

    path = session_events(session)
    results = deque(maxlen=limit)
    for action, responses in equipment_use_responses(path):
        if any(r['kind'] in ('equipment_remove','inventory_add') for r in responses):
            continue
        for index in range(len(responses)-5):
            sequence = responses[index:index+6]
            if [r['opcode'] for r in sequence] != [0x37,0x33,0x37,0x33,0x10,8]:
                continue
            first, appearance, second, refreshed, removal, status = sequence
            if (slot not in (None,first['slot']) or first['slot']!=second['slot'] or first['name']!=second['name'] or
                not appearance.get('isSelf') or not refreshed.get('isSelf') or
                removal['slot']!=action['slot'] or removal.get('byte2')!=0x0C):
                continue
            results.append(dict(request=action['packetId'],equipment_slot=first['slot'],source_slot=action['slot'],
                item=first['name'],sprite=first.get('wireSprite'),item_weight=first['word2'],carried_weight=removal.get('word'),
                equipment_weights=[first['word1'],second['word1']],item_flags=[first.get('flag'),second.get('flag')],
                status_mask=status['mask'],status_state=status.get('state'),
                status_maximum_values=status.get('maximumValues'),status_base_words=status.get('baseWords'),
                physical_defense=status.get('physicalDefense'),magic_defense=status.get('magicDefense'),
                status_current_values=status.get('currentValues'),status_effect_flags=status.get('extraDword'),
                movement_percent=status.get('extraWord'),
                sequence=[frame(r) for r in sequence]))
            break
    return dict(session=path.parent.name,observations=list(results))


@server.tool(annotations=READ_ONLY)
def fishing_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 24) -> dict[str, Any]:
    """C1C 미끼 직접 사용과 C13 낚시의 미끼 차감·모션·어빌·획득을 대조합니다."""
    path = session_events(session)
    rods, actions, replies, inventory, old_shoes = (deque(maxlen=limit) for _ in range(5))
    old_rings = deque(maxlen=limit)
    old_ring_names = {0x801A:'낡은아이언링', 0x801B:'낡은실버링', 0x801C:'낡은골드링'}
    seen_ground_rings = set()
    casts = deque(maxlen=limit)
    bait_uses = deque(maxlen=limit)
    counter_increases = Counter()
    attempt_counts = Counter()
    unconfirmed_attempts = {}
    same_spot_intervals = []
    cast_count = success_count = 0
    rod = request = cast = player = position = None
    item_use = fishing_slot = fishing_grade = previous_counter_cast = None
    attempt = previous_confirmed = None
    bag_slots = {}

    def fields(row, *names):
        return {name: row[name] for name in ('map', 'packetId', *names) if name in row}

    def finish_attempt():
        nonlocal attempt, previous_confirmed
        if attempt is None:
            return
        result, started = attempt['cast'],attempt['started']
        client_frame, inbound_frames = attempt['client_frame'],attempt['inbound_frames']
        spot = (attempt['epoch'],result['map'],result['position'])
        interval = None
        if (result['position'] is not None and previous_confirmed is not None
                and previous_confirmed[0]==spot and started>=previous_confirmed[1]):
            interval = round((started-previous_confirmed[1])*1000)
        if 'bait_remaining' in result:
            outcome = 'bait_debit_observed'
            if result['catch'] and all('quantity_added' in item for item in result['catch']):
                result['catch_quantity_added'] = sum(item['quantity_added'] for item in result['catch'])
            if interval is not None:
                same_spot_intervals.append(interval)
            previous_confirmed = spot,started
        else:
            if not client_frame:
                outcome = 'client_frame_unobserved'
            elif not inbound_frames:
                outcome = 'inbound_frames_unobserved'
            else:
                outcome = 'inbound_without_bait_debit'
            sample = dict(
                request_packet=result['request_packet'],map=result['map'],time=result['time'],
                client_frame=client_frame,inbound_frames=inbound_frames)
            if interval is not None:
                sample['since_previous_confirmed_ms'] = interval
            unconfirmed_attempts.setdefault(outcome,deque(maxlen=limit)).append(sample)
        attempt_counts[outcome] += 1
        attempt = None

    for row in recorded_rows(path):
        kind = row['kind']
        if kind in ('client_action','context_reset','map') or kind in ('equipment_add','equipment_remove') and row.get('slot')==1:
            finish_attempt()
            if kind!='client_action':
                previous_confirmed = None
        elif attempt is not None and kind=='frame_header':
            result = attempt['cast']
            if ((row.get('epoch'),row.get('map'),row.get('recentActionId'))==(attempt['epoch'],result['map'],attempt['action_id'])
                    and 0<=observed_time(row)-attempt['started']<=2):
                if (row.get('direction')=='C' and row.get('packetId')==result['request_packet']
                        and row.get('opcode')==0x13 and row.get('plainLength')==3
                        and row.get('lengthMatches') and row.get('opcodeMatches')):
                    attempt['client_frame'] = row['packetId']
                elif row.get('direction')=='S':
                    attempt['inbound_frames'] += 1
        if kind == 'ability' and row.get('mode') in (1, 3):
            if row['mode'] == 3:
                fishing_slot = fishing_grade = None
            for entry in row.get('entries', []):
                if entry.get('name') == '낚시':
                    fishing_slot = entry['slot']
                    fishing_grade = entry.get('flags')
                elif entry['slot'] == fishing_slot:
                    fishing_slot = fishing_grade = None
        previous_bag_item = None
        if kind == 'inventory_add':
            previous_bag_item = bag_slots.get(row.get('slot'))
            bag_slots[row.get('slot')] = (row.get('wireSprite'), row.get('name'), row.get('quantity'))
        elif kind == 'inventory_remove':
            bag_slots[row.get('slot')] = (None, None, 0)
        if kind == 'client_action':
            item_use = row if row.get('opcode') == 0x1C else None
        if kind == 'context_reset':
            rod = request = cast = player = position = item_use = fishing_slot = fishing_grade = previous_counter_cast = None
            bag_slots.clear()
        elif kind == 'context_snapshot':
            player, position = row.get('selfId'), row.get('position')
        elif kind == 'self_identity':
            player = row.get('oid')
        elif kind in ('self_move', 'self_position'):
            position = {name: row[name] for name in ('x', 'y', 'floor')}
        elif kind in ('equipment_add', 'equipment_remove') and row.get('slot') == 1:
            rod = request = cast = previous_counter_cast = None
            if kind == 'equipment_add' and any(word in row.get('name', '') for word in ('낚싯대', '낚시대')):
                rod = fields(row, 'name', 'wireSprite')
                rods.append(rod)
        elif kind == 'map':
            request = cast = position = item_use = previous_counter_cast = None
        elif rod and kind == 'client_action':
            cast = None
            if row.get('opcode') in (0x13, 0x1C, 0x21, 0x3E):
                request = row
                actions.append(dict(fields(row, 'opcode', 'length', 'attackMode', 'attackParam', 'slot', 'slotType'),
                                    rod_packet=rod.get('packetId')))
                if (row.get('opcode'), row.get('attackMode'), row.get('attackParam')) == (0x13, 5, 0):
                    cast = dict(request_packet=row.get('packetId'), map=row.get('map'), position=position,
                                time=row['time'], catch=[], catch_updates=[], sequence=[])
                    if fishing_grade is not None:
                        cast['fishing_grade'] = fishing_grade
                    attempt = dict(cast=cast,epoch=row.get('epoch'),started=observed_time(row),
                                   action_id=row.get('recentActionId'),client_frame=None,inbound_frames=0)
            elif row.get('opcode') in (0x07, 0x08, 0x33, 0x39):
                request = None
        elif cast is not None and request and row.get('recentActionId')==request.get('recentActionId') and (
            kind=='status' and row.get('mask')==0 or
            kind=='frame_header' and row.get('direction')=='S' and row.get('opcode')==0x0A
        ):
            cast['sequence'].append(dict(fields(row,'state','length','plainLength','messageMode'),opcode='S08' if kind=='status' else 'S0A'))
        elif kind=='fishing_notice' and cast is not None and row.get('castPacketId')==cast['request_packet']:
            if 'bait_packet' in cast and row.get('baitPacketId')==cast['bait_packet']:
                cast['notice']=fields(row,'text','messageMode','messageBytes','trailingBytes')
        elif kind == 'inventory_add':
            bait = fishing_bait_remaining(row.get('name'),row.get('wireSprite'))
            if bait is not None and item_use and all(row.get(key) == item_use.get(key) for key in ('map', 'slot', 'recentActionId')):
                bait_uses.append(dict(fields(row, 'slot'), request_packet=item_use.get('packetId'),
                                      bait_remaining=bait, rod_equipped=rod is not None))
            is_old_shoe = row.get('wireSprite') == 0x804E and row.get('name') == '낡은신발'
            if is_old_shoe:
                old_shoes.append(dict(fields(row, 'slot', 'quantity', 'wireSprite'), location='inventory'))
            if row.get('wireSprite') in old_ring_names and row.get('name')==old_ring_names[row['wireSprite']]:
                old_rings.append(dict(fields(row, 'slot', 'quantity', 'wireSprite', 'name'), location='inventory'))
            if request and row.get('map') == request.get('map') and row.get('recentActionId') == request.get('recentActionId'):
                inventory.append(dict(fields(row, 'name', 'wireSprite', 'quantity'),
                                      request_packet=request.get('packetId')))
                if cast is not None:
                    if bait is not None:
                        if 'bait_remaining' not in cast:
                            casts.append(cast)
                            cast_count += 1
                        cast.update(bait_remaining=bait, bait_packet=row.get('packetId'), bait_slot=row.get('slot'))
                        cast['sequence'].append(dict(fields(row,'slot','length'),opcode='S0F',role='bait'))
                    elif 'bait_remaining' in cast:
                        catch = fields(row, 'slot', 'name', 'wireSprite', 'quantity')
                        if not cast['catch']:success_count += 1
                        cast['catch_updates'].append(dict(catch,position=[row.get('word1'),row.get('word2')],
                                                          length=row.get('length'),padding=row.get('trailingBytes')))
                        cast['sequence'].append(dict(fields(row,'slot','length'),opcode='S0F',role='catch'))
                        previous = next((i for i, item in enumerate(cast['catch']) if item.get('slot') == row.get('slot')), None)
                        prior_quantity = cast['catch'][previous].get('previous_quantity') if previous is not None else None
                        if previous is None and previous_bag_item is not None:
                            sprite, name, quantity = previous_bag_item
                            if sprite is None or (sprite, name) == (row.get('wireSprite'), row.get('name')):
                                prior_quantity = quantity
                        if isinstance(prior_quantity,int) and isinstance(catch.get('quantity'),int) and catch['quantity']>=prior_quantity:
                            catch.update(previous_quantity=prior_quantity,quantity_added=catch['quantity']-prior_quantity)
                        if previous is None:
                            cast['catch'].append(catch)
                        else:
                            cast['catch'][previous] = catch
        elif kind == 'objects':
            for obj in row.get('objects', []):
                if obj.get('sprite') == 0x804E and obj.get('name') == '낡은신발':
                    old_shoes.append(dict(fields(row), location='ground', x=obj['x'], y=obj['y'], floor=obj['floor']))
                if obj.get('sprite') in old_ring_names and obj.get('name')==old_ring_names[obj['sprite']]:
                    key = row.get('epoch'), row.get('map'), obj.get('oid'), obj['name']
                    if key not in seen_ground_rings:
                        seen_ground_rings.add(key)
                        old_rings.append(dict(fields(row),location='ground',name=obj['name'],sprite=obj['sprite'],
                                              x=obj['x'],y=obj['y'],floor=obj['floor']))
        elif request and kind in ('damage_effect', 'ability', 'sound', 'inventory_remove') and \
                row.get('map') == request.get('map') and row.get('recentActionId') == request.get('recentActionId'):
            replies.append(dict(fields(row, 'kind', 'opcode', 'slot', 'animation', 'effect', 'mode', 'id'),
                                request_packet=request.get('packetId')))
            if cast is None or 'bait_remaining' not in cast:
                continue
            if kind == 'damage_effect' and 'motion_packet' not in cast and row.get('words') == [35, 0, 0, 0] and \
                    row.get('attacker') == row.get('target') and (player is None or row.get('attacker') == player):
                cast['motion_packet'] = row.get('packetId')
                cast['sequence'].append(dict(fields(row,'length'),opcode='S13',frames=35))
            if kind == 'ability':
                value, reference = row.get('value'), row.get('referenceWord')
                if (row.get('mode') == 2 and fishing_slot is not None and row.get('slot') == fishing_slot
                        and isinstance(value,int) and isinstance(reference,int) and 0<=value<reference):
                    if 'counter' not in cast:
                        previous = previous_counter_cast
                        if (previous is not None and previous['ability_slot'] == fishing_slot
                                and previous['counter_reference'] == reference
                                and previous['bait_slot'] == cast['bait_slot']
                                and previous['bait_remaining'] == cast['bait_remaining'] + 1):
                            increase = (value - previous['counter']) % reference
                            cast.update(counter_increase_modulo_reference=increase,
                                        previous_counter_packet=previous['counter_packet'])
                            counter_increases[increase] += 1
                        cast['ability_slot'] = fishing_slot
                        previous_counter_cast = cast
                    cast.update(counter=value, counter_reference=reference, counter_packet=row.get('packetId'))
                    cast['sequence'].append(dict(fields(row,'length','mode','slot','value','referenceWord'),opcode='S2C'))
                for entry in row.get('entries', []):
                    if entry.get('name') == '낚시':
                        cast.update(mastery=entry['headerWords'][1], mastery_packet=row.get('packetId'))
                        cast['sequence'].append(dict(fields(row,'length','mode'),opcode='S2C',
                                                     slot=entry['slot'],mastery=entry['headerWords'][1]))
    finish_attempt()
    intervals = dict(count=len(same_spot_intervals),minimum=min(same_spot_intervals),
                     median=median(same_spot_intervals),maximum=max(same_spot_intervals)) if same_spot_intervals else None
    return dict(session=path.parent.name, rod_equips=list(rods), action_candidates=list(actions),
                response_candidates=list(replies), inventory_candidates=list(inventory),
                old_shoe_observations=list(old_shoes), old_ring_observations=list(old_rings),
                direct_bait_uses=list(bait_uses), confirmed_cast_count=cast_count,
                successful_cast_count=success_count, counter_increases_modulo_reference=dict(counter_increases),
                confirmed_casts=list(casts),attempt_counts=dict(attempt_counts),
                unconfirmed_attempts={kind:list(rows) for kind,rows in unconfirmed_attempts.items()},
                same_spot_request_intervals_ms=intervals)


@server.tool(annotations=READ_ONLY)
def bow_attack_observations(session: str = "all", limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """C14 활 공격 요청과 S13 화살 효과를 연결하고 두 개체의 좌표를 대조합니다."""
    paths = [session_events(name) for name in reversed(recent_sessions())] if session == 'all' else [session_events(session)]
    samples, requests, matches = deque(maxlen=limit), 0, 0

    def tile(row):
        return row['x'], row['y'], row['floor']

    for path in paths:
        context, player, position, request, objects = (None,None), None, None, None, {}
        for row in recorded_rows(path):
            kind, current = row['kind'], (row.get('epoch'),row.get('map'))
            if current != context or kind == 'context_reset':
                if current[0] != context[0] or kind == 'context_reset':
                    player = None
                context, position, request = current, None, None
                objects.clear()
            if kind == 'context_snapshot':
                player = row.get('selfId')
                position = tile(row['position']) if row.get('position') else None
            elif kind == 'self_identity':
                player = row['oid']
            elif kind in ('self_position','self_move'):
                position = tile(row)
            elif kind == 'objects':
                for obj in row['objects']:
                    if obj['category'] == 0:
                        objects[obj['oid']] = tile(obj), obj['sprite']
            elif kind in ('object_remove','death'):
                objects.pop(row['oid'],None)
            elif kind == 'object_move' and row['oid'] in objects and 0 <= row['direction'] < len(DELTAS):
                dx,dy = DELTAS[row['direction']]
                objects[row['oid']] = (row['x']+dx,row['y']+dy,row['floor']), objects[row['oid']][1]
            elif kind == 'client_action' and row.get('opcode') == 0x14 and row.get('length') == 7:
                request = row
                requests += 1
            elif (kind == 'damage_effect' and request and row['attacker'] == player and row['animation'] == 1
                  and row['effect'] == 1 and row['words'][:2] == [14,38]
                  and row.get('recentActionId') == request.get('recentActionId')
                  and request.get('targetId',row['target']) == row['target']):
                target, sprite = objects.get(row['target'],(None,None))
                distance = max(abs(target[0]-position[0]),abs(target[1]-position[1])) if target and position and target[2] == position[2] else None
                matches += 1
                samples.append(dict(session=path.parent.name,map=current[1],request=request.get('packetId'),effect=row.get('packetId'),
                    request_target=request.get('targetId'),effect_target=row['target'],request_heading=request.get('attackParam'),
                    effect_heading=row['extraByte'],position=position,target_position=target,target_sprite=sprite,distance=distance,
                    words=row['words'],damage_words=row.get('extraWords')))
                request = None
    return dict(session=session,requests=requests,matched=matches,observations=list(samples))


@server.tool(annotations=READ_ONLY)
def passive_recovery_observations(session: str = "latest",
                                  limit: Annotated[int, Field(ge=1, le=50)] = 24) -> dict[str, Any]:
    """S08의 자연 회복, 포만감 감소, 수치가 없는 주기 응답 후보를 조회합니다."""
    path = session_events(session)
    previous = last_manual = maximum = last_action = None
    samples, empty_satiety, hungry_mana, decay_samples, idle_status = (deque(maxlen=limit) for _ in range(5))
    combined_samples, pending_combined = deque(maxlen=limit), None
    counts, last_ticks = Counter(), {}
    intervals = {'hp': [], 'mp': [], 'mp_at_empty_satiety': [], 'satiety_decay': [], 'idle_status': []}
    prior_idle = None
    decay_formats = Counter()
    prior_decay = None
    scope = None
    health_potion_active = False
    connection_dimension = None
    last_base_fame = None
    for row in recorded_rows(path):
        location = row.get('epoch'), row.get('map')
        if scope is None or location[0]!=scope[0] or row['kind']=='context_reset':
            health_potion_active = False
            connection_dimension = None
            last_base_fame = None
        if row['kind']=='timed_spell' and row.get('id')==110:
            health_potion_active = bool(row.get('duration'))
        if location != scope:
            previous = last_manual = maximum = prior_decay = prior_idle = last_action = pending_combined = None
            last_ticks.clear()
            scope = location
        if row['kind'] == 'client_action':
            last_action = datetime.datetime.fromisoformat(row['time'])
            if row.get('opcode') in (0x0F, 0x1C, 0x1E, 0x39, 0x3A, 0x3E):
                last_manual = last_action
            continue
        if row['kind'] != 'status':
            continue
        if pending_combined is not None:
            combined, sent_at = pending_combined
            pending_combined = None
            if (row.get('raw')==f"0800{combined['state']:02x}00" and combined['packet'] and row.get('packetId')
                    and adjacent_packets(combined['packet'],row['packetId'])
                    and 0 <= observed_time(row)-sent_at <= .01):
                combined['following_state'] = dict(packet=row['packetId'],length=row['length'],mask=0,mode=row['state'])
                continue
        preceding_fame = last_base_fame
        if base := row.get('baseBytes'):
            connection_dimension = base[0] >> 4
            last_base_fame = status_fame(row)
        if row.get('mask')==0 and row.get('length')==4 and row.get('state') in (0,1,2,4,8):
            if row.get('raw')!=f"0800{row['state']:02x}00":
                continue
            at = datetime.datetime.fromisoformat(row['time'])
            if last_action and 0 <= (at-last_action).total_seconds() <= 1:
                continue
            satiety = previous['currentValues'][2] if previous else None
            if prior_idle and satiety in (0,None):
                previous_packet, previous_at = prior_idle
                seconds = (at-previous_at).total_seconds()
                if 118<=seconds<=124:
                    intervals['idle_status'].append(seconds)
                    idle_status.append(dict(map=location[1],before_packet=previous_packet,packet=row.get('packetId'),
                                            state=row['state'],last_observed_satiety=satiety,seconds=round(seconds,3)))
            prior_idle = (row.get('packetId'),at) if satiety in (0,None) else None
            continue
        if maxima := row.get('maximumValues'):
            if maximum is not None and maxima!=maximum:
                previous = prior_decay = None
                last_ticks.clear()
            maximum = maxima
        if not row.get('currentValues'):
            continue
        current = row['currentValues']
        if current[2]>0:
            prior_idle = None
        old, previous = previous, row
        if (not old or (row.get('mask'),row.get('length')) not in ((0x20,20),(0x28,31),(0x28,32),(0x60,66))
                or row.get('state') not in (0,1,2,4,8)):
            continue
        observed = datetime.datetime.fromisoformat(row['time'])
        if last_manual and 0 <= (observed-last_manual).total_seconds() <= 1:
            continue
        hp, mp, satiety, _ = current
        old_hp, old_mp, old_satiety, _ = old['currentValues']
        if hp == 0 or old_hp == 0:
            continue
        sample = dict(map=location[1], connection_dimension=connection_dimension, before_packet=old.get('packetId'), packet=row.get('packetId'),
                      hp=[old_hp,hp], mp=[old_mp,mp], satiety=[old_satiety,satiety],
                      max_hp=maximum[0] if maximum else None, auxiliary_maximum=maximum[3] if maximum else None, state=row['state'], length=row['length'],mask=row['mask'],
                      auxiliary_values=[old['currentValues'][3],current[3]])
        if preceding_fame is not None and last_base_fame is not None and last_base_fame!=preceding_fame:
            sample['fame_change'] = [preceding_fame,last_base_fame]
        if row['mask']&8:
            sample.update(status_flags=row.get('extraDword'),speed_percent=row.get('extraWord'))
        if len(words := row.get('baseWords', [])) == 6:
            sample.update(strength=words[0], intelligence=words[1], vitality=words[3], agility=words[4])
        if (old_satiety-satiety==1 and 0<hp-old_hp<=10 and mp==old_mp and current[3]==old['currentValues'][3]
                and (last_action is None or (observed-last_action).total_seconds()>1)):
            combined_samples.append(sample)
            pending_combined = sample, observed_time(row)
        if satiety == 0 and old_satiety > 0 and hp == old_hp and mp == old_mp:
            empty_satiety.append(sample)
        if old_satiety-satiety==1 and hp==old_hp and mp==old_mp:
            decay_formats[row['mask'],row['length']]+=1
            if prior_decay and prior_decay[1]==old_satiety:
                seconds=(observed-prior_decay[0]).total_seconds()
                if 60<=seconds<=180:
                    intervals['satiety_decay'].append(seconds)
                    sample['seconds_since_previous']=round(seconds,3)
            decay_samples.append(sample)
            prior_decay=observed,satiety
        elif satiety != old_satiety:
            prior_decay=None
        if satiety != old_satiety:
            continue
        delta = hp-old_hp, mp-old_mp
        resource = {(3,0):'hp',(0,1):'mp'}.get(delta)
        if delta==(6,0) and (row.get('extraWord')==150 or health_potion_active):
            resource='hp'
        if resource is None or (resource=='hp' and satiety==0):
            continue
        counts[resource] += 1
        interval_key='mp_at_empty_satiety' if resource=='mp' and satiety==0 else resource
        if interval_key!=resource:counts[interval_key]+=1
        if prior := last_ticks.get(interval_key):
            seconds = (observed-prior).total_seconds()
            if 0 < seconds <= (15 if resource=='hp' else 45):
                intervals[interval_key].append(seconds)
                sample['seconds_since_previous'] = round(seconds, 3)
        last_ticks[interval_key] = observed
        samples.append(dict(resource=resource, **sample))
        if interval_key=='mp_at_empty_satiety':hungry_mana.append(sample)
    return {'session':path.parent.name,'candidate_counts':dict(counts),'samples':list(samples),
            'median_seconds':{key:round(median(values),3) for key,values in intervals.items() if values},
            'satiety_depleted':list(empty_satiety),'mp_at_empty_satiety':list(hungry_mana),
            'satiety_decay':list(decay_samples),'combined_hp_satiety':list(combined_samples),
            'idle_status_count':len(intervals['idle_status']),
            'idle_status':list(idle_status),
            'satiety_decay_formats':[dict(mask=mask,length=length,count=count)
                                     for (mask,length),count in sorted(decay_formats.items())]}


@server.tool(annotations=READ_ONLY)
def dagger_poison_observations(session: str = "latest") -> dict[str, Any]:
    """C1C 단검용독 사용의 상태·모션·소지품 형식과 S3A 아이콘 제거를 조회합니다."""
    path = session_events(session)
    timers, requests, last_timer = {}, {}, {}
    uses, clears = [], []
    active_scope, use_frames = None, {}
    for row in recorded_rows(path):
        kind, epoch = row['kind'], row.get('epoch')
        opcode, packet = row.get('opcode'), row.get('packetId')
        scope = epoch, row.get('map'), row.get('recentActionId')
        if kind=='client_action' and opcode==0x1C and "slot" in row:
            requests[scope]=row
            active_scope, use_frames = scope, {}
        elif scope==active_scope and kind=='status':
            use_frames['status'] = dict(packet=packet,length=row['length'],mask=row['mask'],mode=row['state'])
        elif scope==active_scope and opcode==0x29 and kind in ('effect','packet') and (raw := row.get('raw')):
            if len(raw) in (18,20) and raw[10:18]=='000b0000':
                use_frames['effect'] = dict(packet=packet,length=row['length'])
                use_frames['effect_actor'] = int(raw[2:10],16)
        elif scope==active_scope and kind=='damage_effect' and (raw := row.get('raw')):
            if (row.get('attacker')==row.get('target')==use_frames.get('effect_actor')
                    and raw[18:54]=='00090014002c000000000001000b0000ffff'):
                use_frames['motion'] = dict(packet=packet,length=row['length'])
        elif kind=='frame_header' and row.get('direction')=='S':
            if opcode==0x3A and row.get('plainLength')==18:
                timer=dict(packet=packet,time=row['time'],map=scope[1],epoch=epoch,action=scope[2],length=18)
                if scope==active_scope and 'status' in use_frames:
                    timer['status']=use_frames['status']
                timers[packet]=timer
                last_timer[epoch]=timer
            elif opcode==0x0A and epoch in last_timer:
                timer=last_timer.pop(epoch)
                elapsed=(datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(timer['time'])).total_seconds()
                if scope[2]==timer['action'] and 0<=elapsed<=.15:
                    timer.update(notice=packet,notice_length=row.get('plainLength'))
        elif kind=='timed_spell' and packet in timers:
            timers[packet].update(id=row['id'],duration=row['duration'])
        elif kind=='inventory_add' and row.get('wireSprite')==0x8F07 and scope in requests:
            request=requests[scope]
            if request['slot']!=row.get('slot'):
                continue
            elapsed=(datetime.datetime.fromisoformat(row['time'])-datetime.datetime.fromisoformat(request['time'])).total_seconds()
            if 0<=elapsed<=1:
                recent=next((timer for timer in reversed(timers.values()) if
                    (timer['epoch'],timer['map'],timer['action'])==scope and
                    ('id' not in timer or timer['id']==18 and timer['duration']>0)),None)
                if recent is not None:
                    recent.update(request=request['packetId'],slot=row['slot'],quantity_after=row['quantity'],
                                  inventory_packet=packet,inventory_length=row['length'],
                                  inventory_tail=row.get('trailingBytes'))
                    if scope==active_scope and 'motion' in use_frames:
                        recent.update(effect=use_frames['effect'],motion=use_frames['motion'])
                    uses.append(recent)
                    del requests[scope]
    for timer in timers.values():
        if 'request' in timer:
            continue
        if timer.get('id')==18 and timer.get('duration')==0:
            clears.append(dict(timer,evidence="decoded_timer"))
        elif 'id' not in timer and timer.get('notice_length')==18:
            expiry=datetime.datetime.fromisoformat(timer['time'])
            start=next((use for use in reversed(uses) if use['epoch']==timer['epoch'] and
                        599<=(expiry-datetime.datetime.fromisoformat(use['time'])).total_seconds()<=602),None)
            if start:
                clears.append(dict(timer,evidence="timer_frame_at_expected_expiry",start=start['packet'],
                    elapsed_seconds=(expiry-datetime.datetime.fromisoformat(start['time'])).total_seconds()))
    return dict(session=path.parent.name,uses=uses[-20:],clears=clears[-20:])


@server.tool(annotations=READ_ONLY)
def player_debuff_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """감속·저주 S3A, 직후 S08의 능력치 변화와 클라이언트 설명을 조회합니다."""
    path = session_events(session)
    samples = deque(maxlen=limit)
    pending = deque()
    active = {}
    scope = None
    status = None
    for row in recorded_rows(path):
        location = row.get('epoch'),row.get('map')
        if location!=scope:
            scope=location
            pending.clear()
            status=None
        kind=row['kind']
        if kind=='timed_spell' and row.get('id') in (0x6D,0x74):
            timer=row['id']
            previous=active.get(timer)
            phase='apply'
            if row['duration']==0:
                phase='expire'
                active.pop(timer,None)
            else:
                if previous is not None and previous!=scope:
                    phase='arrival'
                active[timer]=scope
            sample=dict(packet=row.get('packetId'),map=scope[1],id=timer,name=row.get('name'),
                        duration=row['duration'],length=row.get('length'),phase=phase)
            pending.append((datetime.datetime.fromisoformat(row['time']),sample))
            samples.append(sample)
        elif kind=='status' and len(row.get('baseWords',[]))==6:
            updated=dict(packet=row.get('packetId'),strength=row['baseWords'][0],intelligence=row['baseWords'][1],
                         vitality=row['baseWords'][3],agility=row['baseWords'][4],
                         physical_defense=row.get('physicalDefense'),magic_defense=row.get('magicDefense'))
            if pending:
                when=datetime.datetime.fromisoformat(row['time'])
                while pending and (when-pending[0][0]).total_seconds()>.25:
                    pending.popleft()
                if pending:
                    _,sample=pending.popleft()
                    sample.update(before=status,after=updated)
            status=updated
    metadata = {}
    wanted = {
        'TimedSpell': {'감속','저주'},
        'ItemInfoETC': {'미샤트','루미트','미샤트(감속)','미샤트(저주)'},
        'ItemHelpETC': {'루미트(감속)','루미트(저주)'},
    }
    try:
        for section,_,data in metadata_blocks((ROOT/'evidence'/'original-Meta.dat').read_bytes()):
            if section not in wanted:
                continue
            reader=Reader(data)
            reader.p=0
            entries={}
            for _ in range(reader.u16()):
                key=reader.string()
                values=[reader.string16() for _ in range(reader.u16())]
                if key in wanted[section]:
                    entries[key]=values
            metadata[section]=entries
    except (OSError,ValueError,zlib.error) as error:
        raise ToolError("감속·저주 클라이언트 메타데이터를 읽을 수 없습니다.") from error
    return dict(session=path.parent.name,samples=list(samples),client_metadata=metadata)


@server.tool(annotations=READ_ONLY)
def consumable_observations(session: str = "latest", item: str | None = None,
                            limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """C1C와 S08, 안내, 소지품, 효과음을 연결해 음식과 포션의 사용 결과를 조회합니다."""
    path = session_events(session)
    timers, samples, requests, uses = (deque(maxlen=limit) for _ in range(4))
    slots = {}
    previous_status, active = None, None
    carried_weight = preceding_status_mode = None
    preceding_effect_status = None
    capture_time = None
    food_names = {"식빵", "닭고기", "달걀", "삶은달걀", "돼지고기(조각)", "돼지고기", "쇠고기(조각)",
                  "사과", "바나나", "복숭아", "레몬", "밤", "감", "수박", "메론", "배", "체리", "포도",
                  "토마토", "땅콩", "딸기", "치즈", "햄", "프랑크 소시지", "햄버그 햄", "딸기 쨈"}

    def known(name):
        return name in food_names or name.startswith(("힐링포션","마나포션","스피드포션","체력향상포션","단검용독","엘의축복포션"))

    def matches(name):
        return known(name) and (item is None or item.casefold() in name.casefold())

    for row in recorded_rows(path):
        kind = row['kind']
        capture_time = row.get('time',capture_time)
        scope = row.get('epoch'),row.get('map'),row.get('recentActionId')
        if kind=='context_reset':
            slots.clear()
            previous_status = active = carried_weight = preceding_status_mode = preceding_effect_status = None
        if kind=='client_action' and row.get('opcode')==0x1C and "slot" in row:
            slot = row['slot']
            prior_item = slots.get(slot,{})
            request = dict(request=row['packetId'], time=row['time'], map=scope[1],slot=slot,
                name=prior_item.get('name',''),quantity_before=prior_item.get('quantity'),
                previous_status=previous_status,preceding_status_mode=preceding_status_mode,
                preceding_effect_status=preceding_effect_status if preceding_effect_status and
                    preceding_effect_status['epoch']==scope[0] else None,
                carried_weight_before=carried_weight,item_weight=prior_item.get('word4'),responses=[])
            active = (scope, datetime.datetime.fromisoformat(row['time']), request)
            if matches(request['name']):
                requests.append(request)
            continue

        use = None
        blessing_timer = (active and kind=='timed_spell' and scope[:2]==active[0][:2] and
                          active[2]['name'].startswith("엘의축복포션") and row.get('name')=="엘의축복1일")
        if active and (scope==active[0] or blessing_timer) and kind in ('status','timed_spell','inventory_add','inventory_remove','sound','frame_header'):
            elapsed=(datetime.datetime.fromisoformat(row['time'])-active[1]).total_seconds()
            if 0<=elapsed<=1:
                use=active[2]
        packet = row.get('packetId')
        if kind=='frame_header' and row.get('direction')=='S' and row.get('opcode')==0x0A and use:
            use['responses'].append(dict(opcode="S0A",packet=packet,length=row.get('plainLength')))
        if kind not in ('timed_spell','status','inventory_add','inventory_remove','sound') or not row.get('raw'):
            continue
        try:
            event = decode(bytes.fromhex(row['raw']))
        except (ValueError,IndexError):
            continue
        common = dict(packet=packet,length=row['length'])
        if kind=='status':
            preceding_status_mode = event['state']
            if 'currentValues' in event:
                previous_status = dict(packet=packet,time=row['time'],current_values=event['currentValues'],state=event['state'])
            effect = None
            if event['mask']&8 and 'extraDword' in event:
                effect = dict(effect_flags=event['extraDword'],movement_percent=event['extraWord'])
                preceding_effect_status = dict(packet=packet,epoch=scope[0],**effect)
            if use and (event['mask']&0x68 or event['mask']==0):
                use['responses'].append(dict(common,opcode="S08",mask=event['mask'],
                                             state=event['state'],current_values=event.get('currentValues'),**(effect or {})))
        elif kind=='timed_spell':
            timer = {key:event[key] for key in ('id','name','duration')}
            if item is None or use and matches(use['name']):
                timers.append(timer)
            if use:
                use['responses'].append(dict(common,opcode="S3A",**timer))
                if blessing_timer and event['id']==22 and event['duration']==20864 and 'timer_packet' not in use:
                    use['timer_packet']=packet
                    uses.append(use)
        elif kind=='sound' and use and not event['bgm']:
            use['responses'].append(dict(common,opcode="S19",sound=event['id']))
        elif kind in ('inventory_add','inventory_remove'):
            slot = event['slot']
            carried_weight = event['word3'] if kind=='inventory_add' else event['word']
            if kind=='inventory_add':
                slots[slot]=event
                if matches(event['name']):
                    sample = dict(name=event['name'],slot=slot,quantity=event['quantity'],
                                  satiety=previous_status['current_values'][2] if previous_status else None,packet_id=packet)
                    if event['wireSprite']==0x8F94 and event['name'].startswith("엘의축복포션"):
                        sample['item_timer_word']=event['dword']
                    samples.append(sample)
            else:
                slots.pop(slot,None)
            if use and slot==use['slot'] and "inventory_packet" not in use:
                if kind=='inventory_add' and not use['name']:
                    use['name']=event['name']
                    if matches(use['name']):
                        requests.append(use)
                quantity = event.get('quantity',0)
                if matches(use['name']) and (kind=='inventory_add' and event['name']==use['name'] and
                    use['quantity_before'] in (None,quantity+1) or kind=='inventory_remove' and event['byte2'] in (2,6)):
                    use.update(quantity_after=quantity,inventory_packet=packet,carried_weight_after=carried_weight)
                    use['responses'].append(dict(common,opcode="S0F" if kind=='inventory_add' else "S10",
                                                 mode=event.get('byte2'),word=event.get('word'),quantity=quantity))
                    before = use['previous_status']
                    vitals = [response for response in use['responses'] if response.get('current_values') is not None]
                    if before and len(vitals)==1 and not use['name'].startswith(("스피드포션","체력향상포션","단검용독")):
                        use['vitals_packet'] = vitals[0]['packet']
                        use['auxiliary_values'] = [before['current_values'][3],vitals[0]['current_values'][3]]
                        use['vitals_delta'] = dict(zip(('hp','mp','fp'), (current-previous for current,previous in
                            zip(vitals[0]['current_values'],before['current_values']))))
                    if 'timer_packet' not in use:uses.append(use)
    return dict(session=path.parent.name, capture_time=capture_time, timers=list(timers), inventory_observations=list(samples),
                uses=list(uses),
                unconfirmed_requests=[request for request in requests
                                      if 'inventory_packet' not in request and 'timer_packet' not in request])

@server.tool(annotations=READ_ONLY)
def fighting_rooster_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """싸움닭의 C1C 설치, 차감, 같은 개체의 방향과 보행 패킷을 연결합니다."""
    path = session_events(session)
    placements = deque(maxlen=limit)
    active = {}
    request = candidate = context = None

    def packet(row):
        return dict(packet=row.get('packetId'), length=row.get('length'))

    for row in recorded_rows(path):
        kind = row['kind']
        scope = row.get('epoch'),row.get('map')
        if scope != context or kind in ('context_reset','map'):
            active.clear()
            request = candidate = None
            context = scope
        if kind == 'client_action':
            request = row if row.get('opcode')==0x1C and row.get('length')==2 and row.get('slot') and row.get('recentActionId') is not None else None
            candidate = None
        elif kind == 'objects' and request and request.get('recentActionId')==row.get('recentActionId') and len(row['objects'])==1:
            obj, = row['objects']
            if (obj['category']==0 and obj['sprite']==0x43F2 and obj['name'].endswith('의싸움닭')
                    and 0 <= observed_time(row)-observed_time(request) <= .75):
                candidate = dict(map=scope[1], request=request.get('packetId'), slot=request['slot'], oid=obj['oid'],
                                 appearance=dict(packet(row),tile=[obj['x'],obj['y'],obj['floor']],direction=obj['type'],
                                                 flags=obj['flags']), steps=[])
        elif (kind == 'inventory_remove' and candidate and request['recentActionId']==row.get('recentActionId')
              and row.get('slot')==request['slot'] and row.get('byte2')==6
              and 0 <= observed_time(row)-observed_time(request) <= .75):
            candidate['consumed'] = dict(packet(row), carried_weight=row.get('word'))
            placements.append(candidate)
            active[candidate['oid']] = dict(result=candidate,last_at=observed_time(row),turn=None,turn_at=None)
            candidate = None
        elif kind in ('direction','object_move','death','object_remove') and (state := active.get(row['oid'])):
            result = state['result']
            if kind == 'direction':
                state['turn'] = dict(packet(row),direction=row['direction'])
                state['turn_at'] = observed_time(row)
            elif kind == 'object_move' and row['direction'] < len(DELTAS):
                at = observed_time(row)
                dx,dy = DELTAS[row['direction']]
                step = dict(packet(row),origin=[row['x'],row['y'],row['floor']],
                            destination=[row['x']+dx,row['y']+dy,row['floor']],
                            direction=row['direction'],animation=row['animation'],
                            elapsed_ms=round((at-state['last_at'])*1000))
                turn = state['turn']
                if turn and turn['direction']==row['direction'] and 0 <= at-state['turn_at'] <= .1:
                    step['turn'] = turn
                state.update(last_at=at,turn=None,turn_at=None)
                if len(result['steps'])<limit:
                    result['steps'].append(step)
            elif kind in ('death','object_remove'):
                result[kind] = packet(row)
                del active[row['oid']]
    return dict(session=path.parent.name,placements=list(placements))


def prior_inventory_capture(path, context, *, earlier=None):
    """Carry over known slots when a recording was restarted in the same world session."""
    earlier = earlier or next((session for session in recent_sessions() if session < path.parent.name), None)
    if earlier is None:
        return {}
    actor = last = None
    slots = {}
    for row in recorded_rows(capture_file(earlier,'events.jsonl')):
        last = row
        kind = row['kind']
        if kind == 'context_reset':
            actor = None
            slots.clear()
        elif kind == 'context_snapshot':
            actor = row.get('selfId')
        elif kind == 'self_identity':
            actor = row.get('oid')
        elif kind == 'inventory_add':
            slots[row['slot']] = row
        elif kind == 'inventory_remove':
            slots.pop(row['slot'],None)
    if not last or actor != context['selfId'] or last.get('map') != context.get('map'):
        return {}
    elapsed = datetime.datetime.fromisoformat(context['time'])-datetime.datetime.fromisoformat(last['time'])
    return {slot:(earlier,row) for slot,row in slots.items()} if 0 <= elapsed.total_seconds() <= 15 else {}


@server.tool(annotations=READ_ONLY)
def inventory_layout_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20,
                                  request_id: str | None = None) -> dict[str, Any]:
    """C30·C44·C58·C6D 요청, 가방 이동 응답의 길이와 슬롯별 갱신을 조회합니다."""
    path = session_events(session)
    requests, balances = deque(maxlen=limit), {}
    inventory, previous_slots = {}, None
    bag_positions = {}
    initial_context = None
    pending = None
    request_lengths = {0x30:8,0x44:6,0x58:11,0x6D:6}
    drag_formats = Counter()
    count = 0
    for row in recorded_rows(path):
        kind, op, epoch = row['kind'], row.get('opcode'), row.get('epoch')
        if kind == 'context_snapshot' and row.get('selfId') and initial_context is None:
            initial_context = row
        if kind == 'context_reset':
            pending = None
        elif kind == 'client_action' or kind == 'frame_header' and row.get('direction') == 'C':
            if pending and pending['request'] == row.get('packetId'):
                continue
            if pending and pending['opcode'] == 'C44' and op not in request_lengths and op not in (0x1C,0x1E):
                continue
            pending = None
            length = row.get('plainLength' if kind == 'frame_header' else 'length')
            if op not in request_lengths or length != request_lengths[op]:
                continue
            pending = dict(request=row['packetId'], opcode=f'C{op:02X}', length=length, map=row.get('map'),
                           updates=[])
            pending.update({key:row[key] for key in ('mode','slot','destinationSlot','dropX','dropY','dropQuantity') if key in row})
            if op == 0x6D and epoch in balances:
                pending['gold_before'] = balances[epoch]
            if request_id is None or pending['request']==request_id:
                requests.append(pending)
            request_epoch, request_time = epoch, datetime.datetime.fromisoformat(row['time'])
            count += 1
        elif kind in ('inventory_add','inventory_remove','status','equipment_remove','human') or kind == 'frame_header' and op == 0x0A:
            gold = row.get('quantity') if row.get('wireSprite') == 0x8BB8 else row.get('gold')
            if gold is not None:
                balances[epoch] = gold
            slot = row.get('slot')
            item_key = epoch, slot
            before_item = inventory.get(item_key)
            prior_position = bag_positions.get(item_key)
            if kind=='inventory_add' and 'word1' in row and 'word2' in row:
                bag_positions[item_key] = [row['word1'],row['word2']]
            elif kind=='inventory_remove' and row.get('byte2') not in (9,12):
                bag_positions.pop(item_key,None)
            already_observed = item_key in inventory
            if kind in ('inventory_add','inventory_remove'):
                inventory[item_key] = row if kind=='inventory_add' else None
            if (not pending or epoch != request_epoch or row.get('map') != pending['map']
                    or not 0 <= (datetime.datetime.fromisoformat(row['time']) - request_time).total_seconds() <= 1):
                continue
            if pending['opcode'] == 'C44':
                if kind == 'equipment_remove' and slot == pending.get('slot'):
                    pending['removed_slot'] = slot
                mask = row.get('mask')
                equipment_status = kind=='status' and mask is not None and (mask==0 or mask&0x48)
                if equipment_status or 'removed_slot' in pending and (
                        kind == 'equipment_remove' and slot == pending['removed_slot'] or
                        kind == 'inventory_add' and row.get('byte1') == pending['removed_slot'] or
                        kind == 'human' and row.get('isSelf')):
                    pending['updates'].append({key:row[key] for key in ('kind','packetId','slot','name','wireSprite','quantity',
                        'word1','word2','mask','state','extraDword','extraWord','refreshWord','durabilityPercent','length','trailingBytes',
                        'baseWords','maximumValues','physicalDefense','magicDefense','currentValues') if key in row})
                    if kind=='inventory_add' and 'initial_bag_slot' not in pending:
                        pending['initial_bag_slot'] = dict(slot=slot,previous_position=prior_position,
                            sent_position=[row.get('word1'),row.get('word2')])
                    if equipment_status and sum(update['kind']=='inventory_add' for update in pending['updates'])>=2:
                        pending = None
                continue
            if kind in ('inventory_add','inventory_remove'):
                pending['updates'].append({key:row[key] for key in ('kind','packetId','slot','name','wireSprite','quantity',
                    'word1','word2','word3','word4','byte2','word','length','trailingBytes') if key in row})
            if pending['opcode']=='C30' and kind=='inventory_add' and 'drag_response' not in pending:
                position = [row.get('word1'),row.get('word2')]
                expected = [pending.get('dropX'),pending.get('dropY')]
                typed = all(value is not None for value in expected)
                if slot==pending.get('slot',slot) and (not typed or position==expected) and row.get('wireSprite') is not None:
                    packet_length,suffix = row.get('length'),row.get('trailingBytes')
                    pending['drag_response'] = dict(packet=row.get('packetId'),position=position,typed_request=typed,
                                                    length=packet_length,trailing_bytes=suffix)
                    drag_formats[row['wireSprite'],row.get('name'),packet_length,suffix,typed]+=1
            if pending['opcode'] == 'C58' and len(pending['updates']) == 3:
                first,middle,last = pending['updates']
                if (first['kind']==last['kind']=='inventory_add' and first.get('slot') is not None
                        and first['slot']==last.get('slot') and first.get('wireSprite')==last.get('wireSprite')):
                    if middle['kind']=='inventory_remove' and middle.get('slot') not in (None,first['slot']):
                        pending['layout']=dict(source_slot=middle['slot'],target_slot=first['slot'],
                            initial_position=[first.get('word1'),first.get('word2')],position=[last.get('word1'),last.get('word2')],
                            carried_weight=last.get('word3'))
                    elif middle['kind']=='inventory_add' and middle.get('slot')==first['slot']:
                        pending['layout']=dict(source_slot=first['slot'],target_slot=first['slot'],
                            initial_position=[first.get('word1'),first.get('word2')],position=[last.get('word1'),last.get('word2')],
                            carried_weight=last.get('word3'))
            if pending['opcode'] != 'C6D':
                continue
            if kind=='inventory_remove' and row.get('byte2')==0:
                pending.setdefault('slot',slot)
            if kind in ('inventory_add','inventory_remove') and slot==pending.get('slot') and 'item_before' not in pending:
                item_session = path.parent.name
                if not already_observed and initial_context and epoch==initial_context.get('epoch'):
                    if previous_slots is None:
                        previous_slots = prior_inventory_capture(path,initial_context)
                    item_session,before_item = previous_slots.get(slot,(item_session,None))
                if before_item and before_item.get('wireSprite')!=0x8BB8:
                    item = before_item
                    if before_item.get('raw'):
                        try:
                            item = decode(bytes.fromhex(before_item['raw']))
                        except (ValueError,IndexError):
                            pass
                    pending['item_before'] = {key:item[key] for key in ('name','wireSprite','quantity','durabilityPercent') if key in item}
                    pending['item_before'].update(session=item_session,packet=before_item.get('packetId'))
            if kind=='status' and gold is not None:
                pending['status'] = dict(packet=row.get('packetId'),mask=row.get('mask'),state=row.get('state'),
                                         length=row.get('length'),padding=row.get('trailingBytes'))
            if kind=='frame_header' and row.get('direction')=='S':
                pending['result_message'] = dict(packet=row.get('packetId'),length=row.get('plainLength'))
            if gold is not None:
                pending['gold_after'] = gold
                if 'gold_before' in pending:
                    pending['gold_delta'] = gold - pending['gold_before']
    return dict(session=path.parent.name,count=count,observations=list(requests),
                drag_formats=[dict(sprite=sprite,item=name,length=length,trailing_bytes=suffix,typed_request=typed,count=total)
                              for (sprite,name,length,suffix,typed),total in drag_formats.most_common()])


@server.tool(annotations=READ_ONLY)
def forced_equipment_stow_observations(session: str = "latest",
                                       limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """직접 착용·해제 요청 없이 관측된 장비 해제와 가방 이동을 대조합니다."""
    path = session_events(session)
    results, sequence = deque(maxlen=limit), deque(maxlen=6)
    scope = action = None
    pattern = ('status', 'equipment_remove', 'human', 'inventory_add', 'inventory_add', 'status')
    for row in recorded_rows(path):
        kind = row['kind']
        current = row.get('epoch'), row.get('map'), row.get('recentActionId')
        if current != scope or kind in ('map', 'context_reset', 'client_action'):
            scope = current
            sequence.clear()
            action = row if kind == 'client_action' else None
        if not action or action.get('opcode') in (0x1C, 0x1E, 0x44) or kind not in pattern:
            continue
        if kind == 'human' and not row.get('isSelf'):
            continue
        sequence.append(row)
        if tuple(r['kind'] for r in sequence) != pattern:
            continue
        before, removed, appearance, initial, placed, after = sequence
        if (not before.get('mask',0)&0x40 or not after.get('mask',0)&0x40
                or initial.get('byte1') != removed['slot'] or placed.get('byte1') != removed['slot']
                or initial.get('slot') != placed.get('slot') or initial.get('wireSprite') != placed.get('wireSprite')
                or not 0 <= observed_time(after)-observed_time(before) <= .25):
            continue
        fields = ('packetId','kind','slot','name','wireSprite','durabilityPercent','word1','word2','word3','word4',
                  'refreshWord','mask','state','length','trailingBytes','baseWords','maximumValues','physicalDefense','magicDefense')
        results.append(dict(map=scope[1], nearby_action=action.get('packetId'), action_opcode=action.get('opcode'),
                            updates=[{key:r[key] for key in fields if key in r} for r in sequence]))
    return dict(session=path.parent.name, observations=list(results))


@server.tool(annotations=READ_ONLY)
def item_drop_observations(session: str = "latest") -> dict[str, Any]:
    """C08 내려놓기와 같은 동작의 S07·S10·S0F 응답, 이어진 S53 줍기를 조회합니다."""
    path = session_events(session)
    drops, by_object = [], {}
    active = None
    for row in recorded_rows(path):
        kind = row.get('kind')
        if kind == 'client_action':
            active = None
            if row.get('opcode') == 0x08:
                fields = ('time', 'map', 'packetId', 'slot', 'dropX', 'dropY', 'dropQuantity')
                active = {'request': {key: row[key] for key in fields if key in row},
                          'objects': [], 'inventory_updates': []}
                drops.append(active)
                action_id = row.get('recentActionId')
        elif kind == 'pickup' and (entry := by_object.get(row.get('itemId'))) is not None:
            entry['pickup_packet_id'] = row.get('packetId')
        elif active is not None and action_id == row.get('recentActionId') and active['request'].get('map') == row.get('map'):
            request = active['request']
            if kind == 'objects':
                for obj in row.get('objects', []):
                    if obj.get('category') == 3 and all(obj.get(field) == request.get(key, obj.get(field))
                                                        for field, key in (('x', 'dropX'), ('y', 'dropY'))):
                        entry = {**obj, 'packet_id': row.get('packetId')}
                        active['objects'].append(entry)
                        by_object[obj['oid']] = entry
            elif kind in ('inventory_remove', 'inventory_add') and request.get('slot', row.get('slot')) == row.get('slot'):
                fields = ('kind', 'packetId', 'slot', 'byte2', 'word', 'quantity', 'raw')
                active['inventory_updates'].append({key: row[key] for key in fields if key in row})
    return {'session': path.parent.name, 'count': len(drops), 'observations': drops[-30:]}


@server.tool(annotations=READ_ONLY)
def world_snapshot_status() -> dict[str, Any]:
    """로컬 서버의 월드 데이터 항목 수와 기록에서 준비한 메타데이터 파일 수를 조회합니다."""
    try:
        metadata_files = len(metadata_index(LIVE.with_name("recorded-meta.dat")))
    except FileNotFoundError:
        metadata_files = 0
    try:
        with LIVE.open(encoding="utf-8-sig") as stream:
            world = json.load(stream)
            stat = os.fstat(stream.fileno())
    except FileNotFoundError:
        return {"exists": False, "counts": {table: 0 for table in TABLES}, "recorded_metadata_files": metadata_files}
    return {
        "exists": True,
        "updated_at": utc_timestamp(stat.st_mtime),
        "counts": {table: len(world.get(table, [])) for table in TABLES},
        "monster_slots_by_map": dict(Counter(row['Map'] for row in world.get('Monsters', []))),
        "monster_loot_recipes": len(world.get("MonsterLoot", [])),
        "recorded_melee_outcomes": len(world.get("MonsterMelee", [])),
        "recorded_spell_damage_outcomes": len(world.get("MonsterSpellDamage", [])),
        "monster_kill_experience_values": len(world.get("MonsterKillExperience", [])),
        "recorded_metadata_files": metadata_files,
        "clock": world.get("Clock"),
        "shops": sum(bool(row.get("Shop")) for row in world.get("Npcs", [])),
        "npc_dialogs": sum(bool(row.get("OpenDialog") or row.get("DialogReplies") or row.get("PursuitReplies")) for row in world.get("Npcs", [])),
        "npc_dialog_pages": sum(len(row.get("PursuitReplies", [])) for row in world.get("Npcs", [])),
        "npc_dialog_travel": sum(len(row.get("PursuitTravel", [])) for row in world.get("Npcs", [])),
    }


@server.tool(annotations=READ_ONLY)
def treasure_chest_observations(session: str = "latest") -> dict[str, Any]:
    """일반·해적 나무상자의 출현, 이동, 반격, 파괴 드랍과 상자좀비를 조회합니다."""
    path = session_events(session)
    tracker, broken = DropObservations(), []
    zombies, retaliations, respawns = deque(maxlen=24), deque(maxlen=24), deque(maxlen=24)
    awaiting = {}
    position = None
    last_refresh = None

    def clearly_visible(tile):
        return position is not None and tile[2]==position[2] and abs(tile[0]-position[0])<=12 and abs(tile[1]-position[1])<=7
    for row in recorded_rows(path):
        kind, map_id = row['kind'], row.get('map')
        context = (row.get('epoch'), map_id)
        if kind in ('context_reset','map') or context != tracker.context:
            zombies.clear()
            awaiting.clear()
            position = last_refresh = None
        if kind in ('self_position','self_move','context_snapshot'):
            at = row.get('position') if kind=='context_snapshot' else row
            if at and all(key in at for key in ('x','y','floor')):
                position = (at['x'],at['y'],at['floor'])
                for tile in list(awaiting):
                    if not clearly_visible(tile):
                        del awaiting[tile]
        elif row.get('opcode')==7 and (kind in ('decode_error','privacy_parse_error') or
                kind=='frame_header' and row.get('direction')=='S' and
                (row.get('lengthMatches') is False or row.get('opcodeMatches') is False)):
            awaiting.clear()
        elif kind=='frame_header' and row.get('opcode')==0x0C and row.get('direction')=='C':
            last_refresh = observed_time(row)
        if kind == 'objects':
            for obj in row['objects']:
                if (obj['category'], obj['sprite'], obj['name']) == (0, 0x4019, '나무상자'):
                    tile = (obj['x'], obj['y'], obj['floor'])
                    if tile in awaiting:
                        death, old_oid, when = awaiting.pop(tile)
                        now = observed_time(row)
                        if obj['oid']!=old_oid and now>when and (last_refresh is None or now-last_refresh>.5):
                            respawns.append(dict(map=map_id,tile=tile,death=death,appearance=row.get('packetId'),
                                                 previous_oid=old_oid,oid=obj['oid'],seconds=round(now-when,3)))
                if (obj['category'], obj['sprite'], obj['name']) == (0, 0x47D0, '상자좀비'):
                    tile = (obj['x'], obj['y'], obj['floor'])
                    zombies.append((observed_time(row), tile, row.get('packetId'), obj['oid']))
        elif kind == 'damage_effect' and context == tracker.context:
            attacker = tracker.active.get(row['attacker'])
            words = row.get('extraWords', [])
            if attacker and (attacker['name'], attacker['sprite']) == ('나무상자', 0x440F) and len(words) >= 2:
                retaliations.append({'map':map_id,'packetId':row.get('packetId'),'appearance':attacker['appearance'],
                                     'attacker':row['attacker'],'target':row['target'],'damage':words[1]})
        if (sample := tracker.ingest(row)) is not None and sample['name'] == '나무상자':
            if sample['sprite'] == 0x4019:
                when = observed_time(row)
                if clearly_visible(sample['tile']):
                    awaiting[sample['tile']] = sample['packetId'],row['oid'],when
                for spawn in reversed(zombies):
                    at, tile, packet_id, oid = spawn
                    if tile == sample['tile'] and 0 <= when-at <= .25:
                        sample['zombie'] = {'packetId':packet_id,'oid':oid,'tile':tile}
                        zombies.remove(spawn)
                        break
            broken.append(sample)
    appearances = {f'0x{sprite:04x}':count for (name,sprite),count in tracker.appearances.items() if name=='나무상자'}
    moves = {f'0x{sprite:04x}':count for (name,sprite),count in tracker.moves.items() if name=='나무상자'}
    return {'session':path.parent.name,'appearances_by_sprite':appearances,'moves_by_sprite':moves,
            'breaks':broken[-24:],'pirate_retaliations':list(retaliations),'stationary_respawns':list(respawns)}


@server.tool(annotations=READ_ONLY)
def ground_pickup_observations(session: str = "latest",
                               limit: Annotated[int, Field(ge=1, le=50)] = 20,
                               request_id: str | None = None) -> dict[str, Any]:
    """C07 뒤 S53·S0E와 인벤토리 지급 또는 같은 개체의 S07 반환을 비교합니다."""
    path = session_events(session)
    recent, adjusted, notice_only = (deque(maxlen=limit) for _ in range(3))
    ground, slots, vacated = {}, {}, {}
    context = (None,None)
    player = pending = carried_weight = position = baseline_session = None
    initial_snapshot = True
    count = adjusted_count = notice_only_count = returned_ground_count = 0
    placement_formats = Counter()

    def finish():
        nonlocal count, adjusted_count, notice_only_count, returned_ground_count
        if not pending or not pending.get('removal'):
            return
        result = {k:v for k,v in pending.items() if k not in ('action','at','last_inventory_raw','notices')}
        if not pending['inventory']:
            if pending['self_confirmed'] and pending['notices']:
                notice_only_count += 1
                returned_ground_count += 'returned_ground' in result
                if request_id is None or result['request']==request_id:
                    notice_only.append(dict(result,notices=pending['notices']))
            return
        actual = pending['inventory'][-1]['position']
        result.update(inventory_position=actual,position_changed=actual!=pending['requested_position'])
        placement_formats[pending['inventory'][-1]['flag1'],result['position_changed'],len(pending['inventory'])] += 1
        count += 1
        selected = request_id is None or result['request']==request_id
        if selected:
            recent.append(result)
        if result['position_changed']:
            if selected:
                adjusted.append(result)
            adjusted_count += 1

    for row in recorded_rows(path):
        kind = row['kind']
        current = row.get('epoch'), row.get('map')
        if current != context or kind == 'context_reset':
            finish()
            ground.clear()
            pending = None
            position = None
            if current[0] != context[0] or kind == 'context_reset':
                player = None
                carried_weight = None
                baseline_session = None
                slots.clear()
                vacated.clear()
            context = current
        if kind in ('self_identity','context_snapshot'):
            player = row.get('oid') if kind == 'self_identity' else row.get('selfId')
        if kind == 'context_snapshot' and player and initial_snapshot:
            initial_snapshot = False
            if not slots and (earlier := adjacent_capture_session(path, row)):
                previous = prior_inventory_capture(path, row, earlier=earlier)
                slots.update((slot,(item['word1'],item['word2'],item.get('packetId')))
                             for slot,(_,item) in previous.items() if {'word1','word2'} <= item.keys())
                if slots:
                    baseline_session = earlier
        if kind in ('self_position','self_move','context_snapshot'):
            at = row.get('position') if kind == 'context_snapshot' else row
            if at and all(key in at for key in ('x','y','floor')):
                position = [at['x'],at['y'],at['floor']]
        if kind == 'objects':
            for obj in row['objects']:
                if obj['category'] == 3:
                    ground.setdefault(obj['oid'], dict(obj,packet=row.get('packetId')))
                    if (pending and pending.get('removal') and pending['notices'] and 'returned_ground' not in pending
                            and pending['action'] is not None and pending['action']==row.get('recentActionId')
                            and 0 <= observed_time(row)-pending['at'] <= 1
                            and all(obj[key]==pending['item'][key] for key in ('oid','sprite','name','quantity','floor'))):
                        tile = [obj['x'],obj['y'],obj['floor']]
                        padding = row.get('trailingBytes')
                        if padding is None and row.get('raw'):
                            try:
                                padding = decode(bytes.fromhex(row['raw'])).get('trailingBytes')
                            except (TypeError, ValueError, IndexError):
                                pass
                        pending['returned_ground'] = dict(packet=row.get('packetId'),tile=tile,length=row.get('length'),
                                                         padding=padding,at_picker_feet=None if position is None else tile==position)
        elif kind == 'client_action':
            finish()
            words = row.get('pickupWords')
            pending = (dict(request=row.get('packetId'),map=current[1],action=row.get('recentActionId'),
                            at=observed_time(row),tile=words[:2],requested_position=words[2:],inventory=[],notices=[],
                            carried_weight_before=carried_weight)
                       if row.get('opcode')==7 and row.get('mode')==0 and words and len(words)==4 else None)
            if pending:
                x,y = pending['requested_position']
                nearby = [dict(slot=slot,position=[a,b],distance_squared=(a-x)**2+(b-y)**2,packet=packet)
                          for slot,(a,b,packet) in slots.items() if (a,b)!=(0,0)]
                pending.update(known_slot_count=len(nearby),observed_occupied_slots=len(slots),
                               inventory_baseline_session=baseline_session,
                               nearest_known_slots=sorted(nearby,key=lambda entry:entry['distance_squared'])[:3])
        elif kind == 'object_remove':
            ground.pop(row['oid'],None)
            if pending and row['oid']==pending.get('item',{}).get('oid'):
                pending['removal']=row.get('packetId')
                pending['removal_length']=row.get('length')
        elif (pending and pending.get('removal') and kind=='frame_header' and row.get('direction')=='S'
              and row.get('opcode')==0x0A and pending['action'] is not None and pending['action']==row.get('recentActionId')
              and 0 <= observed_time(row)-pending['at'] <= 1):
            pending['notices'].append(dict(packet=row.get('packetId'),length=row.get('plainLength'),mode=row.get('messageMode')))
        elif (pending and pending['notices'] and kind=='pickup_notice' and row.get('requestPacketId')==pending['request']
              and row.get('pickupPacketId')==pending.get('pickup') and row.get('removalPacketId')==pending.get('removal')
              and row.get('itemId')==pending.get('item',{}).get('oid') and row.get('packetId')==pending['notices'][-1]['packet']):
            pending['notices'][-1].update({key:row[key] for key in ('text','messageBytes','trailingBytes') if key in row})
        elif (pending and pending['action'] is not None and pending['action']==row.get('recentActionId')
              and kind in ('pickup','inventory_add') and 0 <= observed_time(row)-pending['at'] <= 1):
            if kind=='pickup' and (player is None or player==row.get('pickerId')):
                item=ground.get(row['itemId'])
                if item and pending['tile']==[item['x'],item['y']]:
                    peers=[obj for obj in ground.values()
                           if (obj['x'],obj['y'],obj['floor'])==(item['x'],item['y'],item['floor'])]
                    pending.update(item=item,pickup=row.get('packetId'),newest_visible=peers[-1]['oid']==item['oid'],
                                   visible_stack=len(peers),self_confirmed=player is not None,pickup_length=row.get('length'))
            elif (kind=='inventory_add' and (item:=pending.get('item')) and row.get('wireSprite')==item['sprite']
                  and row.get('name')==item['name'] and (not pending['inventory'] or row.get('slot')==pending['inventory'][0]['slot'])):
                if not pending['inventory'] and (previous_slot := vacated.get(row.get('slot'))):
                    pending['previous_slot'] = previous_slot
                raw = row.get('raw')
                pending['duplicate_final_frame'] = raw is not None and raw==pending.get('last_inventory_raw')
                pending['last_inventory_raw'] = raw
                pending['inventory'].append(dict(packet=row.get('packetId'),slot=row.get('slot'),
                                                 position=[row.get('word1'),row.get('word2')],quantity=row.get('quantity'),
                                                 carried_weight=row.get('word3'),item_weight=row.get('word4'),
                                                 flag1=row.get('flag1'),
                                                 length=row.get('length'),padding=row.get('trailingBytes')))
        if kind=='inventory_add' and 'word3' in row:
            carried_weight = row['word3']
            if {'slot','word1','word2'} <= row.keys():
                vacated.pop(row['slot'],None)
                slots[row['slot']] = row['word1'],row['word2'],row.get('packetId')
        elif kind=='inventory_remove' and 'word' in row:
            carried_weight = row['word']
            slot, mode = row.get('slot'),row.get('byte2')
            last_item = slots.pop(slot,None)
            if mode in (9,12) and last_item is None:
                last = vacated.get(slot,{})
                position, packet = last.get('position'),last.get('inventory_packet')
            elif last_item is not None:
                x,y,packet = last_item
                position = [x,y]
            else:
                position = packet = None
            vacated[slot] = dict(slot=slot,position=position,inventory_packet=packet,
                                 removal_packet=row.get('packetId'),removal_mode=mode)
    finish()
    return dict(session=path.parent.name,count=count,adjusted_count=adjusted_count,
                placement_formats=[dict(flag1=flag,position_changed=changed,inventory_frames=frames,count=total)
                                   for (flag,changed,frames),total in placement_formats.most_common()],
                observations=list(recent),adjusted=list(adjusted),
                notice_only_count=notice_only_count,returned_ground_count=returned_ground_count,notice_only=list(notice_only))


@server.tool(annotations=READ_ONLY)
def ability_mastery_markers(session: str = "latest", map_id: int | None = None,
                            limit: Annotated[int, Field(ge=1, le=100)] = 25) -> dict[str, Any]:
    """S2C 숙련도 갱신과 카운터 갱신 사이에 기록된 S3F 본문을 조회합니다."""
    path = session_events(session)
    markers = deque(maxlen=limit)
    headers = bodies = 0
    for row in recorded_rows(path):
        if map_id is not None and row.get('map') != map_id:
            continue
        if row.get('direction') != 'S' or row.get('opcode') != 0x3F:
            continue
        if row['kind'] == 'frame_header':
            headers += 1
        elif row['kind'] == 'mastery_marker':
            bodies += 1
            fields = ('map','packetId','abilityPacketId','refreshPacketId','words','slot','ability','mastery','counter','reference')
            markers.append({key:row[key] for key in fields})
    return dict(session=path.parent.name, headers=headers, bodies=bodies, observations=list(markers))


@server.tool(annotations=READ_ONLY)
def training_target_observations(session: str = "latest", map_id: int | None = 297) -> dict[str, Any]:
    """수련장 상태와 인형의 설치·피해·파괴를 대조합니다. map_id를 null로 주면 모든 맵의 설치형 인형을 조회합니다."""
    path = session_events(session)
    active, observations = {}, []
    placed_targets, visible_placed, last_placed_hit = {}, set(), {}
    placed_sprites = {0x400B, 0x400C, 0x400D, 0x40D0, 0x40D1, 0x40D2}
    inventory = {}
    increases, previous_status, self_ids = [], {}, {}
    progress, outcomes, zero_counter_examples = {}, Counter(), []
    mastery_responses, last_training_progress = [], None
    counter_replies = {}
    combat_stats, mastery, damage_profiles = {}, {}, Counter()
    previous_vitals, vitals_runs = {}, []
    vitals_run = last_recovery = None
    attack = request = strike = placement = use = None
    context = None

    def finish_strike():
        nonlocal strike, last_training_progress
        if strike is None:
            return
        damage, gain = strike['damage'], strike['gain']
        if strike['request_mode'] == (0,35):
            outcomes[damage, gain] += 1
            if damage == 0 and gain:
                zero_counter_examples.append(strike)
        if strike['starting_mastery'] is not None:
            totals = counter_replies.setdefault(strike['starting_mastery'], Counter())
            totals['hits'] += 1
            if 'first_progress' in strike:
                totals['with_counter'] += 1
            elif damage > 0:
                totals['positive_without_counter'] += 1
        if (meter := strike.get('refresh') or strike.get('first_progress')):
            last_training_progress = dict(meter, request=strike['request'], hit=strike['packet'],
                                          mastery=mastery.get(strike['epoch'], (None,None))[0])
        if all(field in strike for field in ('first_progress','status','mastery','refresh')):
            mastery_responses.append({key:strike[key] for key in
                ('request','packet','first_progress','status','mastery','ignored_header','refresh','mastery_marker') if key in strike})
        strike = None

    for row in recorded_rows(path):
        epoch, map_number, kind = row.get('epoch'), row.get('map'), row['kind']
        current = epoch, map_number
        action = row.get('recentActionId')
        if strike and (current != context or action != strike['action'] or kind in ('client_action', 'context_reset')):
            finish_strike()
        if current != context or kind == 'context_reset':
            if context is None or context[0] != epoch or kind == 'context_reset':
                inventory.clear()
            active.clear()
            context = current
            attack = request = placement = use = None
            vitals_run = last_recovery = None
        if kind in ('context_reset', 'map'):
            for key in visible_placed:
                placed_targets[key].setdefault('visibility_gap', row.get('packetId') or kind)
            visible_placed.clear()
            last_placed_hit.clear()
            placement = use = None
        if kind == 'client_action':
            attack = row if row.get('opcode') == 0x13 and row.get('length') == 3 else None
            request = attack if attack and (row.get('attackMode'),row.get('attackParam')) in ((0,0),(0,35)) else None
            use = row if row.get('opcode') == 0x1C and row.get('length') == 2 and row.get('slot') is not None else None
            placement = None
        if kind == 'inventory_add':
            inventory[row['slot']] = row
        elif kind == 'inventory_remove' and row.get('byte2') not in (9, 12):
            inventory.pop(row['slot'], None)
        if kind == 'ability' and row.get('mode') in (1,3):
            if row['mode'] == 3:
                mastery.pop(epoch,None)
            unarmed = next((skill for skill in row.get('entries', []) if skill.get('name') == '격투'), None)
            if unarmed:
                mastery[epoch] = unarmed['headerWords'][1], unarmed['flags']
                if strike and row['mode'] == 1 and 'first_progress' in strike:
                    strike['mastery'] = dict(packet=row.get('packetId'), length=row.get('length'),
                                             value=mastery[epoch][0])
                if len(words := unarmed.get('additionalWords', [])) == 2:
                    progress[epoch] = tuple(words)
        if kind == 'ability' and row.get('mode') == 2 and row.get('slot') == 1 and row.get('referenceWord'):
            value, reference = row['value'], row['referenceWord']
            if strike:
                meter = dict(packet=row.get('packetId'), length=row.get('length'), count=value, reference=reference)
                if 'first_progress' not in strike:
                    before_progress = progress.get(epoch)
                    gain = (value-before_progress[0])%reference if before_progress and before_progress[1] == reference else None
                    strike.update(gain=gain,counter_packet=row.get('packetId'),first_progress=meter)
                elif 'mastery' in strike and 'refresh' not in strike:
                    strike['refresh'] = meter
            progress[epoch] = (value, reference)
        if strike and kind == 'status' and 'first_progress' in strike and 'mastery' not in strike and row['mask'] in (0,0x40):
            strike['status'] = dict(packet=row.get('packetId'), length=row.get('length'), mask=row['mask'])
        if (strike and kind == 'frame_header' and row.get('direction') == 'S' and row.get('opcode') == 0x3F
                and 'mastery' in strike and 'refresh' not in strike):
            strike['ignored_header'] = dict(packet=row.get('packetId'), opcode=0x3F, length=row.get('plainLength'))
        if (strike and kind == 'mastery_marker' and row.get('packetId') == strike.get('ignored_header',{}).get('packet')
                and row.get('abilityPacketId') == strike.get('mastery',{}).get('packet')
                and row.get('refreshPacketId') == strike.get('refresh',{}).get('packet')):
            strike['mastery_marker'] = dict(packet=row['packetId'], words=row['words'])
        if kind == 'self_identity':
            self_ids[epoch] = row['oid']
        elif kind == 'context_snapshot' and row.get('selfId'):
            self_ids[epoch] = row['selfId']
        if kind in ('context_reset', 'equipment_add', 'equipment_remove'):
            previous_status.pop(epoch, None)
            previous_vitals.pop(epoch, None)
            combat_stats.pop(epoch,None)
        if kind == 'context_reset':
            mastery.pop(epoch,None)
        if kind == 'status' and len(stats := row.get('baseWords', [])) >= 5:
            combat_stats[epoch] = stats[0], stats[4]
        if kind == 'status' and row.get('maximumValues'):
            before = previous_status.get(epoch)
            previous_status[epoch] = row
            if map_number == map_id and before and row['maximumValues'][0] == before['maximumValues'][0]+1:
                increases.append(dict(previous=before.get('packetId'), packet=row.get('packetId'),
                                      before=before['maximumValues'][0], maximum=row['maximumValues'][0],
                                      current=(row.get('currentValues') or [None])[0],
                                      mask=row['mask'], length=row.get('length')))
        if kind == 'status' and (vitals := row.get('currentValues')):
            previous = previous_vitals.get(epoch)
            previous_vitals[epoch] = vitals
            if map_number == map_id:
                at = observed_time(row)
                if vitals_run is None:
                    vitals_run = dict(first_packet=row.get('packetId'),first_time=at,satiety=Counter(),recoveries=[])
                    vitals_runs.append(vitals_run)
                vitals_run['last_packet'] = row.get('packetId')
                vitals_run['seconds'] = round(at-vitals_run['first_time'],3)
                vitals_run['satiety'][vitals[2]] += 1
                if previous and previous[0]>0 and vitals[0]==previous[0]+1 and vitals[1:]==previous[1:] and row['mask'] in (0x20,0x60):
                    recovery = dict(packet=row.get('packetId'),mask=row['mask'],hp=vitals[0])
                    if last_recovery is not None:
                        recovery['seconds_since_previous'] = round(at-last_recovery,3)
                    vitals_run['recoveries'].append(recovery)
                    last_recovery = at
        if map_id is not None and map_number != map_id:
            continue
        if kind == 'objects':
            for obj in row['objects']:
                if obj['category'] != 0:
                    continue
                if obj['sprite'] in placed_sprites and obj['name'].endswith('수련인형'):
                    key = epoch, obj['oid']
                    if key not in placed_targets:
                        placed_targets[key] = dict(obj, map=map_number, appearance=row.get('packetId'),
                                                   appearance_length=row.get('length'), hits=0, positive_hits=0,
                                                   total_visible_damage=0, self_hits=0, damage_samples=deque(maxlen=12))
                        if (use is not None and action is not None and use.get('recentActionId') == action
                                and len(row['objects']) == 1 and 0 <= observed_time(row)-observed_time(use) <= .75):
                            placement = placed_targets[key]
                            item = inventory.get(use['slot'])
                            if item and '수련인형' in item.get('name', ''):
                                placement['placement_item'] = dict(name=item['name'], sprite=item.get('wireSprite'),
                                                                   packet=item.get('packetId'))
                    if 'death' not in placed_targets[key]:
                        visible_placed.add(key)
                    continue
                if map_id is not None and obj['sprite'] == 0x400A and obj['name'] == '수련인형' and obj['oid'] not in active:
                    target = dict(obj, appearance=row.get('packetId'), hits=0, self_hits=0, positive_self_hits=0)
                    active[obj['oid']] = target
                    observations.append(target)
        elif kind == 'direction' and placement is not None and placement['oid'] == row.get('oid') and use.get('recentActionId') == action:
            placement.update(turn=row.get('packetId'), direction=row['direction'], turn_length=row.get('length'))
        elif (kind == 'inventory_remove' and placement is not None and use.get('recentActionId') == action
              and use['slot'] == row.get('slot') and row.get('byte2') == 6 and 'turn' in placement):
            placement.update(use=use.get('packetId'), slot=use['slot'], consumed=row.get('packetId'))
            placement = None
        elif kind == 'damage_effect' and (key := (epoch, row['target'])) in visible_placed:
            target = placed_targets[key]
            if len(words := row.get('extraWords', [])) == 3:
                damage = words[1]
                target['hits'] += 1
                target['positive_hits'] += damage > 0
                target['self_hits'] += row['attacker'] == self_ids.get(epoch)
                target['total_visible_damage'] += damage
                target['damage_samples'].append(dict(packet=row.get('packetId'), damage=damage))
                target.setdefault('first_hit', row.get('packetId'))
                target.update(last_hit=row.get('packetId'), last_damage=damage)
                last_placed_hit[key] = observed_time(row)
            else:
                target['hits_without_damage_value'] = target.get('hits_without_damage_value', 0) + 1
                last_placed_hit.pop(key, None)
        elif kind in ('death','object_remove') and (key := (epoch, row['oid'])) in visible_placed:
            visible_placed.remove(key)
            target = placed_targets[key]
            last_hit_at = last_placed_hit.pop(key, None)
            if kind == 'death':
                target.update(death=row.get('packetId'), death_length=row.get('length'))
                if last_hit_at is not None and 0 <= observed_time(row)-last_hit_at <= .75 and target['last_damage'] > 0:
                    total = target['total_visible_damage']
                    target['visible_damage_at_death'] = [total - target['last_damage'], total]
                    target['visible_positive_hits_at_death'] = target['positive_hits']
                    if target.get('consumed') and 'visibility_gap' not in target and 'hits_without_damage_value' not in target:
                        # Conditional on visible damage equalling HP loss without recovery between hits.
                        target['initial_hp_if_damage_is_subtracted'] = [total - target['last_damage'] + 1, total]
            else:
                target.setdefault('visibility_gap', row.get('packetId') or kind)
        elif kind == 'damage_effect' and (target := active.get(row['target'])) is not None:
            target['hits'] += 1
            target.setdefault('first_hit', row.get('packetId'))
            target['last_hit'] = row.get('packetId')
            if row['attacker'] == self_ids.get(epoch):
                target['self_hits'] += 1
                target['positive_self_hits'] += (row.get('displayedDamage') or 0)>0
                if (attack and action is not None and attack.get('recentActionId') == action
                        and row['words'][:2] == [10,9] and (row['animation'],row['effect']) == (0,1)
                        and epoch in combat_stats and epoch in mastery and len(words := row.get('extraWords', [])) == 3):
                    damage_profiles[*combat_stats[epoch],*mastery[epoch],words[1]] += 1
                    attack = None
                if (request is not None and action is not None and request.get('recentActionId') == action
                        and row.get('words',[])[:2] == [10,9] and (row['animation'],row['effect']) == (0,1)
                        and len(words := row.get('extraWords',[])) == 3):
                    finish_strike()
                    strike = dict(action=action,epoch=epoch,request=request.get('packetId'),packet=row.get('packetId'),
                                  damage=words[1],impact_sound=words[2],gain=0,
                                  request_mode=(request['attackMode'],request['attackParam']),
                                  starting_mastery=mastery.get(epoch,(None,None))[0])
        elif kind in ('death','object_remove') and (target := active.pop(row['oid'],None)) is not None:
            target[kind] = row.get('packetId')
    finish_strike()
    for run in vitals_runs:
        run.pop('first_time')
    for target in placed_targets.values():
        target['damage_samples'] = list(target['damage_samples'])
        if 'consumed' not in target:
            target.pop('placement_item', None)
    local = []
    if map_id is not None:
        configured = training_target_baseline(read_json(ROOT / 'world-recorded.json'))
        if LIVE.is_file():
            configured.update((location(row),row) for row in read_json(LIVE).get('TrainingTargets', []))
        local = [target for key,target in sorted(configured.items()) if key[0] == map_id]
    return {'session':path.parent.name,'map':map_id,'observations':observations,'local_targets':local,
            'placed_targets':list(placed_targets.values()),
            'unarmed_damage_profiles':[dict(strength=strength,agility=agility,mastery=value,grade=grade,damage=damage,count=count)
                                       for (strength,agility,value,grade,damage),count in sorted(damage_profiles.items())],
            'maximum_hp_increases':increases,
            'training_vitals':vitals_runs,
            'mastery_responses':mastery_responses,
            'last_training_progress':last_training_progress,
            'counter_replies_by_mastery':[dict(mastery=value,hits=counts['hits'],with_counter=counts['with_counter'],
                                               positive_without_counter=counts['positive_without_counter'])
                                          for value,counts in sorted(counter_replies.items())],
            'unarmed_outcomes':[dict(damage=damage,counter_gain=gain,count=count)
                                for (damage,gain),count in outcomes.most_common()],
            'zero_damage_with_counter':zero_counter_examples}


def visible_stationary_objects(row, sprite, session):
    return {
        obj['oid']: dict(x=obj['x'], y=obj['y'], floor=obj['floor'], flags=obj['flags'], type=obj['type'],
                         appearance=row.get('packetId'), appearance_session=session)
        for obj in row['objects'] if obj['category'] == 2 and obj['sprite'] == sprite
    }


@server.tool(annotations=READ_ONLY)
def merchant_weapon_wear_observations(session: str = "latest") -> dict[str, Any]:
    """무기 교체가 없는 두 S39 사이의 고용상인 명중·피해 0과 표시 내구도를 대조합니다."""
    path = session_events(session)
    visible, hits, samples = set(), Counter(), deque(maxlen=20)
    scope = weapon = player = before = None
    for row in recorded_rows(path):
        kind = row['kind']
        location = row.get('epoch'), row.get('map')
        if scope != location or kind == 'context_reset':
            visible.clear()
            if scope is None or scope[0] != location[0] or kind == 'context_reset':
                weapon = player = before = None
                hits.clear()
            scope = location
        if kind == 'self_identity' or kind == 'context_snapshot' and row.get('selfId'):
            identity = row.get('oid') if kind == 'self_identity' else row['selfId']
            if player is not None and identity != player:
                before = None
                hits.clear()
            player = identity
        elif kind in ('equipment_add', 'equipment_remove') and row.get('slot') == 1:
            weapon = row.get('wireSprite') if kind == 'equipment_add' else None
            before = None
            hits.clear()
        elif kind == 'objects':
            visible.update(visible_stationary_objects(row,0x40C1,path.parent.name))
        elif kind == 'damage_effect' and weapon == 0x8007 and player is not None and row.get('attacker') == player:
            target, damage = row.get('target'), row.get('displayedDamage')
            if target != player and damage is not None:
                hits['merchant_positive' if target in visible and damage>0 else
                     'merchant_zero' if target in visible else 'other_positive' if damage>0 else 'other_zero'] += 1
        elif kind == 'self_look' and row.get('mode') == 0 and weapon == 0x8007:
            percent = dict(row.get('bytePairs', [])).get(1)
            if before and percent is not None and (hits['merchant_positive'] or hits['merchant_zero']):
                samples.append(dict(before=before[0],after=row.get('packetId'),percent=[before[1],percent],hits=dict(hits)))
            before = (row.get('packetId'),percent) if percent is not None else None
            hits.clear()
    return dict(session=path.parent.name,observations=list(samples))


@server.tool(annotations=READ_ONLY)
def armor_wear_observations(session: str = "latest") -> dict[str, Any]:
    """같은 연결의 두 장비창 사이에서 그대로 착용한 방어구·무기와 전투 이벤트를 대조합니다."""
    path = session_events(session)
    hits, equipment_changes, samples = Counter(), set(), deque(maxlen=25)
    incoming_damage = deque(maxlen=8)
    weapon_samples, weapon_attacks = deque(maxlen=25), Counter()
    epoch = player = before = weapon = None
    for row in recorded_rows(path):
        kind = row['kind']
        if row.get('epoch') != epoch or kind == 'context_reset':
            epoch, player, before, weapon = row.get('epoch'), None, None, None
            hits.clear()
            equipment_changes.clear()
            incoming_damage.clear()
            weapon_attacks.clear()
        if kind == 'self_identity' or kind == 'context_snapshot' and row.get('selfId'):
            identity = row.get('oid') if kind == 'self_identity' else row['selfId']
            if player is not None and identity != player:
                before = weapon = None
                hits.clear()
                equipment_changes.clear()
                incoming_damage.clear()
                weapon_attacks.clear()
            player = identity
        elif kind in ('equipment_add','equipment_remove'):
            slot = row.get('slot')
            equipment_changes.add(slot)
            if slot==1:
                weapon = (row.get('name'),row.get('wireSprite')) if kind=='equipment_add' else None
        elif kind == 'timed_spell' and row.get('id') == 22 and row.get('duration') == 11:
            hits['stun_starts'] += 1
        elif kind == 'damage_effect' and player is not None:
            damage = row.get('displayedDamage')
            if damage is None and len(words := row.get('extraWords', [])) > 1:
                damage = words[1]
            attacker, target = row.get('attacker'), row.get('target')
            if damage is not None and attacker != target:
                if target == player:
                    hits['incoming_positive' if damage>0 else 'incoming_zero'] += 1
                    if damage>0:
                        hits['incoming_displayed_damage'] += damage
                        incoming_damage.append(dict(packet=row.get('packetId'),map=row.get('map'),
                                                    effect=row.get('effect'),damage=damage))
                elif attacker == player:
                    hits['outgoing_positive' if damage>0 else 'outgoing_zero'] += 1
                    if row.get('effect')==1:
                        weapon_attacks['positive' if damage>0 else 'zero'] += 1
        elif kind == 'self_look' and row.get('mode') == 0 and 'bytePairs' in row:
            current = dict(row['bytePairs'])
            if before:
                old_weapon, new_weapon = before[2].get(1), current.get(1)
                if weapon and 1 not in equipment_changes and old_weapon is not None and new_weapon is not None and new_weapon<old_weapon:
                    weapon_samples.append(dict(before=before[0],after=row.get('packetId'),item=weapon[0],sprite=weapon[1],
                                               percent=[old_weapon,new_weapon],attacks=dict(weapon_attacks)))
                changes = [dict(slot=slot,percent=[before[2][slot],value]) for slot,value in sorted(current.items())
                           if slot != 1 and slot not in equipment_changes and before[2].get(slot,-1)>value]
                if changes:
                    unchanged = [dict(slot=slot,percent=value) for slot,value in sorted(current.items())
                                 if slot != 1 and slot not in equipment_changes and before[2].get(slot)==value]
                    samples.append(dict(before=before[0],after=row.get('packetId'),changes=changes,hits=dict(hits),
                                        unchanged=unchanged,recent_incoming_damage=list(incoming_damage),
                                        seconds=round(observed_time(row)-before[1],3)))
            before = (row.get('packetId'),observed_time(row),current)
            hits.clear()
            equipment_changes.clear()
            incoming_damage.clear()
            weapon_attacks.clear()
    return dict(session=path.parent.name,observations=list(samples),weapon_observations=list(weapon_samples))


def adjacent_capture_session(path, context):
    earlier = next((name for name in recent_sessions() if name < path.parent.name), None)
    if earlier is None:
        return None
    try:
        before, after = (read_json(capture_file(name, 'session.json')) for name in (earlier, path.parent.name))
        summary = read_json(capture_file(earlier, 'summary.json'))
        elapsed = observed_time(context) - datetime.datetime.fromisoformat(summary['updatedAt']).timestamp()
    except (OSError, ValueError, KeyError):
        return None
    if not 0 <= elapsed <= 15 or any(not before.get(k) or before.get(k) != after.get(k) for k in ('pid','sha256')):
        return None
    if any(summary.get(k) != context.get(field) or context.get(field) is None
           for k,field in (('currentMap','map'),('selfId','selfId'),('position','position'))):
        return None
    return earlier


def prior_stationary_objects(path, context, sprite):
    if (earlier := adjacent_capture_session(path, context)) is None:
        return {}
    map_id = context['map']
    visible = {}
    for row in recorded_rows(capture_file(earlier, 'events.jsonl')):
        kind = row['kind']
        if kind in ('context_reset','map'):
            visible.clear()
        elif kind == 'objects' and row.get('map') == map_id:
            visible.update(visible_stationary_objects(row,sprite,earlier))
        elif kind in ('object_remove','death'):
            visible.pop(row.get('oid'),None)
    return visible


@server.tool(annotations=READ_ONLY)
def stationary_target_observations(session: str = "latest", map_id: int = 2,
                                  sprite: Annotated[int, Field(ge=0, le=65535)] = 0x40C1,
                                  limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """고정 오브젝트 S07 분류 2와 실제로 보이는 동안의 S13 타격 및 제거를 대조합니다."""
    path = session_events(session)
    visible, struck = {}, {}
    shops = deque(maxlen=limit)
    context = request = None
    initial_snapshot = True
    for row in recorded_rows(path):
        kind = row['kind']
        scope = row.get('epoch'), row.get('map')
        if scope != context or kind == 'context_reset':
            visible.clear()
            context = scope
            request = None
        if scope[1] != map_id:
            continue
        if kind == 'context_snapshot' and initial_snapshot:
            initial_snapshot = False
            if row.get('source') == 'memory':
                visible.update(prior_stationary_objects(path,row,sprite))
        elif kind == 'objects':
            visible.update(visible_stationary_objects(row,sprite,path.parent.name))
        elif kind == 'client_action':
            request = row if row.get('opcode') in (0x43,0x39) and row.get('targetId') in visible else None
        elif kind == 'merchant_dialog' and request and request['targetId'] == row['oid'] and row['oid'] in visible:
            if request.get('recentActionId') == row.get('recentActionId') and 0 <= observed_time(row)-observed_time(request) <= 1:
                reply = dict(request=request.get('packetId'), opcode=request['opcode'], menu=request.get('menu'),
                             response=row.get('packetId'), oid=row['oid'], mode=row['mode'], flags=row['flags'])
                decoded = row
                if raw := row.get('raw'):
                    try:
                        decoded = decode(bytes.fromhex(raw))
                    except (ValueError,IndexError):
                        pass
                if 'items' in decoded:
                    reply['item_count'] = len(decoded['items'])
                    reply['quantity_counts'] = dict(Counter(item['flags'] for item in decoded['items']))
                    reply['durability_percent_counts'] = dict(Counter(
                        item['tailByte'] for item in decoded['items'] if item.get('tailByte',255)<=100))
                if 'menuCodes' in decoded:
                    reply['menu_codes'] = decoded['menuCodes']
                shops.append(reply)
                request = None
        elif kind == 'damage_effect' and (obj := visible.get(row['target'])) is not None:
            key = scope[0], row['target']
            packet = row.get('packetId')
            target = struck.setdefault(key, dict(epoch=scope[0], oid=row['target'], **obj,
                                                 hits=0, zero_damage=0, positive_damage=0,
                                                 first_hit=packet, variants=Counter(), damage_counts=Counter(),
                                                 damage_by_attacker={}, removals=0, deaths=0))
            target['hits'] += 1
            target['last_hit'] = packet
            damage = row.get('displayedDamage')
            if damage is not None:
                target['zero_damage' if damage == 0 else 'positive_damage'] += 1
                target['damage_counts'][damage] += 1
                target['damage_by_attacker'].setdefault(row['attacker'],Counter())[damage] += 1
            target['variants'][row.get('motionId'),row.get('swingSoundId'),row.get('impactSoundId')] += 1
        elif kind in ('object_remove','death') and visible.pop(row['oid'],None) is not None:
            if (target := struck.get((scope[0], row['oid']))) is not None:
                target['removals' if kind == 'object_remove' else 'deaths'] += 1
    for target in struck.values():
        by_attacker = target['damage_by_attacker']
        target['damage_by_attacker'] = [
            dict(attacker=oid, damage_counts=dict(counts), samples=counts.total())
            for oid,counts in sorted(by_attacker.items(),key=lambda item:-item[1].total())[:limit]
        ]
        target['damage_counts'] = dict(target['damage_counts'])
        target['variants'] = [dict(motion=motion, swing=swing, impact=impact, count=count)
                              for (motion,swing,impact), count in target['variants'].most_common()]
    return dict(session=path.parent.name,map=map_id,sprite=sprite,targets=len(struck),
                attacks=sum(row['hits'] for row in struck.values()),observations=list(struck.values())[-limit:],
                shop_replies=list(shops))


@server.tool(annotations=READ_ONLY)
def monster_loot_observations(session: str = "latest", monster: str | None = None) -> dict[str, Any]:
    """몬스터별 드랍과 직접 처치가 확인된 무드랍 근거를 조회합니다."""
    path = session_events(session)
    tracker, confirmation = DropObservations(), LootConfirmation()
    matched, confirmed, empty = [], Counter(), []
    for row in recorded_rows(path):
        sample = tracker.ingest(row)
        if sample and sample['name'] != '나무상자' and (monster is None or sample['name'] == monster) and sample['hit'] and sample['nearby_drops']:
            matched.append(sample)
        certain = confirmation.ingest(row,sample)
        if certain and (monster is None or certain['name'] == monster):
            confirmed[certain['confirmation']] += 1
            if not certain['nearby_drops']:
                empty.append(certain)
    live = read_json(LIVE).get('MonsterLoot', []) if LIVE.is_file() else []
    recipes = [{**rule,'Samples':rule['Samples'][-3:],'SampleCount':len(rule['Samples']),
                'ConfirmedSamples':rule.get('ConfirmedSamples', [])[-3:],
                'ConfirmedSampleCount':len(rule.get('ConfirmedSamples', []))}
               for rule in live if monster is None or rule['Name'] == monster]
    return {'session':path.parent.name,'observed_count':len(matched),'observations':matched[-30:],
            'confirmed_kills':sum(confirmed.values()),'confirmation_methods':dict(confirmed),
            'confirmed_no_drops':len(empty),'no_drop_observations':empty[-30:],
            'local_recipes':recipes}


@server.tool(annotations=READ_ONLY)
def monster_engagement_observations(session: str = "latest", monster: str | None = None,
                                   limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """같은 개체의 S07 출현, S0C 접근, S13 근접·주문 피격과 직전 S08을 연결합니다."""
    path = session_events(session)
    samples, counts, active = deque(maxlen=limit), Counter(), {}
    recent_status = deque(maxlen=2)
    context, self_id, position = (None, None), None, None

    def tile(row):
        return row['x'], row['y'], row['floor']

    def distance(at):
        if position is not None and position[2] == at[2]:
            return max(abs(position[0] - at[0]), abs(position[1] - at[1]))

    for row in recorded_rows(path):
        kind, current = row['kind'], (row.get('epoch'), row.get('map'))
        if current != context or kind == 'context_reset':
            if current[0] != context[0] or kind == 'context_reset':
                self_id = None
            context, position = current, None
            active.clear()
            recent_status.clear()
        packet_id = row.get('packetId')
        if kind == 'context_snapshot':
            self_id, position = row.get('selfId'), tile(row['position']) if row.get('position') else None
        elif kind == 'self_identity':
            self_id = row['oid']
        elif kind in ('self_position', 'self_move'):
            position = tile(row)
        elif kind == 'status':
            recent_status.append(row)
        elif kind == 'objects':
            for obj in row['objects']:
                name, sprite = obj['name'], obj['sprite']
                if obj['category'] != 0 or MONSTER_SPRITES.get(name) != sprite or monster not in (None, name):
                    continue
                state = active.setdefault(obj['oid'], dict(name=name, sprite=sprite, appearance=packet_id,
                    prior_hit=None, moves=deque(maxlen=12), reported=False))
                state['tile'] = tile(obj)
        elif kind in ('object_remove', 'death'):
            active.pop(row['oid'], None)
        elif kind == 'object_move' and (state := active.get(row['oid'])) is not None and row['direction'] < len(DELTAS):
            x, y, floor = tile(row)
            dx, dy = DELTAS[row['direction']]
            state['tile'] = (x+dx, y+dy, floor)
            before, after = distance((x,y,floor)), distance(state['tile'])
            if before is not None and 1 < before and after < before and row['animation'] == 0:
                state['moves'].append(dict(packet=packet_id, time=row['time'], before=before, after=after))
        elif kind == 'damage_effect' and row['attacker'] != row['target'] and len(words := row.get('extraWords', [])) >= 2 and words[1] > 0:
            if (state := active.get(row['target'])) is not None:
                state.update(prior_hit=packet_id, player_hit=row['attacker'] == self_id, reported=False)
                state['moves'].clear()
            state = active.get(row['attacker'])
            if state is None or row['target'] != self_id:
                continue
            strike_distance = distance(state['tile'])
            details = {}
            if row['effect'] == 9 and row['words'][3]:
                mode = 'spell_effect'
                struck = datetime.datetime.fromisoformat(row['time'])
                status = [s for s in recent_status
                          if 0 <= (struck - datetime.datetime.fromisoformat(s['time'])).total_seconds() <= .25]
                details = dict(distance=strike_distance, displayed_damage=words[1], animation=row['animation'],
                    effect=row['effect'], words=row['words'], extra_words=words,
                    status=[{key:s[key] for key in ('packetId','mask','currentValues','magicDefense') if key in s} for s in status])
            elif row['effect'] != 0 or strike_distance != 1 or state['reported']:
                continue
            elif state['prior_hit'] is None:
                mode = 'no_prior_observed_hit'
            elif state['player_hit'] and state['moves']:
                mode = 'approach_after_player_hit'
            else:
                continue
            counts[mode] += 1
            samples.append(dict(name=state['name'], sprite=state['sprite'], map=current[1], mode=mode,
                appearance=state['appearance'], prior_hit=state['prior_hit'], steps=list(state['moves']), strike=packet_id, **details))
            if mode == 'spell_effect':
                state['moves'].clear()
            else:
                state['reported'] = True
    return dict(session=path.parent.name, counts=dict(counts), observations=list(samples))


@server.tool(annotations=READ_ONLY)
def monster_auxiliary_observations(session: str = "latest",
                                   limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """몬스터 피격의 S08 네 번째 현재치와 정수 계산 결과를 대조합니다."""
    path = session_events(session)
    recent = deque(maxlen=limit)
    scope = (None,None)
    player = maximum = previous = current = base = None
    counts = Counter()
    for row in recorded_rows(path):
        kind, context = row['kind'],(row.get('epoch'),row.get('map'))
        if context != scope or kind == 'context_reset':
            if context[0] != scope[0] or kind == 'context_reset':
                player = maximum = None
            scope = context
            previous = current = base = None
        if kind in ('context_snapshot','self_identity'):
            player = row.get('selfId',row.get('oid'))
        elif kind == 'status':
            if row.get('maximumValues'):
                maximum = row['maximumValues'][0]
            if row.get('currentValues'):
                previous,current = current,row
            if row.get('mask')==0x40:
                base = row
        elif (kind == 'damage_effect' and player is not None and row.get('target')==player and
              row.get('effect') in (0,9) and maximum and previous and current):
            strike = row['packetId']
            if row['effect']==9:
                if not base or not adjacent_packets(current['packetId'],base['packetId']) or not adjacent_packets(base['packetId'],strike):
                    continue
            elif not adjacent_packets(current['packetId'],strike):
                continue
            before,after = previous['currentValues'],current['currentValues']
            words = row.get('extraWords',[])
            if len(words)<2 or words[1]<=0 or after[0]<=0 or before[0]-after[0]!=words[1] or before[0]>maximum:
                continue
            if before[3]>=1000:
                continue
            expected = min(1000,before[3]+(maximum-before[0])*words[1]//max(1,maximum//10))
            mode = 'melee' if row['effect']==0 else 'spell'
            counts[mode,expected==after[3]]+=1
            recent.append(dict(mode=mode, map=scope[1], before=previous['packetId'], vitals=current['packetId'],
                               strike=strike, hp_before=before[0], maximum_hp=maximum, damage=words[1],
                               auxiliary_before=before[3], auxiliary_after=after[3], expected=expected))
    return dict(session=path.parent.name, counts=[
        dict(mode=mode,matched=counts[mode,True],different=counts[mode,False])
        for mode in sorted({mode for mode,_ in counts})
    ],observations=list(recent))


@server.tool(annotations=READ_ONLY)
def guild_npc_retaliation_observations(session: str = "latest",
                                     limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """길드만들기 NPC 타격 뒤 HP 응답, 효과 8, 효과음 58과 시간 간격을 조회합니다."""
    path = session_events(session)
    recent = deque(maxlen=limit)
    scope = (None,None)
    visible = set()
    player = first_hit = last_hit = previous = current = effect = last_reaction = None
    count = 0
    for row in recorded_rows(path):
        kind, context = row['kind'],(row.get('epoch'),row.get('map'))
        if context != scope or kind == 'context_reset':
            if context[0] != scope[0] or kind == 'context_reset':
                player = previous = current = None
            scope = context
            first_hit = last_hit = effect = last_reaction = None
            visible.clear()
        if kind in ('context_snapshot','self_identity'):
            player = row.get('selfId',row.get('oid'))
        elif kind == 'objects' and context[1] is not None and context[1]%10000==2:
            visible.update(o['oid'] for o in row['objects']
                           if (o.get('category'),o.get('sprite'),o.get('name'))==(1,0x4035,'길드만들기'))
        elif kind in ('object_remove','death'):
            visible.discard(row.get('oid'))
        elif kind == 'damage_effect' and player is not None and row.get('attacker')==player and row.get('target') in visible:
            if len(words := row.get('extraWords',[]))>=2 and words[1]>0:
                last_hit = row
                if first_hit is None:
                    first_hit = row
        elif kind == 'status' and row.get('currentValues'):
            previous, current = current,row
        elif kind == 'effect' and last_hit and current and row.get('oid')==player and row.get('effect')==8:
            if adjacent_packets(current['packetId'],row['packetId']) and row.get('raw','')[14:18]=='0064':
                effect = row
        elif kind == 'sound' and effect and row.get('id')==58 and adjacent_packets(effect['packetId'],row['packetId']):
            now = observed_time(current)
            observed = dict(map=scope[1], first_positive_hit=first_hit['packetId'], last_positive_hit=last_hit['packetId'],
                            vitals=current['packetId'], mask=current['mask'], hp=current['currentValues'][0],
                            auxiliary=current['currentValues'][3], effect=effect['packetId'],
                            effect_length=effect['length'], sound=row['packetId'])
            if last_reaction is not None:
                observed['interval_ms'] = round((now-last_reaction)*1000)
            else:
                observed['first_hit_delay_ms'] = round((now-observed_time(first_hit))*1000)
            if previous:
                observed['hp_change'] = current['currentValues'][0]-previous['currentValues'][0]
                observed['auxiliary_change'] = current['currentValues'][3]-previous['currentValues'][3]
            recent.append(observed)
            count += 1
            last_reaction = now
            effect = None
    return dict(session=path.parent.name,count=count,observations=list(recent))


@server.tool(annotations=READ_ONLY)
def monster_attack_cadence_observations(session: str = "latest", monster: str | None = None,
                                       limit: Annotated[int, Field(ge=1, le=100)] = 30,
                                       include_peers: bool = False) -> dict[str, Any]:
    """양쪽이 움직이지 않은 첫 반격과 연속 근접 타격, 추격 뒤 첫 타격의 간격을 비교합니다."""
    path = session_events(session)
    samples = deque(maxlen=limit)
    groups, visible, peers = {}, {}, {}
    scope = (None,None)
    self_id = position = None

    def tile(row):
        return row['x'],row['y'],row['floor']

    def adjacent(a,b):
        return a is not None and a[2] == b[2] and abs(a[0]-b[0])+abs(a[1]-b[1]) == 1

    def record(name, sample_type, before, after, map_id, target_source, target_position_packet):
        milliseconds = round((after[0]-before[0])*1000)
        lower = 0 if sample_type == 'after_provocation' else 350
        upper = 12_000 if sample_type == 'stationary' else 5_000
        if not lower <= milliseconds <= upper:
            return
        values = groups.setdefault((name,sample_type,target_source),[])
        values.append(milliseconds)
        sample = dict(monster=name, kind=sample_type, map=map_id, before=before[1],
                      strike=after[1], milliseconds=milliseconds, target_source=target_source)
        if target_position_packet is not None:
            sample['target_position_packet'] = target_position_packet
        samples.append(sample)

    for row in recorded_rows(path):
        kind, context = row['kind'],(row.get('epoch'),row.get('map'))
        if scope != context or kind == 'context_reset':
            if context[0] != scope[0] or kind == 'context_reset':
                self_id = None
            scope, position = context,None
            visible.clear()
            peers.clear()
        if kind == 'context_snapshot':
            self_id = row.get('selfId')
            if row.get('position'):
                position = tile(row['position'])
        elif kind == 'self_identity':
            self_id = row.get('oid')
        elif kind in ('self_move','self_position'):
            at, position = position, tile(row)
            for state in visible.values():
                if state['strike'] and state['strike'][2] == self_id:
                    state['strike'] = None
                if at != position and state['provocation'] and state['provocation'][2] == self_id:
                    state['provocation'] = None
                state['step'] = None
        elif kind == 'human' and include_peers:
            oid, at = row['oid'],tile(row)
            if oid != self_id and (oid not in peers or peers[oid][0] != at):
                peers[oid] = at,row.get('packetId')
        elif kind == 'objects':
            for obj in row['objects']:
                name = obj.get('name')
                if obj['category'] != 0 or MONSTER_SPRITES.get(name) != obj['sprite'] or monster not in (None,name):
                    continue
                at = tile(obj)
                state = visible.get(obj['oid'])
                if not state:
                    visible[obj['oid']] = dict(name=name,tile=at,strike=None,step=None,provocation=None,engaged=False)
                elif state['tile'] != at:
                    state.update(tile=at,strike=None,step=None,provocation=None)
        elif kind in ('death','object_remove'):
            visible.pop(row.get('oid'),None)
            peers.pop(row.get('oid'),None)
        elif kind == 'object_move' and row['direction'] < len(DELTAS):
            x,y,floor = tile(row)
            dx,dy = DELTAS[row['direction']]
            at = x+dx,y+dy,floor
            oid = row['oid']
            if oid in peers:
                peers[oid] = at,row['packetId']
            state = visible.get(oid)
            if state is None:
                continue
            state.update(tile=at,strike=None,step=None,provocation=None)
            if position and position[2] == floor and row.get('animation') == 0:
                before = abs(position[0]-x)+abs(position[1]-y)
                after = abs(position[0]-x-dx)+abs(position[1]-y-dy)
                if before > after == 1:
                    state['step'] = observed_time(row),row['packetId']
        elif kind == 'damage_effect' and row.get('effect') == 1 and (state := visible.get(row.get('target'))):
            source = row.get('attacker')
            damage = row.get('displayedDamage')
            if damage is None and len(words := row.get('extraWords', [])) == 3:
                damage = words[1]
            if not damage:
                continue
            if not state['engaged']:
                own = self_id is not None and source == self_id
                at, revision = (position,None) if own else peers.get(source,(None,None))
                if adjacent(at,state['tile']):
                    state['provocation'] = observed_time(row),row['packetId'],source,revision
            elif state['provocation'] and state['provocation'][2] != source:
                state['provocation'] = None
            state['engaged'] = True
        elif kind == 'damage_effect' and row.get('effect') == 0 and (state := visible.get(row.get('attacker'))):
            target = row.get('target')
            own = self_id is not None and target == self_id
            at, revision = (position,None) if own else peers.get(target,(None,None))
            provocation = state['provocation']
            state.update(engaged=True,provocation=None)
            if not adjacent(at,state['tile']):
                state['strike'] = state['step'] = None
                continue
            target_source = 'recorder' if own else 'peer'
            strike = observed_time(row),row['packetId'],target,revision
            if provocation and provocation[2:] == strike[2:]:
                record(state['name'],'after_provocation',provocation,strike,context[1],target_source,revision)
            if state['strike'] and state['strike'][2:] == strike[2:]:
                record(state['name'],'stationary',state['strike'],strike,context[1],target_source,revision)
            elif own and state['step']:
                record(state['name'],'after_step',state['step'],strike,context[1],target_source,revision)
            state.update(strike=strike,step=None)
    return dict(session=path.parent.name,cadence=[
        dict(monster=name,kind=kind,target_source=target,count=len(values),median_ms=round(median(values)),
             minimum_ms=min(values),maximum_ms=max(values))
        for (name,kind,target),values in sorted(groups.items())
    ],observations=list(samples))


@server.tool(annotations=READ_ONLY)
def monster_melee_observations(session: str = "latest", monster: str | None = None, armor: int | None = None,
                               limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """몬스터 근접 공격의 S1A, S08, S13과 물리방어력별 피해 표본을 조회합니다."""
    path = session_events(session)
    field, melee = DropObservations(), MeleeObservations()
    counts, recent = Counter(), deque(maxlen=limit)

    def matches(row):
        return (monster is None or row['Name'] == monster) and (armor is None or row['Armor'] == armor)

    for row in recorded_rows(path):
        field.ingest(row)
        if (sample := melee.ingest(row,field.active)) and matches(sample):
            counts[sample['Name'],sample['Armor'],sample['Damage']] += 1
            recent.append(sample)
    live = read_json(LIVE).get('MonsterMelee', []) if LIVE.is_file() else []
    return dict(session=path.parent.name, observations=list(recent),
        counts=[dict(name=name, armor=defense, damage=damage, samples=count)
                for (name,defense,damage),count in counts.most_common()],
        published_outcomes=[{**rule,'Samples':rule['Samples'][-3:],'SampleCount':len(rule['Samples'])}
                            for rule in live if matches(rule)])


@server.tool(annotations=READ_ONLY)
def monster_unarmed_training_observations(session: str = "latest", monster: str | None = None,
                                          limit: Annotated[int, Field(ge=1, le=100)] = 30,
                                          ability: Annotated[str, Field(pattern="^(격투|대검)$")] = "격투",
                                          include_zero_damage: bool = False,
                                          player_strength: int | None = None) -> dict[str, Any]:
    """자기 격투·대검의 S13과 S2C 카운트를 대조합니다. 피해 0 타격도 C13과 연결해 조회할 수 있습니다."""
    path = session_events(session)
    profiles = read_json(ROOT / "world-recorded.json")["MonsterHealth"]
    field = DropObservations({"콤보풍선(Lv100)": 0x40F4})
    recent, boundaries = deque(maxlen=limit), deque(maxlen=limit)
    counts = Counter()
    phase_counts, hit_outcomes = Counter(), Counter()
    combo_counts = Counter()
    unarmed = ability == "격투"
    epoch = scope = self_id = strength = weapon = None
    ability_slot = request = None
    meter = {}
    before = {}
    at_hit = at_death = at_combo = first_hit_progress = None
    hit_time = death_time = None
    updates, hits, combos, deaths = [], [], [], set()

    def counter_delta(first, last):
        if not first or not last or not (reference := first.get("reference")) or last.get("reference") != reference:
            return None
        if not all("mastery" in state and "counter" in state for state in (first, last)):
            return None
        return (last["mastery"] - first["mastery"]) * reference + last["counter"] - first["counter"]

    def finish_action():
        if len(hits) != 1:
            return
        hit = hits[0]
        combo = combos[0] if len(combos) == 1 and hit["target"] in deaths else None
        sample = {**hit, "before": before, "after": dict(meter), "ability_packets": list(updates),
                  "combo": combo, "killed": hit["target"] in deaths}
        isolated_target = deaths.issubset((hit["target"],))
        if isolated_target and at_hit is not None and (at_death is not None or not combos):
            after_hit = at_death if at_death is not None else meter
            if (gain := counter_delta(at_hit, after_hit)) is not None and gain >= 0:
                sample.update(counter_at_hit=at_hit, counter_after_hit=dict(after_hit), hit_counter_gain=gain)
                phase_counts["hit", at_hit["reference"], gain] += 1
                hit_outcomes[hit["damage"], gain] += 1
        elif not isolated_target and at_hit and first_hit_progress and hit["request"]:
            # An unrelated death may follow the first immediate S2C. Keep that
            # packet separate from later counter, mastery and combo traffic.
            value, reference, packet = first_hit_progress
            if at_hit.get("reference") == reference and "counter" in at_hit:
                gain = (value - at_hit["counter"]) % reference
                sample.update(immediate_counter_gain=gain, immediate_counter_packet=packet)
                hit_outcomes[hit["damage"], gain] += 1
        if isolated_target and combo is not None and (gain := counter_delta(at_death, at_combo)) is not None and gain >= 0:
            sample["combo_counter_gain"] = gain
            sample["counter_at_death"] = at_death
            sample["counter_at_combo"] = at_combo
            phase_counts["combo", at_death["reference"], gain] += 1
            combo_counts[combo["count"], combo["bonus_counter"], gain] += 1
        counts[hit["monster"], before.get("mastery"), hit["strength"], hit["damage"]] += 1
        recent.append(sample)
        maximum = combo["monster_level"] if combo else profiles.get(hit["monster"], {}).get("MaxLevel", 0)
        if sample["killed"] and maximum and before.get("mastery", -1) >= 100 * maximum - (100 if combo else 0):
            boundaries.append({**sample, "level_bound": maximum, "exact_monster_level": combo is not None})

    for row in recorded_rows(path):
        kind, current_epoch = row["kind"], row.get("epoch")
        current_scope = current_epoch, row.get("map"), row.get("recentActionId")
        # A map checksum reply can be queued between the hit and the kill response.
        if (scope is not None and current_scope[:2] == scope[:2] and kind == "client_action"
                and row.get("opcode") == 0x35 and len(hits) == 1 and not combos
                and 0 <= observed_time(row) - hit_time <= .25):
            scope = current_scope
        if current_scope != scope or kind == "context_reset":
            finish_action()
            hits.clear()
            updates.clear()
            combos.clear()
            deaths.clear()
            at_hit = at_death = at_combo = first_hit_progress = None
            hit_time = death_time = None
            request = None
            scope = current_scope
            if current_epoch != epoch or kind == "context_reset":
                epoch = current_epoch
                self_id = strength = weapon = ability_slot = None
                meter.clear()
            before = dict(meter)
        target = field.active.get(row.get("target")) if field.context == current_scope[:2] else None
        field.ingest(row)
        packet_id = row.get("packetId")
        if kind == "context_snapshot":
            self_id = row.get("selfId")
        elif kind == "self_identity":
            self_id = row["oid"]
        elif kind == "status" and row.get("baseWords"):
            strength = row["baseWords"][0]
        elif kind == "client_action" and row.get("opcode") == 0x13 and row.get("length") == 3:
            request = row
        elif kind == "equipment_add" and row["slot"] == 1:
            weapon = row["name"]
        elif kind == "equipment_remove" and row["slot"] == 1:
            weapon = ""
        elif kind == "ability":
            if row["mode"] == 3:
                ability_slot = None
                meter.clear()
            for entry in row.get("entries", []):
                if entry["name"] == ability:
                    ability_slot = entry["slot"]
                    meter["mastery"] = entry["headerWords"][1]
                    if words := entry.get("additionalWords"):
                        meter.update(counter=words[0], reference=words[1])
                    if row["mode"] == 1:
                        updates.append(dict(packet=packet_id, mode=1, mastery=meter["mastery"]))
            if row["mode"] == 2 and row["slot"] == ability_slot:
                meter.update(counter=row["value"], reference=row["referenceWord"])
                updates.append(dict(packet=packet_id, mode=2, counter=row["value"], reference=row["referenceWord"]))
                if (first_hit_progress is None and len(hits) == 1 and not deaths and row["referenceWord"]
                        and 0 <= observed_time(row) - hit_time <= .25):
                    first_hit_progress = row["value"], row["referenceWord"], packet_id
        elif kind == "damage_effect" and scope[2] is not None and row["attacker"] == self_id and target:
            if (row["animation"], row["effect"]) != (0, 1):
                continue
            motion = row["words"][:2]
            if unarmed and (weapon not in ("", None) or (weapon is None and motion != [10, 9])):
                continue
            if not unarmed and (weapon not in ("바스타드소드", "그레이트소드") or motion not in ([15, 9], [16, 9])):
                continue
            if (monster not in (None, target["name"]) or player_strength not in (None, strength)
                    or len(words := row.get("extraWords", [])) < 3):
                continue
            if words[1] == 0 and (not include_zero_damage or request is None or request.get("recentActionId") != scope[2]
                                  or words[2] != 0xFFFF or not 0 <= observed_time(row) - observed_time(request) <= .75):
                continue
            at_hit = dict(meter)
            hit_time = observed_time(row)
            sample = dict(monster=target["name"], target=target["oid"], map=scope[1], packet=packet_id,
                          damage=words[1], strength=strength, request=request.get("packetId") if request else None,
                          motion_frames=motion[0], impact_sound=words[2])
            if unarmed:
                sample["unarmed_confirmed"] = weapon == ""
            else:
                sample["weapon"] = weapon
            hits.append(sample)
        elif kind == "death":
            deaths.add(row["oid"])
            if len(hits) == 1 and row["oid"] == hits[0]["target"] and at_death is None:
                when = observed_time(row)
                if 0 <= when - hit_time <= .25:
                    at_death, death_time = dict(meter), when
        elif kind == "combo":
            combos.append(dict(packet=packet_id, count=row["count"], bonus_counter=row["bonusCounter"],
                               skill_level=row["skillLevel"], monster_level=row["monsterLevel"],
                               seconds_left=row["secondsLeft"], seconds_limit=row["secondsLimit"]))
            if death_time is not None and 0 <= observed_time(row) - death_time <= .25:
                at_combo = dict(meter)
    finish_action()
    return dict(session=path.parent.name, ability=ability, observations=list(recent), near_or_above_cap=list(boundaries),
                hit_counter_outcomes=[dict(damage=damage, gained=gain, samples=n)
                                      for (damage, gain), n in sorted(hit_outcomes.items())],
                combo_counter_counts=[dict(combo=count, displayed=display, gained=gain, samples=n)
                                      for (count, display, gain), n in sorted(combo_counts.items())],
                counter_phase_counts=[dict(phase=phase, reference=reference, gain=gain, samples=count)
                                      for (phase, reference, gain), count in sorted(phase_counts.items())],
                local_profiles={name: {key: data.get(key) for key in ("Defense", "MinLevel", "MaxLevel", "Source")}
                                for name, data in profiles.items() if monster == name or
                                (monster is None and any(name == key[0] for key in counts))},
                damage_counts=[dict(monster=name, mastery=mastery, strength=strength, damage=damage, samples=n)
                               for (name, mastery, strength, damage), n in counts.most_common(limit)])


@server.tool(annotations=READ_ONLY)
def unarmed_mastery_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=50)] = 20) -> dict[str, Any]:
    """격투 명중 뒤 카운트, 상태, 안내, 자기 효과와 숙련도 갱신을 대조합니다."""
    path = session_events(session)
    updates = deque(maxlen=limit)
    self_id = scope = hit = None
    meters, statuses, notices, effects = [], [], [], []
    mastery = None
    for row in recorded_rows(path):
        kind = row['kind']
        context = row.get('epoch'), row.get('map')
        if context!=scope or kind=='context_reset':
            if scope is None or context[0]!=scope[0] or kind=='context_reset':
                self_id = None
            hit = None
            scope = context
        if kind in ('context_snapshot','self_identity'):
            self_id = row.get('selfId') if kind=='context_snapshot' else row.get('oid')
        if kind=='damage_effect' and self_id is not None and row.get('attacker')==self_id:
            hit = row if row.get('animation')==0 and row.get('words',[])[:2]==[10,9] and len(row.get('extraWords',[]))>=2 and row['extraWords'][1]>0 else None
            meters, statuses, notices, effects = [], [], [], []
            mastery = None
        elif hit and (row.get('recentActionId')!=hit.get('recentActionId') or observed_time(row)-observed_time(hit)>.25):
            hit = None
        if not hit:
            continue
        packet = row.get('packetId')
        if kind=='status':
            status = dict(packet=packet,mask=row['mask'],length=row['length'])
            if maxima := row.get('maximumValues'):
                status['maximum_hp'] = maxima[0]
            statuses.append(status)
        elif kind=='frame_header' and row.get('direction')=='S' and row.get('opcode')==0x0A:
            notices.append(dict(packet=packet,mode=row.get('messageMode'),length=row['plainLength']))
        elif kind=='effect' and row.get('oid')==self_id:
            effects.append(dict(packet=packet,effect=row['effect'],parameter=row['parameter'],length=row['length']))
        elif kind=='ability' and row.get('mode')==1:
            skill = next((entry for entry in row.get('entries',[]) if entry['name']=='격투'),None)
            if skill:
                mastery = dict(packet=packet,value=skill['headerWords'][1],slot=skill['slot'],length=row['length'])
        elif kind=='ability' and row.get('mode')==2:
            meter = dict(packet=packet,slot=row['slot'],counter=row['value'],reference=row['referenceWord'],
                         length=row.get('length'),padding=row.get('trailingBytes'))
            if not mastery:
                meters.append(meter)
            elif mastery['slot']==meter['slot']:
                before = next((prior for prior in reversed(meters) if prior['slot']==meter['slot']),None)
                if before:
                    updates.append(dict(hit=hit['packetId'],map=context[1],before=before,mastery=mastery,
                                        after=meter,statuses=statuses,notices=notices,effects=effects))
                hit = None
    return dict(session=path.parent.name,updates=list(updates))


@server.tool(annotations=READ_ONLY)
def player_spell_observations(session: str = "latest", name: str | None = None,
                              limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """S17 주문의 시전 지연·수련 카운트와 C0F·C56·S52·S13·최대 MP 갱신을 대조합니다."""
    path = session_events(session)
    lists, progress, requests, states, effects = (deque(maxlen=limit) for _ in range(5))
    slots, self_ids, counts = {}, {}, Counter()
    latest_casts, maximum_status = {}, {}
    mana_increases = deque(maxlen=limit)
    for row in recorded_rows(path):
        epoch, kind = row.get('epoch'), row['kind']
        if kind=='context_reset':
            slots.pop(epoch,None)
            self_ids.pop(epoch,None)
            latest_casts.pop(epoch,None)
            maximum_status.pop(epoch,None)
        if kind == 'status' and (maximum := row.get('maximumValues')):
            previous = maximum_status.get(epoch)
            maximum_status[epoch] = row
            cast = latest_casts.get(epoch)
            if (previous and cast and previous['map'] == cast['map'] == row['map']
                    and cast.get('recentActionId') is not None and cast['recentActionId'] == row.get('recentActionId')
                    and 0 <= observed_time(row) - observed_time(cast) <= 2):
                old = previous['maximumValues']
                label = slots.get(epoch, {}).get(cast.get('slot'))
                if name in (None,label) and maximum[1] == old[1] + 1 and maximum[0] == old[0] and maximum[2:] == old[2:]:
                    counts['maximum_mp_increase'] += 1
                    mana_increases.append(dict(spell=label, map=row['map'], request=cast.get('packetId'),
                        before_status=previous.get('packetId'), status=row.get('packetId'), mask=row.get('mask'),
                        before_max_mp=old[1], max_mp=maximum[1], current_mp=(row.get('currentValues') or [None,None])[1]))
        if kind in ('context_snapshot', 'self_identity'):
            self_ids[epoch] = row.get('selfId') if kind == 'context_snapshot' else row.get('oid')
        if kind == 'spell_remove':
            slots.get(epoch, {}).pop(row['slot'], None)
        if row.get('opcode') == 0x17 and (kind=='spell' or row.get('raw')):
            try:
                spell = row if kind == 'spell' else decode(bytes.fromhex(row['raw']))
            except (ValueError, IndexError):
                continue
            if spell.get('kind') != 'spell':
                continue
            active = slots.setdefault(epoch, {})
            if spell['mode']==2:
                slot=spell['slot']
                label=active.get(slot)
                if name in (None,label):
                    sample=dict(packetId=row.get('packetId'),map=row.get('map'),slot=slot,spell=label,
                                counter=spell['word1'],counter_reference=spell['word2'])
                    previous=latest_casts.get(epoch)
                    if (previous and previous.get('recentActionId') is not None
                            and previous['recentActionId']==row.get('recentActionId')
                            and (previous.get('map'),previous.get('slot'))==(row.get('map'),slot)):
                        sample['request_packet']=previous.get('packetId')
                    progress.append(sample)
                    counts['S17_02']+=1
            if spell['mode'] == 3:
                active.clear()
            entries = spell.get('entries', [])
            active.update((entry['slot'], entry['name']) for entry in entries)
            selected = []
            for entry in entries:
                if name not in (None,entry['name']):
                    continue
                named=dict(entry,cast_delay_ms=entry['word2'])
                if len(entry.get('additionalWords',[]))==2:
                    named['counter'],named['counter_reference']=entry['additionalWords']
                selected.append(named)
            if selected:
                lists.append(dict(packetId=row.get('packetId'), map=row.get('map'),
                                  mode=spell['mode'], entries=selected))
        if kind == 'client_action' and row.get('opcode') in (0x0F, 0x56):
            if row['opcode']==0x0F:
                latest_casts[epoch]=row
            label = slots.get(epoch, {}).get(row.get('slot'))
            if name is None or label in (None, name):
                counts[f"C{row['opcode']:02X}"] += 1
                requests.append(dict(spell=label, **{k:row[k] for k in (
                    'packetId','time','opcode','map','slot','targetId','spellTargetFlag','mode') if k in row}))
        if not self_ids.get(epoch):
            continue
        if kind == 'cast_spell' and row.get('oid') == self_ids[epoch]:
            counts['self_S52'] += 1
            states.append({k:row[k] for k in ('packetId','time','map','mode','recentActionId') if k in row})
        elif kind == 'damage_effect' and row.get('attacker') == self_ids[epoch] and row.get('animation') == 2:
            counts['self_spell_S13'] += 1
            effects.append({k:row[k] for k in (
                'packetId','time','map','target','effect','words','displayedDamage','recentActionId') if k in row})
    return dict(session=path.parent.name, spell_lists=list(lists), spell_progress=list(progress), requests=list(requests),
                self_cast_states=list(states), self_spell_hits=list(effects),
                maximum_mp_increases=list(mana_increases), counts=dict(counts))


@server.tool(annotations=READ_ONLY)
def monster_spell_damage_observations(session: str = "latest", magic_defense: int | None = None,
                                      limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """오크 셔먼 주문의 S08·S08·S13 순서와 마법방어력별 피해를 조회합니다."""
    path = session_events(session)
    field, spell = DropObservations(), SpellDamageObservations()
    counts, recent = Counter(), deque(maxlen=limit)
    for row in recorded_rows(path):
        field.ingest(row)
        if (sample := spell.ingest(row,field.active)) and magic_defense in (None,sample['MagicDefense']):
            counts[sample['MagicDefense'],sample['Damage']] += 1
            recent.append(sample)
    live = read_json(LIVE).get('MonsterSpellDamage', []) if LIVE.is_file() else []
    return dict(session=path.parent.name, observations=list(recent),
        counts=[dict(magic_defense=defense,damage=damage,samples=count) for (defense,damage),count in sorted(counts.items())],
        published_outcomes=[{**rule,'Samples':rule['Samples'][-3:],'SampleCount':len(rule['Samples'])}
                            for rule in live if magic_defense in (None,rule['MagicDefense'])])


@server.tool(annotations=READ_ONLY)
def monster_kill_experience_observations(session: str = "latest", monster: str | None = None,
                                         limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """S13·S5F·S85와 S08에서 확인된 처치 명성, 로컬 기본값과 자동 반영값을 조회합니다."""
    path = session_events(session)
    field, fame = DropObservations(), KillFameObservations()
    counts, recent = Counter(), deque(maxlen=limit)
    for row in recorded_rows(path):
        defeated = field.ingest(row)
        sample = fame.ingest(row,defeated)
        if sample and monster in (None,sample['Name']):
            gain = sample.pop('Experience')
            counts[sample['Name'],gain] += 1
            recent.append(dict(sample,Fame=gain,status_mode=row.get('state'),
                               status_mask=row.get('mask'),status_length=row.get('length')))
    live = read_json(LIVE).get('MonsterKillExperience', []) if LIVE.is_file() else []
    profiles = read_json(ROOT / 'world-recorded.json')['MonsterHealth']
    return dict(session=path.parent.name, observations=list(recent),
        counts=[dict(name=name, fame=reward, samples=count) for (name,reward),count in counts.most_common()],
        catalog_values=[dict(Name=name,Fame=profile['KillExperience']) for name,profile in profiles.items()
                        if monster in (None,name) and 'KillExperience' in profile],
        published_values=[dict(Name=rule['Name'],Sprite=rule['Sprite'],Fame=rule['Experience'],
                               Samples=rule['Samples'][-3:],SampleCount=len(rule['Samples']))
                          for rule in live if monster in (None,rule['Name'])])


@server.tool(annotations=READ_ONLY)
def currency_observations(session: str = "latest") -> dict[str, Any]:
    """인벤토리 골드코인 수량과 상태창 갈리드 잔액, 바닥 갈리드를 조회합니다."""
    path = session_events(session)
    wallets, ground, matched = [], {}, []
    last_wallet = None
    for row in recorded_rows(path):
        kind = row.get('kind')
        if kind == 'inventory_add' and row.get('wireSprite') == 0x8BB8 and row.get('name') == 'GALRID':
            last_wallet = row
            wallets.append({key: row[key] for key in ('time','map','packetId','slot','quantity','word1','word2','word3') if key in row})
        elif kind == 'status' and 'gold' in row and last_wallet and last_wallet.get('epoch') == row.get('epoch'):
            delay = (datetime.datetime.fromisoformat(row['time']) - datetime.datetime.fromisoformat(last_wallet['time'])).total_seconds()
            if 0 <= delay <= .5 and last_wallet['quantity'] == row['gold']:
                matched.append({'wallet':last_wallet.get('packetId'),'status':row.get('packetId'),'gold':row['gold']})
        elif kind == 'objects':
            for obj in row.get('objects', []):
                if obj.get('sprite') == 0x8BB8 and obj.get('category') == 3:
                    ground.setdefault((row.get('epoch'),obj['oid']), {
                        'map':row.get('map'),'packetId':row.get('packetId'),**obj})
    return {'session':path.parent.name,'wallet_updates':wallets[-24:],'matching_balances':matched[-24:],
            'ground_coins':list(ground.values())[-24:]}


def attach_shop_notice(order, responses, row):
    if row.get('orderPacketId') != order['packetId']:
        return
    for response in reversed(responses):
        if response['packet'] == row['packetId']:
            response.update(mode=row['messageMode'], window_id=row['windowId'], reset_selection=row['resetSelection'],
                            text=row['text'], message_bytes=row['messageBytes'], trailing_bytes=row['trailingBytes'])
            break


@server.tool(annotations=READ_ONLY)
def merchant_service_observations(session: str = "latest") -> dict[str, Any]:
    """S2F 판매·수리 견적과 C39 주문의 결제 및 인벤토리 응답을 조회합니다."""
    path = session_events(session)
    quotes, orders = [], []
    latest_quotes, bag_durability = {}, {}
    active = None
    last_gold = connection_dimension = None
    for row in recorded_rows(path):
        kind = row['kind']
        scope = row.get('epoch'), row.get('map')
        if kind == 'context_reset':
            last_gold = connection_dimension = None
            latest_quotes.clear()
            bag_durability.clear()
        elif kind == 'status':
            if base := row.get('baseBytes'):
                connection_dimension = base[0] >> 4
            if 'gold' in row:
                last_gold = row['gold']
        elif kind == 'inventory_add' and row.get('wireSprite') == 0x8BB8:
            last_gold = row['quantity']
        if kind=='inventory_add' and row.get('slot') is not None:
            if 'durabilityPercent' in row:
                bag_durability[row['slot']] = row
            else:
                bag_durability.pop(row['slot'],None)
        elif kind=='inventory_remove':
            bag_durability.pop(row.get('slot'),None)
        if kind in ('context_reset','client_action'):
            active = None
            if kind == 'client_action' and row.get('opcode') == 0x39 and row.get('menu') in (0x4E,0x5B):
                order = {key:row[key] for key in ('time','map','packetId','targetId','menu','length','orderLines') if key in row}
                orders.append(order)
                quote = latest_quotes.get((*scope,row.get('targetId'),row['menu']))
                if quote and row.get('length',0)>10:
                    order['quote_packet'] = quote['packetId']
                    if row['menu']==0x4E:
                        order['sale_responses'] = []
                    else:
                        order.update(repairs=[], messages=[], connection_dimension=connection_dimension, prior_gold=last_gold)
                    offers = {(o['kind'],o['slot']):o for o in quote['offers']}
                    active = order, scope, row.get('recentActionId'), offers
                    before_repair = bag_durability.copy()
                    wallet = status = None
        elif kind == 'merchant_dialog' and row.get('mode') in (11,14):
            try:
                event=decode(bytes.fromhex(row['raw']))
                offers=event['offers']
            except (ValueError,IndexError,KeyError):
                continue
            quote = dict(packetId=row.get('packetId'),map=scope[1],oid=event['oid'],mode=event['mode'],
                         merchant=event['text'],menu=event['menuCode'],offers=offers,total=sum(o['price'] for o in offers))
            quotes.append(quote)
            latest_quotes[*scope,event['oid'],event['menuCode']] = quote
            if (active and 'sale_responses' in active[0] and scope == active[1] and row.get('recentActionId') == active[2]
                    and event['oid'] == active[0]['targetId'] and event['mode'] == 11
                    and 0 <= observed_time(row) - observed_time(active[0]) <= 2):
                active[0]['sale_responses'].append(dict(kind='catalog',packet=row['packetId'],length=row['length']))
                active = None
        elif active and scope == active[1] and row.get('recentActionId') == active[2]:
            order, _, _, offers = active
            elapsed = datetime.datetime.fromisoformat(row['time']) - datetime.datetime.fromisoformat(order['time'])
            if not 0 <= elapsed.total_seconds() <= 2:
                active = None
            elif kind == 'shop_notice':
                attach_shop_notice(order, order['sale_responses'] if 'sale_responses' in order else order['messages'], row)
            elif 'sale_responses' in order:
                response = {'packet':row.get('packetId'),'length':row.get('length')}
                if kind == 'inventory_remove' and (0,row.get('slot')) in offers:
                    response.update(kind='remove',slot=row['slot'],quote=offers[0,row['slot']]['price'],
                                    reason=row.get('byte2'),weight=row.get('word'))
                elif kind == 'inventory_add' and (row.get('wireSprite') == 0x8BB8 or (0,row.get('slot')) in offers):
                    response.update(kind='wallet' if row.get('wireSprite') == 0x8BB8 else 'item',
                                    slot=row['slot'],quantity=row['quantity'],x=row.get('word1'),y=row.get('word2'),
                                    carried_weight=row.get('word3'),trailing_bytes=row.get('trailingBytes'))
                elif kind == 'status' and 'gold' in row:
                    response.update(kind='balance',gold=row['gold'],state=row.get('state'),mask=row['mask'])
                elif kind == 'frame_header' and row.get('direction')=='S' and row.get('opcode')==0x0A:
                    response.update(kind='message',length=row['plainLength'])
                if 'kind' in response:
                    order['sale_responses'].append(response)
            elif kind == 'inventory_add' and row.get('wireSprite') == 0x8BB8:
                wallet, status = row, None
            elif kind == 'status' and row.get('mask') in (0x10,0x50) and wallet and row.get('gold') == wallet.get('quantity'):
                status = row
            elif kind in ('inventory_add','equipment_add') and status and (offer := offers.pop((int(kind=='equipment_add'),row.get('slot')),None)):
                result = dict(kind=offer['kind'],slot=offer['slot'],item=offer.get('name',row.get('name')),
                    quote=offer['price'],gold=status['gold'],wallet_packet=wallet['packetId'],status_packet=status['packetId'],
                    wallet_length=wallet['length'], carried_weight=wallet.get('word3'),
                    status_length=status['length'], status_mask=status['mask'], state=status.get('state'),
                    item_packet=row['packetId'],item_length=row['length'],item_trailing_bytes=row.get('trailingBytes'))
                if kind=='inventory_add':
                    result['durability_after'] = row.get('durabilityPercent')
                    prior = before_repair.get(row['slot'])
                    if prior and (prior.get('wireSprite'),prior.get('name'))==(row.get('wireSprite'),row.get('name')):
                        result.update(durability_before=prior['durabilityPercent'],durability_before_packet=prior['packetId'])
                previous_gold = order['repairs'][-1]['gold'] if order['repairs'] else order['prior_gold']
                if previous_gold is not None and previous_gold >= status['gold']:
                    result['charged'] = previous_gold - status['gold']
                order['repairs'].append(result)
                wallet = status = None
            elif kind == 'human' and row.get('isSelf') and order['repairs']:
                result = order['repairs'][-1]
                if result['kind']==1 and not wallet and 'appearance_packet' not in result:
                    result.update(appearance_packet=row['packetId'],appearance_length=row['length'])
            elif kind == 'frame_header' and row['direction']=='S' and row['opcode']==0x0A:
                order['messages'].append(dict(packet=row['packetId'],length=row['plainLength'],mode=row.get('messageMode')))
    for order in orders:
        repairs, messages = order.get('repairs'), order.get('messages')
        if repairs and messages and all(row.get('item') for row in repairs):
            amounts = [row.get('charged',row['quote']) for row in repairs]
            total = sum(amounts)
            rows_length = sum(len(row['item'].encode('cp949',errors='replace'))+len(str(amount))+3
                              for row,amount in zip(repairs,amounts))
            order['repair_receipt_shape'] = dict(observed_length=messages[-1]['length'],
                                                item_count=len(repairs),total=total,
                                                reconstructed_length=4+rows_length+13+len(str(total)))
    return {'session':path.parent.name,'quotes':quotes[-24:],'orders':orders[-24:]}


@server.tool(annotations=READ_ONLY)
def shop_purchase_observations(session: str = "latest") -> dict[str, Any]:
    """C39 구매 요청과 지갑, 잔액, 지급 품목, 결과 메시지, 상품 목록을 순서대로 확인합니다."""
    path = session_events(session)
    orders = []
    active = last_request = None
    slots = {}
    catalogs, catalog_changes = {}, deque(maxlen=24)
    stock = ShopStockObservations()
    for row in recorded_rows(path):
        stock_samples = stock.ingest(row)
        kind = row['kind']
        scope = row.get('epoch'), row.get('map'), row.get('recentActionId')
        if kind in ('inventory_add','inventory_remove') and row.get('slot') is None:
            continue
        if kind == 'context_reset':
            slots.clear()
            catalogs.clear()
            last_request = None
        elif kind == 'inventory_add':
            slot = row['slot']
            previous_slot = slots.get(slot)
            slots[slot] = dict(packet=row['packetId'], sprite=row['wireSprite'],
                               name=row.get('displayName',row.get('name')), quantity=row['quantity'],
                               position=[row['word1'],row['word2']])
        elif kind == 'inventory_remove' and row['slot'] in slots:
            slots[row['slot']].update(remove_packet=row['packetId'], remove_mode=row['byte2'])
        if kind in ('context_reset','client_action'):
            active = None
            if kind == 'client_action':
                last_request = row
            if kind == 'client_action' and row.get('opcode') == 0x39 and row.get('menu') == 0x4B:
                order = {key:row[key] for key in ('time','map','packetId','targetId','mode','length','orderMode','orderLines') if key in row}
                order['responses'] = []
                orders.append(order)
                active = order, scope, datetime.datetime.fromisoformat(row['time']), catalogs.get((*scope[:2],row.get('targetId')))
            continue
        if kind == 'merchant_dialog' and row.get('mode') == 10 and row.get('flags') == 1 and 'items' in row:
            key = (*scope[:2],row['oid'])
            current = {(item['sprite'],item['name'],item['price']):item['flags'] for item in row['items']}
            previous = catalogs.get(key)
            catalogs[key] = row['packetId'],observed_time(row),current,row['items']
            if previous:
                deltas = [dict(sprite=sprite,name=name,price=price,before=previous[2][identity],after=quantity)
                          for identity,quantity in current.items() if identity in previous[2] and previous[2][identity]!=quantity
                          for sprite,name,price in [identity]]
                if deltas:
                    change = dict(map=scope[1],merchant=row.get('text'),before=previous[0],after=row['packetId'],
                                  interval_seconds=round(observed_time(row)-previous[1],3),items=deltas)
                    if (last_request and last_request.get('recentActionId') == scope[2] and
                            last_request.get('opcode') == 0x39 and last_request.get('targetId') == row['oid']):
                        change.update(request=last_request['packetId'],menu=last_request.get('menu'))
                    if active and scope == active[1]:
                        change['order_granted_items'] = any(reply['kind']=='item' for reply in active[0]['responses'])
                    catalog_changes.append(change)
        if not active or scope != active[1]:
            continue
        order, _, start, opening_catalog = active
        if not 0 <= (datetime.datetime.fromisoformat(row['time'])-start).total_seconds() <= 2:
            active = None
            continue
        packet = row.get('packetId')
        length = row.get('length')
        if kind == 'inventory_add':
            response = {'kind':'wallet' if row.get('wireSprite') == 0x8BB8 else 'item','packet':packet,'length':length}
            response.update({key:row[key] for key in
                             ('name','wireSprite','slot','quantity','word1','word2','byte3','trailingBytes','description') if key in row})
            if 'word3' in row and 'word4' in row:
                response.update(carried_weight=row['word3'],item_weight=row['word4'])
            if (response['kind'] == 'item' and previous_slot and order['responses']
                    and order['responses'][-1]['kind'] == 'status'):
                response['previous_slot'] = previous_slot.copy()
        elif kind == 'status' and 'gold' in row:
            response = {'kind':'status','packet':packet,'length':length,'gold':row['gold'],'mask':row['mask'],'state':row.get('state')}
            if 'extraDword' in row and 'extraWord' in row:
                response.update(effect_flags=row['extraDword'],speed_percent=row['extraWord'])
        elif kind == 'frame_header' and row.get('direction') == 'S' and row.get('opcode') == 0x0A:
            response = {'kind':'message','packet':packet,'length':row.get('plainLength')}
            if 'messageMode' in row:
                response['mode'] = row['messageMode']
        elif kind == 'shop_notice':
            attach_shop_notice(order, order['responses'], row)
            continue
        elif kind == 'merchant_dialog' and row.get('mode') == 10:
            response = dict(kind='catalog',packet=packet,length=length,native_length=row.get('nativeBytesConsumed'),
                            padding_bytes=int(row.get('trailingBytes')=='00'),
                            discarded_suffix_bytes=row.get('discardedSuffixBytes',0))
            if stock_samples:
                order['stock_lines'] = stock_samples
                if len(stock_samples)==1:
                    order['stock'] = stock_samples[0]
            active = None
        else:
            continue
        replies = order['responses']
        if response['kind'] == 'status':
            previous_index = next((i for i in range(len(replies)-1,-1,-1) if replies[i]['kind'] == 'status'), None)
            if previous_index is not None and response['gold'] > replies[previous_index]['gold']:
                previous_balance = replies[previous_index]
                wallets = [item for item in replies[max(0,previous_index-1):] if item['kind'] == 'wallet']
                order.setdefault('balance_reversals', []).append(dict(
                    debit=previous_balance['packet'], restoration=response['packet'],
                    tentative_gold=previous_balance['gold'], restored_gold=response['gold'],
                    debit_status_length=previous_balance['length'], restoration_status_length=response['length'],
                    debit_mask=previous_balance['mask'], restoration_mask=response['mask'],
                    wallet_updates=[dict(packet=item['packet'],slot=item.get('slot'),gold=item.get('quantity'),
                                         position=[item.get('word1'),item.get('word2')],length=item['length'],
                                         suffix=item.get('trailingBytes')) for item in wallets],
                    refunded=response['gold'] - previous_balance['gold'], earlier_grants=len(order.get('grant_pairs', []))))
        if response['kind'] == 'item' and len(replies) >= 2 and replies[-2]['kind'] == 'status':
            first = replies[-1]
            fields = ('slot', 'wireSprite', 'name', 'quantity')
            if first['kind'] == 'item' and all(first.get(key) == response.get(key) for key in fields):
                grant = dict(
                    name=response.get('name'), sprite=response.get('wireSprite'), slot=response.get('slot'),
                    balance=replies[-2]['packet'], initial=first['packet'], positioned=response['packet'],
                    initial_position=[first.get('word1'),first.get('word2')],
                    final_position=[response.get('word1'),response.get('word2')],
                    initial_suffix=first.get('trailingBytes'), positioned_suffix=response.get('trailingBytes'),
                    initial_length=first.get('length'), positioned_length=response.get('length'))
                if 'description' in response:
                    grant['inventory_description'] = response['description']
                    grant['inventory_stats'] = item_stats(response['description'])
                if opening_catalog:
                    matches = [item for item in opening_catalog[3]
                               if (item['name'],item['sprite']) == (grant['name'],grant['sprite'])]
                    if len(matches)==1 and 'description' in matches[0]:
                        grant['catalog_packet'] = opening_catalog[0]
                        grant['catalog_description'] = matches[0]['description']
                order.setdefault('grant_pairs', []).append(grant)
        replies.append(response)
    return {'session':path.parent.name,'orders':orders[-24:],'catalog_stock_changes':list(catalog_changes)}


@server.tool(annotations=READ_ONLY)
def character_inspection_observations(session: str = "latest") -> dict[str, Any]:
    """C43·C76 조회와 S34·S69 응답의 장비 슬롯과 숫자 필드를 확인합니다."""
    path = session_events(session)
    requests, replies = {}, {}
    visible = {}
    inspected_slots = (1, 2, 3, 4, 6, 7, 8, 0, 13, 14, 15, 16, 20, 23, 24)
    for row in recorded_rows(path):
        op, kind = row.get('opcode'), row.get('kind')
        actor = row.get('epoch'), row.get('oid')
        if kind in ('context_reset', 'map'):
            visible.clear()
        elif kind == 'object_remove':
            visible.pop(actor, None)
        elif kind == 'human' and row.get('equipment'):
            visible[actor] = row
        if op not in (0x34, 0x43, 0x69, 0x76):
            continue
        packet_id = row.get('packetId')
        common = {key: row[key] for key in ('time', 'map', 'packetId', 'length') if key in row}
        if op in (0x43, 0x76) and kind == 'client_action':
            requests[packet_id] = {**common, 'opcode': op, 'mode': row.get('mode'), 'oid': row.get('targetId')}
        elif kind == 'frame_header':
            if op in (0x34, 0x69) and row.get('direction') == 'S':
                replies[packet_id] = {**common, 'opcode': op, 'length': row.get('plainLength'), 'layout': 'header_only'}
            elif op == 0x76 and packet_id not in requests:
                requests[packet_id] = {**common, 'opcode': op, 'length': row.get('plainLength'), 'layout': 'header_only'}
        elif op in (0x34, 0x69) and row.get('raw'):
            try:
                raw = sanitized_packet(bytes.fromhex(row['raw']))
                if raw is not None:
                    reply = {**common, 'opcode': op, **decode(raw)}
                    if op == 0x34 and reply.get('flags') == 0:
                        human = visible.get((row.get('epoch'), reply['oid']))
                        if human and row.get('map') == human.get('map'):
                            age = observed_time(row)-observed_time(human)
                            if 0 <= age <= 12:
                                reply['appearance_packet'] = human.get('packetId')
                                reply['appearance_age_ms'] = round(age*1000)
                                reply['equipment_appearances'] = [
                                    dict(slot=slot, sprite=sprite, appearance_layer=layer, appearance_word=human['equipment'][layer],
                                         model_assets=equipment_layer_assets(layer, human['equipment'][layer], human['equipment'][1]))
                                    for slot, sprite in zip(inspected_slots, reply['equipment'])
                                    if sprite and (layer := EQUIPMENT_LAYERS.get(slot)) is not None and human['equipment'][layer]]
                    replies[packet_id] = reply
            except (ValueError, IndexError):
                continue
    return {'session': path.parent.name, 'requests': list(requests.values())[-30:], 'replies': list(replies.values())[-30:]}


@server.tool(annotations=READ_ONLY)
def merc_packet_observations(session: str = "latest") -> dict[str, Any]:
    """용병의 S4F 상태, C43·C54 제어, C72·S61 가방 이동과 소환·라스 이동을 대조합니다."""
    path = session_events(session)
    replies, client_frames = [], {}
    inventories = deque(maxlen=40)
    inventory_transfers = deque(maxlen=40)
    transfer_request = transfer = None

    def finish_transfer(entry):
        if entry and (entry['requestBodyRecorded'] or any(reply['opcode']==0x61 for reply in entry['responses'])):
            inventory_transfers.append(entry)

    requests, objects = {}, {}
    counts = Counter()
    removals = deque(maxlen=40)
    last_delete = None
    merc_request = None
    merc_frame = None
    inventory_frame = None
    command_choices = deque(maxlen=40)
    control_closes = deque(maxlen=40)
    last_close = None
    leon_item_slots = {}
    summons = deque(maxlen=20)
    summon = None
    leon_id = None
    ras_request = None
    ras_map = None
    ras_controls = None
    ras_arrivals = deque(maxlen=20)
    last_move = None
    state_frame = None
    refresh_at = {}
    refresh_lengths = Counter()
    movement_refreshes = deque(maxlen=40)
    sign_items = {}
    sign_use = None
    sign_uses = deque(maxlen=40)
    known_sign_sprites, sign_effects, sign_hits = {}, {}, deque(maxlen=40)
    for row in recorded_rows(path):
        kind, op = row.get('kind'), row.get('opcode')
        packet_id = row.get('packetId')
        slot = row.get('slot')
        outbound = packet_id and (kind == 'client_action' or kind == 'frame_header' and row.get('direction') == 'C')
        if transfer_request and (kind in ('map','context_reset') or outbound and packet_id != transfer['request'] or
                row.get('epoch') != transfer_request.get('epoch') or row.get('map') != transfer['map'] or
                observed_time(row) - observed_time(transfer_request) > .75):
            finish_transfer(transfer)
            transfer_request = transfer = None
        if outbound and op == 0x72:
            decoded_transfer = row.get('action') == 'merc_inventory_move'
            if row.get('action') == 'bank_move':
                transfer_request = transfer = None
            elif decoded_transfer or kind == 'frame_header' and row.get('plainLength') == 13:
                if transfer is None:
                    transfer_request = row
                    transfer = dict(request=packet_id,map=row.get('map'),requestBodyRecorded=False,responses=[])
                if decoded_transfer:
                    transfer.update(requestBodyRecorded=True, **{key: row[key] for key in
                        ('mode','slot','destinationSlot','dropX','dropY','dropQuantity')})
        if kind == 'context_reset':
            leon_item_slots.clear()
            sign_items.clear()
            sign_use = None
            summon = None
            leon_id = ras_request = ras_map = ras_controls = None
        elif kind == 'map':
            summon = None
            sign_use = None
            ras_map = ras_controls = None
            if (ras_request and ras_request.get('packetId') and packet_id
                    and ras_request.get('mapId') == row.get('map')
                    and ras_request.get('epoch') == row.get('epoch')
                    and ras_request['packetId'].split(':')[0] == packet_id.split(':')[0]
                    and 0 <= observed_time(row) - observed_time(ras_request) <= 1):
                ras_map = row
            else:
                ras_request = None
        elif kind == 'inventory_add':
            leon_item_slots.pop(slot,None)
            sign_items.pop(slot,None)
            sprite = row.get('wireSprite',0)
            rank = sprite - 0x8F80
            if 1 <= rank <= 5 and row.get('name') == f'개인방팻말(Lv{rank})':
                sign_items[slot] = (rank,row.get('quantity'))
            item = {('고용서(레온)',0x8D15):'deed',('소환서(용병)',0x8574):'summon_scroll'}.get(
                (row.get('name'),row.get('wireSprite')))
            if item:leon_item_slots[slot] = (item,row.get('quantity'))
        elif kind == 'client_action':
            sign_use = None
            if op == 0x1C and slot in sign_items and packet_id and row.get('recentActionId') is not None:
                rank, quantity = sign_items[slot]
                sign_use = dict(request=packet_id,map=row.get('map'),slot=slot,rank=rank,previousQuantity=quantity)
                sign_uses.append(sign_use)
                sign_action = row.get('recentActionId')
            summon = dict(request=packet_id, map=row.get('map'), slot=slot, item=leon_item_slots[slot][0],
                          previousQuantity=leon_item_slots[slot][1], appearances=[]) if (
                op == 0x1C and slot in leon_item_slots) else None
            if leon_id and row.get('action') == 'destination_choice' and row.get('targetId') == 0:
                ras_request = row
                ras_map = ras_controls = None
        if sign_use and row.get('recentActionId') == sign_action and row.get('map') == sign_use['map']:
            if kind == 'objects' and 'appearance' not in sign_use:
                for obj in row.get('objects', []):
                    if obj.get('category') == 6 and obj.get('sprite') == 0x40C2 + sign_use['rank']:
                        sign_use.update(appearance=packet_id,oid=obj['oid'],sprite=obj['sprite'],
                                        position=[obj['x'],obj['y'],obj['floor']],appearanceLength=row.get('length'))
                        break
            elif 'appearance' in sign_use and slot == sign_use['slot'] and (
                    kind == 'inventory_remove' and row.get('byte2') == 6 or kind == 'inventory_add' and
                    type(sign_use['previousQuantity']) is int and sign_items.get(slot) == (sign_use['rank'],sign_use['previousQuantity']-1)):
                sign_use.update(inventoryDelta=packet_id,inventoryLength=row.get('length'))
                sign_use = None
            elif kind == 'frame_header' and row.get('direction') == 'S' and op == 0x0A and packet_id and (
                    adjacent_packets(sign_use['request'],packet_id)):
                sign_use.update(notice=packet_id,noticeLength=row.get('plainLength'))
        if kind == 'merc' and (row.get('mode'), row.get('subtype')) == (2, 0) and row.get('targetId'):
            leon_id = row['targetId']
        if summon and row.get('map') == summon['map']:
            if kind == 'merc' and (row.get('mode'), row.get('subtype')) == (2, 0):
                summon['reset'] = packet_id
            elif kind == 'human' and 'reset' in summon and row.get('appearance') == '400100004001':
                summon.setdefault('oid', row['oid'])
                if row['oid'] == summon['oid']:
                    summon['appearances'].append(packet_id)
                    summon['position'] = [row['x'], row['y'], row['floor']]
            elif (slot == summon['slot'] and (kind == 'inventory_remove' and row.get('byte2') == 6
                    or kind == 'inventory_add' and type(summon['previousQuantity']) is int
                    and leon_item_slots.get(slot) == (summon['item'],summon['previousQuantity']-1))):
                summon['inventory_delta'] = packet_id
                if summon['item']=='deed':summon['deed_removal'] = packet_id
            elif (kind == 'merc' and (row.get('mode'), row.get('subtype')) == (2, 2)
                    and 'inventory_delta' in summon and summon['appearances']):
                summon['controls'] = packet_id
                leon_id = summon['oid']
                summons.append(summon)
                summon = None
        if ras_map and row.get('map') == ras_map.get('map'):
            if kind == 'merc' and (row.get('mode'), row.get('subtype')) == (2, 2):
                ras_controls = packet_id
            elif (kind == 'human' and row.get('oid') == leon_id and ras_controls and packet_id
                    and adjacent_packets(ras_controls,packet_id)):
                delay = observed_time(row) - observed_time(ras_map)
                if 0 <= delay <= 10:
                    ras_arrivals.append(dict(request=ras_request['packetId'], mapPacket=ras_map['packetId'],
                        controls=ras_controls, appearance=packet_id, fromMap=ras_request.get('map'),
                        map=row['map'], oid=leon_id, delayMs=round(delay*1000),
                        position=[row['x'],row['y'],row['floor']], direction=row['direction']))
                ras_request = ras_map = ras_controls = None
        if kind == 'inventory_remove':
            leon_item_slots.pop(slot,None)
            sign_items.pop(slot,None)
        if kind in ('map', 'context_reset'):
            requests.clear()
            objects.clear()
            known_sign_sprites.clear()
            sign_effects.clear()
            last_delete = None
            merc_request = None
            merc_frame = None
            inventory_frame = None
            last_close = None
            last_move = state_frame = None
            refresh_at.clear()
        elif kind == 'object_move':
            last_move = row
        elif kind == 'frame_header' and row.get('direction') == 'S' and op == 0x4F:
            state_frame = row
        elif kind == 'frame_header' and row.get('direction') == 'S' and op in (0x51,3) and last_close and packet_id:
            if op == 0x51 and adjacent_packets(last_close['response'],packet_id):
                last_close['inputBlock'] = packet_id
            elif op == 3 and last_close.get('inputBlock') and adjacent_packets(last_close['inputBlock'],packet_id):
                last_close['worldRedirect'] = packet_id
                last_close = None
        elif kind == 'objects':
            objects.update((obj['oid'], (packet_id, obj['sprite']))
                           for obj in row.get('objects', []) if obj.get('category') == 6)
            for obj in row.get('objects', []):
                known_sign_sprites.pop(obj['oid'],None)
                if obj.get('category') == 6 and 0x40C3 <= obj['sprite'] <= 0x40C7:
                    known_sign_sprites[obj['oid']] = obj['sprite']
        elif kind == 'effect' and row.get('oid') in known_sign_sprites:
            sign_effects[row['oid']] = row
        elif kind == 'damage_effect' and (effect := sign_effects.pop(row.get('target'),None)):
            words = row.get('extraWords', [])
            if words and words[0] == effect.get('effect') and packet_id and effect.get('packetId') and (
                    adjacent_packets(effect['packetId'],packet_id)):
                sign_hits.append(dict(effectPacket=effect['packetId'],hitPacket=packet_id,map=row.get('map'),
                    sprite=known_sign_sprites[row['target']],target=row['target'],targetVisible=row['target'] in objects,
                    attacker=row['attacker'],effect=words[0],
                    motion=row.get('motionId'),sound=row.get('swingSoundId'),damage=row.get('displayedDamage'),
                    effectLength=effect.get('length'),hitLength=row.get('length')))
        elif kind == 'object_remove':
            target = row.get('oid')
            sign_effects.pop(target,None)
            seen = objects.pop(target, None)
            if seen and 0x40C3 <= seen[1] <= 0x40C7:
                removal = dict(packetId=packet_id, map=row.get('map'), targetId=target, objectPacket=seen[0],sprite=seen[1])
                if last_delete and last_delete['targetId'] == target and last_delete.get('map') == row.get('map') and (
                        0 <= (datetime.datetime.fromisoformat(row['time']) -
                              datetime.datetime.fromisoformat(last_delete['time'])).total_seconds() <= 1):
                    removal['deleteRequest'] = last_delete['packetId']
                    last_delete = None
                removals.append(removal)
        elif kind == 'client_action' and op == 0x43 and row.get('mode') == 1:
            requests[row.get('recentActionId')] = row
        elif op == 0x54 and (kind == 'client_action' or kind == 'frame_header' and row.get('direction') == 'C'):
            client_frames.setdefault(packet_id, {}).update({key: row[key] for key in
                ('packetId', 'map', 'mode', 'targetId', 'selector', 'inputBytes', 'controlOptions', 'slot', 'mercWords', 'plainLength') if key in row})
            if kind == 'client_action' and row.get('mode') == 2 and 'selector' in row:
                merc_request = row
            elif kind == 'frame_header':
                merc_frame = row
            if kind == 'client_action' and row.get('mode') == 3 and row.get('selector') == 3 and 'targetId' in row:
                last_delete = row
        elif op == 0x61 and kind == 'frame_header' and row.get('direction') == 'S':
            inventory_frame = row
        elif op == 0x61 and row.get('group') == 3:
            inventory = {key: row[key] for key in ('packetId', 'map', 'mode', 'count', 'entries', 'slot',
                'wireSprite', 'quantity', 'word1', 'word2', 'mercFlags', 'mercWords', 'stackFlag',
                'durabilityByte', 'remainingMinutes', 'itemFlag', 'slotByte', 'modeByte', 'word') if key in row}
            inventory['bodyRecorded'] = (kind in ('merc_inventory_slot', 'merc_inventory_delta')
                                         or kind == 'merc_inventory_header' and
                                         (row.get('count') == 0 or 'entries' in row))
            if inventory_frame and inventory_frame.get('packetId') == packet_id:
                inventory['plainLength'] = inventory_frame.get('plainLength')
            if (merc_request and row.get('map') == merc_request.get('map')
                    and row.get('epoch') == merc_request.get('epoch')
                    and row.get('recentActionId') == merc_request.get('recentActionId')
                    and 0 <= observed_time(row) - observed_time(merc_request) <= 0.75):
                inventory['nearbyRequest'] = merc_request['packetId']
            inventories.append(inventory)
            if transfer and row.get('mode') in (1,2):
                transfer['responses'].append(dict(opcode=op, **inventory))
        elif op == 0x4F and row.get('raw'):
            try:
                raw = sanitized_packet(bytes.fromhex(row['raw']))
                if not raw or raw[0] != 0x4F:
                    continue
                packet = decode(raw)
            except (ValueError, IndexError):
                continue
            packet.update(packetId=packet_id, map=row.get('map'))
            if (packet.get('mode'), packet.get('subtype')) == (2, 2) and state_frame and last_move and (
                    state_frame.get('packetId') == packet_id and last_move.get('packetId') and
                    last_move.get('map') == row.get('map') and last_move.get('epoch') == row.get('epoch') and
                    adjacent_packets(last_move['packetId'],packet_id) and
                    0 <= observed_time(row) - observed_time(last_move) <= .1):
                wire_length = state_frame['plainLength']
                refresh_lengths[wire_length] += 1
                key = packet_id.split(':')[0], row.get('map'), last_move['oid']
                at = observed_time(row)
                entry = dict(move=last_move['packetId'], response=packet_id, map=row.get('map'),
                             movingOid=last_move['oid'], plainLength=wire_length)
                if leon_id is not None:
                    entry['boundLeon'] = leon_id == last_move['oid']
                if key in refresh_at:
                    entry['intervalMs'] = round((at - refresh_at[key])*1000)
                refresh_at[key] = at
                if packet.get('record') == 'state':
                    entry.update(controlMode=packet['controlMode'], blocks=packet['blocks'])
                    if len(raw) == wire_length:
                        entry['trailingBytes'] = packet['trailingBytes']
                movement_refreshes.append(entry)
            if packet.get('record') == 'control' and len(raw) == 8 and packet_id:
                last_close = dict(response=packet_id,map=row.get('map'),wireId=int.from_bytes(raw[3:7],'big'))
                if (merc_request and merc_request.get('selector') == 10 and merc_request.get('map') == row.get('map')
                        and merc_request.get('epoch') == row.get('epoch')
                        and merc_request.get('recentActionId') == row.get('recentActionId')
                        and 0 <= observed_time(row) - observed_time(merc_request) <= 0.75):
                    last_close['dismissRequest'] = merc_request['packetId']
                control_closes.append(last_close)
            if (packet.get('mode'), packet.get('subtype')) == (2, 11):
                choice = {'response': packet_id, 'map': row.get('map')}
                if packet.get('record') == 'control_values':
                    choice['selected'] = packet['values']
                if (merc_request and merc_request.get('selector') == 9
                        and merc_request.get('epoch') == row.get('epoch')
                        and merc_request.get('recentActionId') == row.get('recentActionId')
                        and merc_request.get('map') == row.get('map')
                        and 0 <= observed_time(row) - observed_time(merc_request) <= 0.75):
                    choice.update(request=merc_request['packetId'], requested=merc_request.get('controlOptions'))
                elif (merc_frame and merc_frame.get('plainLength') == 5 and merc_frame.get('map') == row.get('map')
                        and merc_frame.get('packetId') and packet_id
                        and adjacent_packets(merc_frame['packetId'],packet_id)):
                    choice['candidateRequest'] = merc_frame['packetId']
                command_choices.append(choice)
            target = packet.get('targetId')
            if target is not None:
                request = requests.get(row.get('recentActionId'))
                if request and request.get('targetId') == target:
                    packet['inspectRequest'] = request['packetId']
                if target in objects:
                    packet['objectPacket'], packet['objectSprite'] = objects[target]
            counts[packet.get('record', 'undecoded')] += 1
            replies.append(packet)
        if (transfer and kind in ('inventory_add','inventory_remove')
                and (transfer.get('mode') != 0 or slot == transfer['slot'])):
            transfer['responses'].append({'opcode':op, **{key:row[key] for key in
                ('packetId','slot','byte2','word','wireSprite','quantity','length') if key in row}})
    finish_transfer(transfer)
    return {'session': path.parent.name, 'counts': dict(counts), 'server_packets': replies[-40:],
            'client_packets': list(client_frames.values())[-40:], 'sign_removals': list(removals),
            'room_sign_uses': list(sign_uses),
            'room_sign_hits': list(sign_hits),
            'leon_summons': list(summons), 'mercenary_inventories': list(inventories),
            'mercenary_inventory_transfers': list(inventory_transfers),
            'leon_ras_arrivals': list(ras_arrivals), 'leon_command_choices': list(command_choices),
            'movement_refresh_lengths': dict(refresh_lengths), 'movement_refreshes': list(movement_refreshes),
            'mercenary_control_closes': list(control_closes)}


@server.tool(annotations=READ_ONLY)
def room_exit_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """개인방의 C6A 버튼, C16 이동 선택과 S64 창·가구 응답을 패킷 ID별로 조회합니다."""
    path = session_events(session)
    packets = deque(maxlen=limit)
    by_id = {}
    counts = Counter()
    for row in recorded_rows(path):
        kind, op = row['kind'], row.get('opcode')
        if row.get('map') != 9801 and (kind, op, row.get('action'), row.get('selector')) != (
                'client_action', 0x6A, 'room_minigame', 10):
            continue
        packet_id = row.get('packetId')
        direction = {0x64: 'S', 0x6A: 'C', 0x16: 'C'}.get(op)
        header = kind == 'frame_header' and row.get('direction') == direction and op != 0x16
        decoded = (kind in ('room_controls', 'room_exit_menu', 'room_status',
                            'room_furniture_list', 'room_furniture_update') or kind == 'client_action' and
                   (row.get('action') == 'room_minigame' and 'selector' in row or
                    row.get('action') == 'destination_choice' and 'mapId' in row))
        if not packet_id or direction is None or not (header or decoded):
            continue
        if packet_id not in by_id:
            if len(packets) == limit:
                del by_id[packets[0]['packetId']]
            packet = {'packetId': packet_id, 'direction': direction, 'opcode': op,
                      'plainLength': row.get('plainLength', row.get('length')), 'bodyRecorded': False}
            packets.append(packet)
            by_id[packet_id] = packet
            counts[direction] += 1
        if decoded:
            by_id[packet_id].update({key: row[key] for key in
                                     ('mode', 'selector', 'value', 'destinationIds', 'mapId', 'flag', 'count',
                                      'inputBytes', 'level', 'ratings', 'today', 'total', 'entries',
                                      'entryId', 'labelBytes', 'placement', 'catalogKey') if key in row})
            by_id[packet_id]['bodyRecorded'] = True
    return {'session': path.parent.name, 'counts': dict(counts), 'packets': list(packets)}


@server.tool(annotations=READ_ONLY)
def room_furniture_catalog(query: str = "", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """클라이언트 가구 키, 이름과 방향별 DEC 리소스를 조회합니다."""
    path = ROOT / "evidence" / "original-Meta.dat"
    try:
        catalog = decoration_catalog(path)
    except (OSError, ValueError, zlib.error) as error:
        raise ToolError("클라이언트 가구 메타데이터를 읽을 수 없습니다.") from error
    matches = [entry for entry in catalog.values()
               if query in entry['catalogKey'] or query in entry['displayName']]
    return {'total': len(catalog), 'matches': len(matches), 'entries': matches[:limit]}


@server.tool(annotations=READ_ONLY)
def world_crc_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """S3B 요청과 C45 응답의 주기, 길이, 기록된 16비트 값을 조회합니다."""
    path = session_events(session)
    request_count = response_count = 0
    requests, last_request_time, challenges, responses = {}, {}, {}, {}
    intervals, pairs = [], []
    for row in recorded_rows(path):
        kind, opcode = row['kind'], row.get('opcode')
        if opcode not in (0x3B,0x45):
            continue
        pid, epoch = row.get('packetId'), row.get('epoch')
        if kind=='crc_challenge':
            challenges[pid] = row['challenge']
        elif kind=='crc_reply':
            responses[pid] = row['checksum']
        elif kind=='frame_header' and row.get('direction')=='S' and opcode==0x3B:
            request_count += 1
            at = datetime.datetime.fromisoformat(row['time'])
            if epoch in last_request_time:
                intervals.append((at-last_request_time[epoch]).total_seconds()*1000)
            last_request_time[epoch] = at
            requests[epoch] = row, at
        elif kind=='frame_header' and row.get('direction')=='C' and opcode==0x45:
            response_count += 1
            if previous := requests.pop(epoch,None):
                request, at = previous
                delay = (datetime.datetime.fromisoformat(row['time'])-at).total_seconds()*1000
                if 0<=delay<=1000:
                    pairs.append(dict(request=request['packetId'],reply=pid,map=request.get('map'),
                        request_length=request.get('plainLength'),reply_length=row.get('plainLength'),reply_ms=round(delay,1)))
    verified, mismatched = 0, 0
    for pair in pairs:
        challenge, reply = challenges.get(pair['request']), responses.get(pair['reply'])
        if challenge is not None and reply is not None:
            expected = ((challenge&0xff)<<8)|(challenge>>8)
            pair.update(challenge=challenge,checksum=reply,expected=expected,matches=reply==expected)
            if reply==expected: verified += 1
            else: mismatched += 1
    return dict(session=path.parent.name,requests=request_count,replies=response_count,paired=len(pairs),
                matched_values=verified,mismatched_values=mismatched,
                median_request_interval_ms=round(median(intervals)) if intervals else None,observations=pairs[-limit:])


@server.tool(annotations=READ_ONLY)
def object_push_observations(session: str = "latest", limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """C69 요청의 전방 칸과 다음 조작 전의 S0C 이동, 가까운 서버 프레임을 대조합니다."""
    path = session_events(session)
    positions, requests = {}, {}
    active = None
    for row in recorded_rows(path):
        key = row.get('epoch'),row.get('map')
        kind = row['kind']
        packet = row.get('packetId')
        if kind in ('self_position','self_move'):
            positions[key] = [row[k] for k in ('x','y','floor')]
        client = kind == 'client_action' or kind == 'frame_header' and row.get('direction') == 'C'
        if client:
            if row.get('opcode') != 0x69:
                active = None
                continue
            request = requests.setdefault(packet,dict(packet=packet,map=key[1],length=row.get('length',row.get('plainLength')),
                preceding_self_position=positions.get(key),nearby_server_frames=[],object_steps=[]))
            if kind == 'client_action' and row.get('action') == 'push_request':
                origin = [row[k] for k in ('pushX','pushY','pushFloor')]
                direction = row['direction']
                request.update(origin=origin,direction=direction)
                if direction in range(8):
                    dx,dy = DELTAS[direction]
                    request['adjacent_tile'] = [origin[0]+dx,origin[1]+dy,origin[2]]
            if not active or active[2] is not request:
                active = key,observed_time(row),request
        elif active:
            scope, started, request = active
            elapsed = observed_time(row)-started
            if scope != key or kind == 'context_reset' or not 0 <= elapsed <= .25:
                active = None
            elif kind == 'frame_header' and row.get('direction') == 'S':
                request['nearby_server_frames'].append(dict(packet=packet,opcode=row['opcode'],length=row.get('plainLength')))
            elif kind == 'object_move':
                tile = [row[k] for k in ('x','y','floor')]
                request['object_steps'].append(dict(packet=packet,origin=tile,direction=row['direction'],
                    starts_in_adjacent_tile=tile==request.get('adjacent_tile'),same_direction=row['direction']==request.get('direction')))
    typed = [request for request in requests.values() if 'adjacent_tile' in request]
    return dict(session=path.parent.name,requests=len(requests),positioned_requests=len(typed),
                requests_with_adjacent_step=sum(any(step['starts_in_adjacent_tile'] for step in r['object_steps']) for r in typed),
                requests_with_matching_step=sum(any(step['starts_in_adjacent_tile'] and step['same_direction']
                                                    for step in r['object_steps']) for r in typed),
                observations=list(requests.values())[-limit:])


@server.tool(annotations=READ_ONLY)
def same_map_displacement_observations(session: str = "latest", map_id: int | None = None,
                                       limit: Annotated[int, Field(ge=1, le=100)] = 20) -> dict[str, Any]:
    """같은 맵에서 밀린 위치와 공격자, 효과 7, 지도 확인, 연속 밀기 간격을 대조합니다."""
    path = session_events(session)
    results = deque(maxlen=limit)
    total = 0
    epoch = player = position = action = arrival = sample = sample_at = None
    previous_displacement = None
    appearances = {}
    pending_checks, awaiting_checks = deque(maxlen=64), deque(maxlen=64)
    for row in recorded_rows(path):
        kind = row['kind']
        if row.get('epoch') != epoch or kind == 'context_reset':
            epoch = row.get('epoch')
            player = position = action = arrival = sample = None
            appearances.clear()
            previous_displacement = None
            pending_checks.clear()
            awaiting_checks.clear()
        if sample and kind in ('client_action','effect','damage_effect','map_check') and observed_time(row)-sample_at > 1.25:
            sample = None
        if kind in ('self_identity','context_snapshot'):
            player = row.get('oid') if kind == 'self_identity' else row.get('selfId')
        elif kind == 'map':
            sample = None
            appearances.clear()
            area = row['map']
            if position and position[0] != area:
                pending_checks.clear()
                awaiting_checks.clear()
            arrival = row if position and position[0] == area and map_id in (None,area) else None
        elif kind in ('self_position','self_move'):
            tile = [row[k] for k in ('x','y','floor')]
            if kind == 'self_position' and arrival is not None and 0 <= observed_time(row)-observed_time(arrival) <= .25:
                origin = position[1]
                if origin != tile:
                    sample_at = observed_time(row)
                    sample = dict(map=row['map'],map_packet=arrival['packetId'],position_packet=row['packetId'],
                                  origin=origin,destination=tile,delta=[b-a for a,b in zip(origin,tile)])
                    pending_checks.append((sample_at,sample))
                    if action and 0 <= observed_time(arrival)-observed_time(action) <= .25:
                        sample['nearby_client_request'] = dict(packet=action['packetId'],opcode=action['opcode'])
                arrival = None
            position = row['map'],tile
        elif kind == 'client_action':
            if row.get('opcode') == 0x35:
                now = observed_time(row)
                while pending_checks and now-pending_checks[0][0] > 1.25:
                    pending_checks.popleft()
                if pending_checks and row.get('mapId') == pending_checks[0][1]['map']:
                    _,checked = pending_checks.popleft()
                    checked['check_request'] = dict(packet=row['packetId'],checksum=row.get('checksum'))
                    awaiting_checks.append((now,checked))
            elif row.get('opcode') not in (0x45,0x71):
                action = row
        elif kind == 'map_check' and row.get('phase') == 2:
            now = observed_time(row)
            while awaiting_checks and now-awaiting_checks[0][0] > 1.25:
                awaiting_checks.popleft()
            if awaiting_checks and row.get('map') == awaiting_checks[0][1]['map']:
                _,checked = awaiting_checks.popleft()
                checked['check_reply'] = dict(packet=row['packetId'],wire_hex=row.get('raw'))
        elif sample:
            if kind == 'human' and 'strike_packet' not in sample:
                appearances[row['oid']] = row
            elif (kind == 'effect' and 'strike_packet' not in sample and player is not None
                    and row.get('oid') == player and row.get('effect') == 7 and 0 <= observed_time(row)-sample_at <= .25):
                sample['effect_packet'] = row['packetId']
            elif kind == 'damage_effect' and 'effect_packet' in sample and 'strike_packet' not in sample:
                if row.get('extraWords') == [7,0,65535] and adjacent_packets(sample['effect_packet'],row['packetId']):
                    sample['strike_packet'] = row['packetId']
                    sample['attack_motion'] = row['words'][0]
                    sample['strike_from_self'] = row['attacker'] == player
                    sample['strike_targets_self'] = row['target'] == player
                    sample['strike_on_caster'] = row['attacker'] == row['target']
                    if caster := appearances.get(row['attacker']):
                        tile = [caster[k] for k in ('x','y','floor')]
                        sample['caster_position'] = tile
                        sample['caster_facing'] = caster['direction']
                        sample['caster_behind_origin'] = tile == [a-b for a,b in zip(sample['origin'],sample['delta'])]
                        dx,dy,dz = sample['delta']
                        sample['caster_faces_displacement'] = dz == 0 and caster['direction'] in range(8) and DELTAS[caster['direction']] == (dx,dy)
                    key = sample['map'],row['attacker']
                    if previous_displacement and previous_displacement[:2] == (key,sample['origin']):
                        sample['interval_ms'] = round((sample_at - previous_displacement[2])*1000)
                    previous_displacement = key,sample['destination'],sample_at
                    total += 1
                    results.append(sample)
    return dict(session=path.parent.name,count=total,observations=list(results))


@server.tool(annotations=READ_ONLY)
def sound_effect_observations(session: str = "latest",
                              sound: Annotated[int, Field(ge=1, le=32767)] | None = None,
                              limit: Annotated[int, Field(ge=1, le=100)] = 30) -> dict[str, Any]:
    """효과음 S19와 같은 연결에서 앞뒤로 관측된 패킷의 헤더를 조회합니다."""
    path = session_events(session)
    frames = {}
    awaiting_next = {}
    observations = deque(maxlen=limit)
    count = 0
    for row in recorded_rows(path):
        kind = row.get('kind')
        epoch = row.get('epoch')
        if kind == 'frame_header':
            frame = {field: row.get(field) for field in
                     ('packetId', 'time', 'map', 'direction', 'opcode', 'plainLength')}
            previous = awaiting_next.pop(epoch, None)
            if previous is not None:
                previous['nextFrame'] = frame
            frames.setdefault(epoch, deque(maxlen=3)).append(frame)
        elif kind == 'sound' and not row.get('bgm') and (sound is None or sound == row.get('id')):
            packet = row.get('packetId')
            preceding = list(frames.get(epoch, ()))
            if preceding and preceding[-1]['packetId'] == packet:
                preceding.pop()
            observation = {**{field: row.get(field) for field in
                              ('packetId', 'time', 'map', 'id', 'length')},
                           'trailingHex': (row.get('raw') or '')[6:],
                           'previousFrames': preceding[-2:], 'nextFrame': None}
            observations.append(observation)
            awaiting_next[epoch] = observation
            count += 1
    return {'session': path.parent.name, 'count': count, 'observations': list(observations)}


@server.tool(annotations=READ_ONLY)
def world_clock_observations(session: str = "latest") -> dict[str, Any]:
    """본서버의 접속 날짜, 날짜 알림, 시간·분 갱신을 조회합니다."""
    path = session_events(session)
    observations, hourly = deque(maxlen=24), deque(maxlen=24)
    count = 0
    previous_hour = None
    for row in recorded_rows(path):
        if row['kind']=='context_reset':
            previous_hour = None
        if row.get('opcode') not in (0x1E, 0x20) or not row.get('raw'):
            continue
        try:
            event = decode(bytes.fromhex(row['raw']))
        except (ValueError, IndexError):
            continue
        current = {**row, **event}
        if event['kind']=='world_hour':
            previous_hour = current
        elif event['kind']=='world_day_tick' and matches_hourly_clock(previous_hour,current):
            hourly.append(dict(time=previous_hour['time'], hour_packet=previous_hour['packetId'],
                               day_packet=current['packetId'], day=current['day']))
        observations.append({**{key: row[key] for key in ('time', 'map', 'packetId', 'raw')}, **event})
        count += 1
    return dict(session=path.parent.name,count=count,observations=list(observations),hourly_pulses=list(hourly))


def describe_changes(before, after, fields):
    added = changed = 0
    examples = []
    for key, row in after.items():
        previous = before.get(key)
        if row == previous:
            continue
        if previous is None:
            added += 1
        else:
            changed += 1
        if len(examples) < 8:
            example = {field: row[field] for field in fields if field in row}
            if previous is not None:
                example["changed_fields"] = sorted(field for field in row.keys() | previous.keys()
                                                   if row.get(field) != previous.get(field))
            if "Shop" in row:
                example["shop_items"] = len(row["Shop"])
            examples.append(example)
    return {"added": added, "changed": changed, "examples": examples}


@server.tool(annotations=READ_ONLY)
def preview_capture_import(session: str = "latest") -> dict[str, Any]:
    """기존 반영기의 규칙으로 선택한 기록을 메모리에서 재생하고 월드 데이터 차이를 미리 봅니다."""
    path = session_events(session)
    client = client_directory()
    world = Snapshot(LIVE, client)
    before_maps, before_portals = world.maps.copy(), world.portals.copy()
    before_doors = world.door_arrivals.copy()
    before_items, before_npcs = world.base_items | world.items, world.base_npcs | world.npcs
    before_monsters = world.base_monsters | world.monsters
    baseline_training_targets = training_target_baseline(world.base)
    before_training_targets = baseline_training_targets | world.training_targets
    skipped = 0

    def count_skip():
        nonlocal skipped
        skipped += 1

    capture = Capture()
    capture.read(path, world, on_skip=count_skip)
    return {
        "session": path.parent.name,
        "bytes_read": capture.offset,
        "skipped_records": skipped,
        "eligible_for_auto_sync": path.parent.name >= min(world.base.get("SourceSessions", ()), default=""),
        "changes": {
            "Maps": describe_changes(before_maps, world.maps, ("Id", "Name")),
            "Portals": describe_changes(before_portals, world.portals, ("Map", "X", "Y", "Floor", "Destination")),
            "DoorArrivals": describe_changes(before_doors, world.door_arrivals, ("Source", "Destination", "MapPadding", "Track", "MapPacket", "BuffStatusPacket", "OptionStatuses")),
            "Items": describe_changes(before_items, world.items, ("Key", "Name", "Sprite")),
            "Npcs": describe_changes(before_npcs, world.npcs, ("Id", "Name", "Map", "X", "Y", "Floor")),
            "Monsters": describe_changes(before_monsters, world.monsters, ("Name", "Map", "X", "Y", "Floor", "Sprite")),
            "TrainingTargets": describe_changes(before_training_targets, baseline_training_targets | world.training_targets,
                                                 ("Map", "X", "Y", "Floor", "Direction", "Packet")),
        },
    }


if __name__ == "__main__":
    server.run()
