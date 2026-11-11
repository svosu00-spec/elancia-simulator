"""User-controlled session recorder. Observe only the explicitly selected client path."""
from __future__ import annotations
import argparse, ctypes, datetime, hashlib, ipaddress, json, os, queue, threading, time, traceback, zlib
from pathlib import Path
import frida
from recorder_core import (EXPECTED_SHA256, PARTY_NOTICE_LIMIT, PARTY_NOTICE_WORDS, SERVER_OPCODES, STAGES,
                           Store, collect_assets, decoration_catalog, dump)

ROOT=Path(__file__).resolve().parent
DEFAULT_CLIENT=Path(r'C:\Nexon\Elancia')
kernel32=ctypes.WinDLL('kernel32',use_last_error=True)
kernel32.OpenProcess.argtypes=[ctypes.c_uint32,ctypes.c_int,ctypes.c_uint32];kernel32.OpenProcess.restype=ctypes.c_void_p
kernel32.QueryFullProcessImageNameW.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_uint32)]
kernel32.QueryFullProcessImageNameW.restype=ctypes.c_int
kernel32.CloseHandle.argtypes=[ctypes.c_void_p];kernel32.CloseHandle.restype=ctypes.c_int


ntdll=ctypes.WinDLL('ntdll')
ntdll.NtQueryInformationProcess.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.POINTER(ctypes.c_uint32)]
ntdll.NtQueryInformationProcess.restype=ctypes.c_long
shell32=ctypes.WinDLL('shell32')
shell32.CommandLineToArgvW.argtypes=[ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_int)]
shell32.CommandLineToArgvW.restype=ctypes.POINTER(ctypes.c_wchar_p)
kernel32.LocalFree.argtypes=[ctypes.c_void_p];kernel32.LocalFree.restype=ctypes.c_void_p


class UnicodeString(ctypes.Structure):
    _fields_=[('length',ctypes.c_uint16),('maximum_length',ctypes.c_uint16),('buffer',ctypes.c_void_p)]


