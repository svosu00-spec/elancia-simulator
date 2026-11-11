# 로컬 시뮬레이터 사용법

본인 PC에서 테스트 계정으로 접속해 캐릭터 상태와 서버 응답을 살펴보는 실습입니다. 서버와 접속기를 같은 PC에서 실행하고 `127.0.0.1`로 연결하세요. 외부에서 접속할 수 있도록 서버를 공개하지 마세요.

## 같은 PC에서 서버 실행

1. Windows 10 또는 11 x64에서 서버와 접속기 ZIP을 같은 상위 폴더에 풉니다. 일랜시아 설치 경로에는 `Profiles\Meta.dat`와 `Maps` 폴더가 필요합니다.
2. `ElanciaLocalServer` 폴더에서 `Start-Server.cmd`를 실행합니다. 기본 설치 경로는 `C:\Nexon\Elancia`이고 기본 접속은 `127.0.0.1:7500`입니다. 포트와 초기화 결과가 콘솔에 표시됩니다. 종료하려면 서버 콘솔에서 Ctrl+C를 누릅니다.
3. 같은 PC와 Windows 계정에서 계정 관리기 ZIP의 `ElanciaAccountManager.exe`를 실행합니다. 실행 중인 시뮬레이터에 연결한 뒤 테스트 계정과 캐릭터를 선택합니다. 아이템·장비·은행·갈리드 및 캐릭터 소속은 대상 캐릭터가 로그아웃한 뒤 편집합니다.

설치 경로가 다르면 다음처럼 실행합니다. 시뮬레이터와 접속기는 같은 PC에서만 사용합니다.

```powershell
.\Start-Server.ps1 -ClientDir 'D:\Games\Elancia' -Bind 127.0.0.1
```

첫 실행에서 로컬 인증서 PFX와 Windows 계정에 연결된 DPAPI 암호 파일이 `data\public`에 만들어집니다. 서버 폴더에 생성되는 `certificate.sha256`에는 공개 지문만 들어갑니다. 접속기와 서버를 다른 상위 폴더에 풀었다면 이 지문 파일을 접속기 EXE 옆에 복사하세요. PC나 Windows 계정을 바꾼 뒤 새 인증서를 초기화했다면 지문 파일도 새로 복사합니다.

## 같은 PC에서 접속 테스트

1. 서버를 시작한 뒤 `ElanciaLauncher.exe`를 실행합니다. 같은 상위 폴더의 `ElanciaLocalServer\certificate.sha256`을 자동으로 읽습니다.
2. 접속기 주소에 `127.0.0.1`을 입력합니다.
3. `Register`로 실습 계정을 만든 후 `Log in and play`를 누릅니다. 초기 데이터베이스는 비어 있습니다. 새 ID는 영문·숫자 4~24자, 새 비밀번호는 영문·숫자 4~128자입니다. 공식 게임 계정과 다른 비밀번호를 사용합니다.
4. 기본 경로는 `C:\Nexon\Elancia\jElancia.exe`입니다. 설치 위치가 다르면 `Browse`에서 v611 실행 파일을 선택합니다.

접속기는 설치된 게임 리소스를 사용합니다. 접속기는 Windows 7 SP1과 TLS 1.2를 대상으로 빌드하며 Windows 7 실기 확인은 남아 있습니다. 서버와 계정 관리기는 Windows x64용입니다.
