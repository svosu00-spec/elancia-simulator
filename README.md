# Elancia Local

**[릴리스 및 다운로드](https://github.com/svosu00-spec/elancia-simulator/releases)**

일랜시아 v611의 클라이언트·서버 통신을 공부하는 비공식 로컬 시뮬레이터입니다. 상점 구매, 이동, NPC 대화, 전투의 요청과 응답을 기록하고 `elancia-local` MCP에서 조회해 서버 코드와 비교할 수 있습니다.

현재 구현 범위는 로랜시아와 주변 사냥터·던전입니다. 다른 지역은 패킷기록기로 이동·NPC 대화·전투 등의 요청과 응답을 수집하고 분석한 뒤, 서버 코드와 월드 데이터에 반영해 구현해야 합니다. [패킷 기록과 MCP 사용법](docs/RECORDING-MCP.md)

사용 권한이 있는 클라이언트와 자료로 본인 PC의 `127.0.0.1`에서만 실습하세요. 외부에서 접속할 수 있도록 서버를 공개하지 마세요.

일랜시아의 저작권은 넥슨에 있습니다.

## 시작하기

1. GitHub Releases에서 서버와 접속기 ZIP을 받아 같은 상위 폴더에 풉니다. 실습에는 설치된 일랜시아 v611 클라이언트가 필요합니다.
2. 서버 폴더에서 `Start-Server.cmd`를 실행합니다. 기본 클라이언트 경로는 `C:\Nexon\Elancia`입니다.
3. `ElanciaLauncher.exe`에서 `127.0.0.1`을 입력합니다. `Register`로 실습 계정을 만들고 `Log in and play`로 접속합니다.

| 구성 | 용도 |
| --- | --- |
| ElanciaLocalServer | C# 콘솔 서버. 시작 로그, 포트 오류, 종료 상태를 콘솔에서 확인합니다. |
| ElanciaAccountManager | 서버 PC의 WinForms 관리기. 계정, 캐릭터, 갈리드, 장비, 소지품, 은행을 관리합니다. |
| ElanciaLauncher | 로컬 실습용 C++ Win32 접속기. `127.0.0.1`에서 테스트 계정을 만들고 로그인합니다. |
| ElanciaRecorder 및 elancia-local | 패킷 기록, MCP 조회와 반영 미리보기, 검토한 기록의 월드 데이터 반영에 사용합니다. |

## 빌드

Windows에서 .NET 10 SDK를 준비한 뒤 `ElanciaLocal.sln`을 엽니다. 솔루션 빌드는 서버와 계정 관리기를 컴파일합니다. 네이티브 접속기의 소스는 솔루션의 Native Launcher에서 열 수 있고, 32비트 MinGW-w64와 zlib를 사용하는 별도 스크립트로 빌드합니다.

```powershell
dotnet build .\ElanciaLocal.sln -c Release
.\Build-Release.ps1 -Version 0.1.0-preview.2
```

## 문서

- [서버·접속기·계정 관리기 설치와 사용](docs/SETUP.md)
- [패킷 기록과 MCP 조회, 서버 반영](docs/RECORDING-MCP.md)
- [릴리스 ZIP, 빌드, GitHub 공개 준비](docs/RELEASE.md)
- [학습용 이용 범위와 권리 안내 초안](docs/TERMS-DRAFT.md)
- [로컬 테스트 데이터 안내 초안](docs/PRIVACY-DRAFT.md)