def process_arguments(pid):
    handle=kernel32.OpenProcess(0x1000,False,pid)
    if not handle:return None
    try:
        size=ctypes.c_uint32()
        ntdll.NtQueryInformationProcess(handle,60,None,0,ctypes.byref(size))
        if not ctypes.sizeof(UnicodeString)<=size.value<=131072:return None
        data=ctypes.create_string_buffer(size.value)
        if ntdll.NtQueryInformationProcess(handle,60,data,size.value,ctypes.byref(size))!=0:return None
        text=UnicodeString.from_buffer(data)
        start=ctypes.addressof(data)
        if not text.buffer or not text.length or text.length%2 or not (
            start<=text.buffer and text.buffer+text.length<=start+ctypes.sizeof(data)
        ):return None
        count=ctypes.c_int()
        argv=shell32.CommandLineToArgvW(ctypes.wstring_at(text.buffer,text.length//2),ctypes.byref(count))
        if not argv:return None
        try:return tuple(argv[i] for i in range(count.value))
        finally:kernel32.LocalFree(argv)
    finally:kernel32.CloseHandle(handle)


def loopback_redirected(arguments):
    for argument in arguments[1:]:
        if not argument.casefold().startswith('addr'):continue
        host=argument[4:].rsplit(':',1)[0]
        try:
            if ipaddress.ip_address(host).is_loopback:return True
        except ValueError:
            continue
    return False


def executable_path(pid):
    handle=kernel32.OpenProcess(0x1000,False,pid)
    if not handle:return None
    try:
        size=ctypes.c_uint32(32768);buffer=ctypes.create_unicode_buffer(size.value)
        if kernel32.QueryFullProcessImageNameW(handle,0,buffer,ctypes.byref(size)):return Path(buffer.value).resolve()
    finally:kernel32.CloseHandle(handle)
    return None

class Engine:
    def __init__(self,client_dir,ui,collect=True,chat_notice_matches=(),target_pid=None):
        self.client=Path(client_dir).resolve()/'jElancia.exe';self.ui=ui;self.collect=collect
        self.chat_notice_matches=chat_notice_matches
        self.target_pid=target_pid
        self.stop=threading.Event();self.incoming=queue.Queue(maxsize=10000);self.commands=queue.Queue()
        self.dropped=0;self.thread=None;self.directory=None;self.last_summary={};self.attached_pid=None
        self.ready=False;self.failure=None
    def tell(self,kind,**data):self.ui.put({'kind':kind,**data})
    def start(self):self.thread=threading.Thread(target=self.run,daemon=False);self.thread.start()
    def on_message(self,message,data):
        try:self.incoming.put_nowait((message,data))
        except queue.Full:self.dropped+=1
    def run(self):
        session=None;script=None;store=None
        try:
            self.tell('status',text='클라이언트 버전 확인 중…')
            if not self.client.is_file():raise RuntimeError('클라이언트 파일이 없습니다: '+str(self.client))
            digest=hashlib.sha256(self.client.read_bytes()).hexdigest()
            if digest!=EXPECTED_SHA256:raise RuntimeError('확인된 클라이언트 버전과 다릅니다. 후킹 주소를 다시 확인해야 합니다.')
            (ROOT/'captures').mkdir(exist_ok=True)
            self.directory=ROOT/'captures'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
            decoration_keys={}
            try:
                furniture=decoration_catalog(self.client.parent/'Profiles'/'Meta.dat')
                decoration_keys={wire:entry['catalogKey'] for wire,entry in furniture.items()}
            except (OSError,ValueError,zlib.error):
                self.tell('log',text='가구 메타데이터를 읽을 수 없어 가구 키는 가립니다.')
            store=Store(self.directory,decoration_keys=decoration_keys,chat_notice_matches=self.chat_notice_matches)
            dump(self.directory/'session.json',{'client':str(self.client),'sha256':digest,'pid':None,'mode':'passive_observer'})
            self.tell('directory',path=str(self.directory))
            if self.collect:
                self.tell('status',text='맵 파일·메타데이터 목록 수집 중…')
                maps,meta=collect_assets(self.client.parent,self.directory/'assets')
                store.write({'kind':'assets_indexed','mapFiles':maps,'metadataEntries':meta})
                self.tell('log',text=f'기존 자료 확인: 맵 {maps}개, 메타데이터 {meta}개')
            source=(ROOT/'capture.js').read_text(encoding='utf-8')
            device=frida.get_local_device();rejected=set();detached=threading.Event();next_scan=0;next_flush=0;summary_failures=()
            self.tell('status',text='접속 대기 — 홈페이지에서 게임을 실행하세요.')
            while not self.stop.is_set():
                if session is not None and detached.is_set():
                    store.reset('process_detached');self.tell('log',text='게임 연결 해제. 다음 실행을 기다립니다.')
                    session=None;script=None;self.attached_pid=None;self.ready=False
                    self.tell('status',text='접속 대기 — 게임이 종료되었거나 기록 연결이 해제됐습니다.')
                if session is None and time.monotonic()>=next_scan:
                    next_scan=time.monotonic()+1
                    candidates=[];local_clients=0;unverified=0
                    for process in device.enumerate_processes():
                        if process.name.lower()!='jelancia.exe' or process.pid in rejected:continue
                        if self.target_pid is not None and process.pid!=self.target_pid:continue
                        path=executable_path(process.pid)
                        if path is None or str(path).casefold()!=str(self.client).casefold():continue
                        arguments=process_arguments(process.pid)
                        if arguments is None:unverified+=1
                        elif loopback_redirected(arguments):local_clients+=1
                        else:candidates.append(process.pid)
                    if len(candidates)>1:
                        self.tell('status',text='동일 경로의 게임이 여러 개입니다. 기록할 게임 하나만 남겨 주세요.')
                    elif len(candidates)==1:
                        pid=candidates[0];rejected.add(pid);detached.clear()
                        try:
                            self.tell('status',text=f'기록 연결 중… PID {pid}')
                            if hashlib.sha256(self.client.read_bytes()).hexdigest()!=EXPECTED_SHA256:
                                raise RuntimeError('게임 실행 중 클라이언트 파일이 갱신됐습니다. 버전을 다시 확인해야 합니다.')
                            session=device.attach(pid);session.on('detached',lambda *a:detached.set())
                            store.reset('client_attached');self.attached_pid=pid
                            config={'serverOpcodes':sorted(SERVER_OPCODES),'attachId':store.epoch,
                                    'decorationKeys':[wire.hex() for wire in decoration_keys],
                                    'partyNoticeLimit':PARTY_NOTICE_LIMIT,
                                    'partyNoticeWords':[(word,list(word.encode('cp949'))) for word in PARTY_NOTICE_WORDS],
                                    'chatNoticeMatches':[list(pattern) for pattern in self.chat_notice_matches]}
                            script=session.create_script(source.replace('__CONFIG__',json.dumps(config)))
                            script.on('message',self.on_message);script.load()
                            dump(self.directory/'session.json',{'client':str(self.client),'sha256':digest,'pid':pid,'mode':'passive_observer'})
                        except Exception as exc:
                            if session is not None:
                                try:session.detach()
                                except Exception:pass
                            session=None;script=None;self.attached_pid=None
                            self.tell('status',text='기록 연결 실패 — 게임을 변경하지 않고 중단했습니다.')
                            self.tell('log',text=f'{type(exc).__name__}: {exc}')
                            store.write({'kind':'attach_error','error':type(exc).__name__})
                    elif local_clients or unverified:
                        self.tell('status',text='본서버 대기 — 로컬 접속 또는 시작 옵션을 확인할 수 없는 클라이언트는 제외합니다.')
                # A single writer preserves packet order and avoids interleaved JSON lines.
                for _ in range(2000):
                    try:message,data=self.incoming.get_nowait()
                    except queue.Empty:break
                    if message.get('type')=='send':
                        payload=message.get('payload',{})
                        if payload.get('type')=='ready':
                            self.ready=True;self.tell('status',text=f'기록 중 — PID {self.attached_pid} / 이제 게임에서 진행하세요.')
                            self.tell('log',text='송수신 기록 연결 완료. 귓말·1:1 채팅 본문을 저장합니다.')
                            store.write({'kind':'hooks_ready','pid':self.attached_pid})
                        else:store.handle(payload,data)
                    elif message.get('type')=='error':
                        store.write({'kind':'hook_error','error':message.get('description','unknown')})
                        raise RuntimeError('기록 후크 오류: '+message.get('description','unknown'))
                while not self.commands.empty():
                    command,value=self.commands.get_nowait()
                    if command=='stage':store.stage=value;store.write({'kind':'stage_marker','label':value});self.tell('log',text='단계 표시: '+value)
                    elif command=='marker':store.write({'kind':'manual_marker','label':store.stage});self.tell('log',text='현재 시점 표시 저장')
                if time.monotonic()>=next_flush:
                    store.dropped=self.dropped
                    failed=store.flush()
                    if failed and failed!=summary_failures:
                        self.tell('log',text='요약 저장 대기 중. 패킷 기록은 계속됩니다.')
                    summary_failures=failed
                    self.last_summary=store.summary();self.tell('summary',data=self.last_summary)
                    next_flush=time.monotonic()+2
                self.stop.wait(.05)
        except Exception as exc:
            self.failure=str(exc);self.tell('error',text=str(exc))
            if store is not None:
                try:store.write({'kind':'recorder_error','error':str(exc)})
                except OSError:pass
        finally:
            if script is not None:
                try:
                    last=script.exports_sync.stop()
                    if store is not None:store.handle({'type':'stats',**last})
                except Exception:pass
            if session is not None:
                try:session.detach()
                except Exception:pass
            if store is not None:
                try:
                    while not self.incoming.empty():
                        message,data=self.incoming.get_nowait()
                        if message.get('type')=='send':store.handle(message.get('payload',{}),data)
                    store.dropped=self.dropped;store.close();self.last_summary=store.summary()
                    self.tell('summary',data=self.last_summary)
                except OSError as exc:self.failure=str(exc);self.tell('error',text='파일 저장 오류: '+str(exc))
            self.tell('stopped')

def gui(client_dir,auto_start=False,chat_notice_matches=(),target_pid=None):
    import tkinter as tk
    from tkinter import ttk, messagebox
    kernel32.CreateMutexW.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_wchar_p];kernel32.CreateMutexW.restype=ctypes.c_void_p
    ctypes.set_last_error(0)
    instance=kernel32.CreateMutexW(None,False,'Local\\ElanciaRecorderDesktop45')
    already_running=ctypes.get_last_error()==183
    if not instance:raise ctypes.WinError(ctypes.get_last_error())
    if already_running:
        messagebox.showinfo('일랜시아 자료 기록기','기록기가 이미 실행 중입니다. 열린 기록기 창을 사용해 주세요.')
        kernel32.CloseHandle(instance);return
    root=tk.Tk();root.title('일랜시아 자료 기록기');root.geometry('780x590');root.minsize(680,520)
    events=queue.Queue();engine=None;closing=False;last_dir=None
    main=ttk.Frame(root,padding=18);main.pack(fill='both',expand=True)
    ttk.Label(main,text='일랜시아 자료 기록기',font=('맑은 고딕',18,'bold')).pack(anchor='w')
    ttk.Label(main,text='대상: '+str(Path(client_dir)/'jElancia.exe')).pack(anchor='w',pady=(7,3))
    ttk.Label(main,text='귓말·1:1 채팅 본문을 저장합니다. 계정·비밀번호·일반 채팅·접속 토큰은 제외합니다.').pack(anchor='w')
    status=tk.StringVar(value='준비됨 — 아래 [기록 시작]을 먼저 누르세요.')
    ttk.Label(main,textvariable=status,font=('맑은 고딕',11,'bold'),wraplength=720).pack(anchor='w',pady=16)
    bar=ttk.Frame(main);bar.pack(fill='x')
    def start():
        nonlocal engine
        if engine is not None and engine.thread.is_alive():return
        engine=Engine(client_dir,events,chat_notice_matches=chat_notice_matches,target_pid=target_pid);start_button.configure(state='disabled');stop_button.configure(state='normal');engine.start()
    def stop():
        if engine is not None:status.set('기록을 마무리하는 중…');engine.stop.set();stop_button.configure(state='disabled')
    def folder():
        path=last_dir or ROOT/'captures';Path(path).mkdir(exist_ok=True);os.startfile(str(path))
    start_button=ttk.Button(bar,text='기록 시작',command=start);start_button.pack(side='left',padx=(0,8))
    stop_button=ttk.Button(bar,text='기록 종료',command=stop,state='disabled');stop_button.pack(side='left',padx=(0,8))
    ttk.Button(bar,text='저장 폴더 열기',command=folder).pack(side='left')
    steps=ttk.LabelFrame(main,text='행동을 자동 분류합니다 — 수동 단계/시점 표시는 선택 사항',padding=10);steps.pack(fill='x',pady=14)
    stage=tk.StringVar(value=STAGES[0]);combo=ttk.Combobox(steps,textvariable=stage,values=STAGES,state='readonly',width=36);combo.pack(side='left')
    def select_stage(event=None):
        if engine is not None and engine.thread.is_alive():engine.commands.put(('stage',stage.get()))
    combo.bind('<<ComboboxSelected>>',select_stage)
    ttk.Button(steps,text='현재 시점 표시',command=lambda:engine.commands.put(('marker',None)) if engine and engine.thread.is_alive() else None).pack(side='left',padx=10)
    stats=tk.StringVar(value='이벤트 0 · 맵 0 · 오브젝트 관찰 위치 0 · 외형 0')
    ttk.Label(main,textvariable=stats,wraplength=720).pack(anchor='w')
    location=tk.StringVar(value='맵 / 좌표: 아직 수신하지 않음')
    ttk.Label(main,textvariable=location).pack(anchor='w',pady=(5,10))
    log=tk.Text(main,height=10,wrap='word',state='disabled',font=('맑은 고딕',10));log.pack(fill='both',expand=True)
    ttk.Label(main,text='생성·이동·NPC·장비·전투를 자동 분류하고 분석 파일을 2초마다 갱신합니다.\n연결 완료 후 평소처럼 플레이하세요. 기록 종료 후에도 게임은 계속 실행됩니다.').pack(anchor='w',pady=(10,0))
    def append(text):
        log.configure(state='normal');log.insert('end',datetime.datetime.now().strftime('%H:%M:%S')+' '+text+'\n');log.see('end');log.configure(state='disabled')
    def close():
        nonlocal closing
        closing=True;stop()
        if engine is None or not engine.thread.is_alive():root.destroy()
    def poll():
        nonlocal last_dir
        while not events.empty():
            event=events.get_nowait();kind=event['kind']
            if kind=='status':status.set(event['text'])
            elif kind=='log':append(event['text'])
            elif kind=='directory':last_dir=event['path'];append('저장: '+last_dir)
            elif kind=='error':status.set('기록 오류 — 아래 내용을 확인하세요.');append(event['text'])
            elif kind=='summary':
                s=event['data'];stage.set(s['stage']);stats.set(f"행동 {s['actionsObserved']} · 프레임 {sum(s['frameCounts'].values())} · 맵 {s['mapsObserved']} · 오류 {s['parseErrors']+s['hookErrors']} · 헤더 불일치 {s['frameMismatches']} · 누락 {s['droppedEvents']}")
                p=s['position'];location.set(f"맵 {s['currentMap']} / 좌표 "+(f"({p['x']}, {p['y']}) 층 {p['floor']}" if p else '아직 수신하지 않음'))
            elif kind=='stopped':
                start_button.configure(state='normal');stop_button.configure(state='disabled')
                if engine and not engine.failure:status.set('기록 종료 — 저장 완료. 게임에는 영향을 주지 않습니다.')
                append('기록 세션 종료')
        if closing and (engine is None or not engine.thread.is_alive()):root.destroy();return
        root.after(100,poll)
    root.protocol('WM_DELETE_WINDOW',close);root.after(100,poll)
    if auto_start:root.after(250,start)
    root.mainloop()
    kernel32.CloseHandle(instance)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--client-dir',default=str(DEFAULT_CLIENT))
    parser.add_argument('--auto-start',action='store_true',help='Start the observer and wait for the chosen client')
    parser.add_argument('--chat-notice-match',action='append',default=[],type=bytes.fromhex,help='Record S0A 00 notices containing these test bytes (hex)')
    parser.add_argument('--smoke-seconds',type=float,default=0,help='Developer: passive attachment check, no game actions')
    parser.add_argument('--target-pid',type=int,help='Observe only this verified game process when multiple clients are open')
    args=parser.parse_args()
    if args.target_pid is not None and args.target_pid<=0:
        parser.error('--target-pid must be positive')
    if any(not 1<=len(pattern)<=255 for pattern in args.chat_notice_match):
        parser.error('--chat-notice-match must contain 1–255 bytes')
    if args.smoke_seconds:
        q=queue.Queue();engine=Engine(args.client_dir,q,collect=False,chat_notice_matches=args.chat_notice_match,target_pid=args.target_pid);engine.start();deadline=time.monotonic()+args.smoke_seconds
        while time.monotonic()<deadline and engine.thread.is_alive():
            try:print(json.dumps(q.get(timeout=.25),ensure_ascii=True),flush=True)
            except queue.Empty:pass
        was_ready=engine.ready;engine.stop.set();engine.thread.join(timeout=10)
        print(json.dumps({'ready':was_ready,'error':engine.failure,'directory':str(engine.directory)}))
        raise SystemExit(0 if was_ready and not engine.failure else 1)
    else:
        try:gui(args.client_dir,args.auto_start,args.chat_notice_match,args.target_pid)
        except Exception:
            (ROOT/'startup-error.log').write_text(traceback.format_exc(),encoding='utf-8');raise
