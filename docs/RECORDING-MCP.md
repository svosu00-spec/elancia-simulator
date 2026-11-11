# 패킷 기록과 MCP

상점 구매처럼 동작 하나를 골라 클라이언트 요청, 서버 응답, 화면 변화를 차례로 비교하는 실습입니다. MCP로 관련 패킷을 찾은 뒤 서버 코드에서 그 요청을 처리하는 부분을 확인할 수 있습니다.

## 기록기 시작

서버 ZIP과 기록기·MCP ZIP을 같은 상위 폴더에 풉니다. 공통 `ElanciaLocalServer` 폴더에 파일이 합쳐집니다. 소스 ZIP으로 실습할 때는 `ElanciaLocalServer/recorder/`가 아래의 `ElanciaRecorder/`에 해당합니다.

```text
workspace/
  ElanciaLocalServer/
    tools/world_mcp.py
    tools/sync_recorded_world.py
    world-recorded.json
    world-npcs.json
  ElanciaRecorder/
    recorder.py
    capture.js
    captures/
```

Windows에서 `uv`와 Python 3.12를 사용합니다. 패킷 분석 실습에는 직접 만든 예시나 사용 권한이 있는 기록을 사용합니다. 기록기를 실행하는 경우 허락받은 테스트 환경의 v611 클라이언트만 대상으로 지정합니다. 실행 파일 경로가 기본 설치 위치와 다르면 `--client-dir`을 지정합니다. 동일한 경로의 클라이언트가 여러 개 열려 있으면 작업 관리자의 세부 정보에서 기록할 PID를 확인합니다.

```powershell
uv run --no-project --python 3.12 --with frida==17.26.0 python .\ElanciaRecorder\recorder.py --auto-start
uv run --no-project --python 3.12 --with frida==17.26.0 python .\ElanciaRecorder\recorder.py --auto-start --target-pid 1234
```

연결 완료 후 전후 상태를 비교할 수 있게 행동을 한 번씩 실행합니다. 상점이면 대화 열기, 목록 열기, 구매, 가방 갱신을 순서대로 남깁니다. 전투면 공격 직전의 장비·능력치, 명중, 처치, 드랍을 남깁니다. 끝나면 **기록 종료**를 누르고 `captures\<세션 ID>\events.jsonl`과 `summary.json`을 확인합니다. 기록기에는 귓말과 1:1 대화 본문이 남을 수 있으므로 원본 기록은 실습 PC에서만 검토합니다.

## MCP 등록

Codex에서 아래 예시의 경로를 자신의 서버 폴더로 바꾸고 실행한 뒤 새 채팅을 엽니다. MCP는 로컬 표준 입출력으로 동작합니다.

```powershell
codex mcp add elancia-local --env PYTHONUTF8=1 -- uv run --script 'D:/Elancia/ElanciaLocalServer/tools/world_mcp.py'
codex mcp list
```

설치 경로 또는 기록 폴더가 다르면 등록할 때 `--env ELANCIA_CLIENT_DIR=D:/Games/Elancia`, `--env ELANCIA_CAPTURE_DIR=D:/Elancia/ElanciaRecorder/captures`를 추가합니다. `ELANCIA_CAPTURE_DIR`은 MCP가 조회할 위치입니다. 기록기는 자체 폴더 아래 `captures`에 저장합니다.

| MCP 도구 | 확인할 내용 |
| --- | --- |
| `list_captures` | 최근 세션 ID |
| `capture_summary` | 기록 범위와 미해석 opcode |
| `npc_dialog_observations` | 대화 응답, 선택지, 길드기 접근·새로고침 |
| `training_target_observations` | 인형 설치·타격·파괴와 어빌 카운터, `map_id: null`로 모든 맵의 설치형 인형 조회 |
| `monster_attack_cadence_observations` | 첫 반격, 연속 근접 타격, 추격 뒤 첫 타격의 시간과 원본 패킷 |
| `merc_packet_observations` | 용병 가방의 보관·인출·이동 요청과 S61 및 가방 변경 응답 |
| `ability_mastery_markers` | S3F 헤더 수와 확보된 본문 |
| `ground_pickup_observations` | 줍기 요청 좌표, 이전 슬롯 위치, 바닥 제거와 인벤토리 응답 |
| `online_fame_observations` | 접속 명성 주기, 차원, S08 형식, 조작·사망 기록 없이 명성이 6 오른 표본 |
| `preview_capture_import` | 특정 세션을 적용할 경우 달라질 월드 데이터 |
| `world_snapshot_status` | 현재 월드 반영 파일의 상태 |

## 검토한 기록을 서버에 적용

1. `list_captures`에서 세션 ID를 확인합니다. `preview_capture_import`에 ID를 전달하고 맵·포탈·상점·NPC·몬스터의 변경 내용을 확인합니다.
2. 서버 폴더의 `data\world-live.json`이 있으면 별도 위치에 복사해 둡니다. 다음 명령으로 검토한 세션을 한 번 반영합니다.

```powershell
Set-Location 'D:\Elancia\ElanciaLocalServer'
uv run --no-project python .\tools\sync_recorded_world.py --client-dir 'C:\Nexon\Elancia' --session 'YYYYMMDD-HHMMSS-ffffff'
```

3. 서버 로그에서 `Recorded world reloaded`를 확인하고 해당 위치나 상점을 다시 엽니다. 아직 해석하지 못한 패킷은 별도로 분석해 서버 처리와 테스트를 수정해야 합니다.

`--output`에 다른 경로를 주면 별도 파일을 만들 수 있습니다. `--watch`는 기록 폴더를 지속적으로 읽는 개발용 모드입니다. `--session`으로 오래된 기록을 지정하면 과거 상점 가격이나 대화가 다시 들어올 수 있으므로 미리보기에서 비교합니다. 테스트 기록을 반영 대상에서 제외하려면 그 세션 폴더에 빈 `exclude-from-live-import` 파일을 만듭니다. MCP의 조회와 미리보기는 서버 파일을 변경하지 않습니다.
