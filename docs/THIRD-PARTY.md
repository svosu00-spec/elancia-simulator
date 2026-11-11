# 제3자 구성요소

서버와 계정 관리기에는 .NET 런타임이 들어갑니다. 계정 관리기는 Windows Desktop 런타임을 사용합니다. 배포본의 `notices` 폴더에 빌드 환경에서 제공된 런타임 고지문을 함께 넣습니다.

- Microsoft.Data.Sqlite 10.0.14 및 Microsoft.Data.Sqlite.Core 10.0.14. [NuGet 패키지](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.14)
- SQLitePCLRaw 2.1.13 및 SQLite. [SQLitePCLRaw 라이선스](https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT) · [SQLite 저작권 안내](https://sqlite.org/copyright.html)
- Win32 접속기에 사용된 MinGW-w64, GCC 런타임, zlib. [MinGW-w64](https://www.mingw-w64.org/) · [GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1.html) · [zlib](https://zlib.net/zlib_license.html)

일랜시아의 저작권은 넥슨에 있습니다.
