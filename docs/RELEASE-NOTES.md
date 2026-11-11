일랜시아 v611의 클라이언트·서버 통신을 공부하는 비공식 로컬 시뮬레이터 미리보기입니다. 로랜시아와 주변 사냥터·던전을 구현했습니다. 다른 지역은 패킷기록기로 요청과 응답을 수집하고 분석해 서버에 구현해야 합니다.

## 이번 버전

- 베타·감마 로랜시아에서 성당으로 들어가는 세 입구와 마을로 돌아오는 경로를 수정했습니다.
- 포탈 도착 칸을 다른 캐릭터가 차지하고 있으면 같은 층의 인접한 통행 가능 칸으로 이동합니다.
- 계정 DB는 비어 있는 상태로 시작합니다. 접속기에서 새 실습 계정을 만들어 로그인합니다.

## 다운로드

| 파일 | 용도 |
| --- | --- |
| `Elancia-Server-v0.1.0-preview.2-win-x64.zip` | Windows x64 콘솔 서버 |
| `Elancia-Launcher-v0.1.0-preview.2-win-x86.zip` | 계정 생성과 접속을 위한 Win32 접속기 |
| `Elancia-AccountManager-v0.1.0-preview.2-win-x64.zip` | 계정·캐릭터·인벤토리 관리기 |
| `Elancia-Recorder-MCP-v0.1.0-preview.2.zip` | 패킷 기록기, MCP 조회와 서버 반영 도구 |
| `Elancia-Source-v0.1.0-preview.2.zip` | Visual Studio 솔루션, 구성요소 소스, 설치 및 MCP 문서 |
| `SHA256SUMS.txt` | ZIP 5개의 SHA-256 체크섬 |

## 실행

설치된 일랜시아 v611 클라이언트가 필요합니다. 서버와 접속기 ZIP을 같은 상위 폴더에 풀고, 서버 폴더의 `Start-Server.cmd`를 실행하세요. 접속기에서 `127.0.0.1`을 입력하고 `Register`로 실습 계정을 만든 뒤 `Log in and play`를 누릅니다. 서버 ZIP의 `SETUP.md`와 기록기 ZIP의 `RECORDING-MCP.md`에서 자세한 절차를 볼 수 있습니다.

본인 PC에서 학습용으로 사용하세요. 외부에서 접속할 수 있도록 서버를 공개하지 마세요. 일랜시아의 저작권은 넥슨에 있습니다.
