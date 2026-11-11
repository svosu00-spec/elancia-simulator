# GitHub 릴리스 준비

`v0.1.0-preview.2`는 일랜시아 v611의 로랜시아와 주변 사냥터·던전을 공부하는 로컬 실습용 미리보기입니다. 다른 지역은 패킷기록기로 요청과 응답을 수집하고 분석한 뒤 구현해야 합니다.

| 파일 | 내용 |
| --- | --- |
| `Elancia-Source-v0.1.0-preview.2.zip` | 솔루션, 서버·접속기·관리기 소스, 기록기·MCP, 안내 문서 |
| `Elancia-Server-v0.1.0-preview.2-win-x64.zip` | 자체 포함 콘솔 EXE, 시작 CMD와 월드 설정 |
| `Elancia-Launcher-v0.1.0-preview.2-win-x86.zip` | 동일한 PC의 서버에 연결하는 Win32 접속기 |
| `Elancia-AccountManager-v0.1.0-preview.2-win-x64.zip` | 자체 포함 WinForms 계정·캐릭터 관리기 |
| `Elancia-Recorder-MCP-v0.1.0-preview.2.zip` | 기록기, MCP, 월드 반영 스크립트와 기준 월드 데이터 |
| `SHA256SUMS.txt` | ZIP별 SHA-256 |

## 빌드

PowerShell 7, .NET 10 SDK, 접속기 빌드용 32비트 MinGW-w64와 zlib를 준비합니다.

```powershell
.\Build-Release.ps1 -Version 0.1.0-preview.2
```

결과는 `artifacts\releases\v0.1.0-preview.2`에 저장됩니다. 접속기와 서버 ZIP을 같은 상위 폴더에 풀면 접속기는 서버를 처음 시작할 때 생성되는 공개 지문 파일을 읽습니다. 실습 PC마다 접속기를 다시 빌드할 필요가 없습니다.

릴리스 서버는 빈 계정 데이터베이스에서 시작합니다. 소스 ZIP에서 직접 빌드해도 접속기에서 새로 만든 계정으로 로그인합니다. 런타임 로그, 원본 패킷 기록, 계정·캐릭터 저장 파일, 테스트 계정, 인증서와 암호 파일, 게임 실행 파일, 원본 Meta.dat와 그래픽은 ZIP에 포함하지 않습니다. 표준 서버의 장비 외형은 설치된 클라이언트의 원본 리소스를 사용합니다. 패키지 구성요소 고지는 각 실행 파일 옆의 `THIRD-PARTY.md`와 `notices` 폴더에 있습니다.

## 저장소와 Releases

소스 ZIP 안의 `ElanciaLocalServer` 폴더 내용을 저장소 루트에 둡니다. 해당 커밋에 `v0.1.0-preview.2` 태그를 붙이고, 위 표의 ZIP 5개와 `SHA256SUMS.txt`를 릴리스에 첨부합니다. 소스 ZIP은 체크섬에 포함된 빌드용 배포 파일입니다. 저장소 소스에 적용할 라이선스를 정하면 별도 `LICENSE`에 기재합니다. [GitHub Releases 관리 문서](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)

GitHub 릴리스 제목은 **Elancia Local v0.1.0-preview.2**로 하고, 본문은 `RELEASE-NOTES.md`를 사용합니다. [릴리스 본문](RELEASE-NOTES.md)

일랜시아의 저작권은 넥슨에 있습니다. 본인 PC의 `127.0.0.1`에서 학습용으로 실습하고, 외부에서 접속할 수 있도록 서버를 공개하지 마세요. [학습용 이용 안내](TERMS-DRAFT.md)

