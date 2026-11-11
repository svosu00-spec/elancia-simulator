"""Analyze a snapshot of an ongoing recording without stopping the recorder.
Writes derived data separately; never rewrites the live events/summary files.
"""
import argparse,collections,json
from pathlib import Path
from recorder_core import decode,dump,now

def review(session,output):
    session=Path(session);output=Path(output);output.mkdir(parents=True,exist_ok=True)
    rows=[];incomplete=0
    for line in (session/'events.jsonl').read_text(encoding='utf-8').splitlines():
        try:rows.append(json.loads(line))
        except ValueError:incomplete+=1
    inventory=[];equipment=[];self_looks=[];maps={};errors=[];all_counts=collections.Counter()
    for row in rows:
        op=row.get('opcode');raw=row.get('raw')
        if not raw:continue
        all_counts[f'0x{op:02X}']+=1
        if op in (0x0F,0x37):
            try:
                event=decode(bytes.fromhex(raw));event.update(time=row['time'],epoch=row['epoch'],map=row.get('map'))
                (inventory if op==0x0F else equipment).append(event)
            except (ValueError,IndexError) as e:errors.append({'opcode':op,'time':row['time'],'error':str(e)})
        elif op==0x33 and row.get('isSelf'):
            self_looks.append({k:row[k] for k in ('time','epoch','map','x','y','floor','appearance','equipment','flag1','flag2')})
        elif op==0x15:maps[row['map']]={k:row.get(k) for k in ('map','name','width','height','flags')}
    dump(output/'inventory-observations.json',inventory);dump(output/'equipment-observations.json',equipment)
    dump(output/'self-appearance-observations.json',self_looks);dump(output/'observed-maps.json',list(maps.values()))
    if self_looks:
        look=self_looks[-1]
        dump(output/'reference-look.json',{'source':str(session),'capturedAt':look['time'],
             'Appearance':look['appearance'].upper(),'Equipment':look['equipment'],'Flag1':look['flag1'],'Flag2':look['flag2'],
             'note':'Observed appearance for local visual comparison; no character name/account/OID. Not a complete character save.'})
    unique_inventory={(e['wireSprite'],e['name']) for e in inventory}
    unique_equipment={(e['slot'],e['wireSprite'],e['name']) for e in equipment}
    distinct_looks={(e['appearance'],tuple(e['equipment']),e['flag1'],e['flag2']) for e in self_looks}
    result={'reviewedAt':now(),'source':str(session),'snapshotEvents':len(rows),'incompleteLines':incomplete,
            'packetCounts':dict(all_counts),'maps':list(maps.values()),'inventoryPacketsDecoded':len(inventory),
            'equipmentPacketsDecoded':len(equipment),'distinctInventoryItems':len(unique_inventory),
            'distinctEquipmentSlotsAndItems':len(unique_equipment),'selfAppearancePackets':len(self_looks),
            'distinctSelfAppearances':len(distinct_looks),'decodeErrors':errors,
            'needsAppearanceChangeComparison':len(distinct_looks)<2,
            'notes':['Opening an equipment window is not proof of an equip/unequip operation.',
                     'Minimap UI activity cannot be confirmed from this packet log alone.',
                     'Appearance equipment words and inventory slot item sprites are different namespaces.']}
    dump(output/'review-summary.json',result)
    print(json.dumps(result,ensure_ascii=True,indent=2))
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('session');parser.add_argument('--output',required=True)
    args=parser.parse_args();review(args.session,args.output)
