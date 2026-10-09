# GitHub Desktop 저장소 목록을 읽는 방법 — 근거와 한계

`GitHubDesktopSource` 가 무엇을 믿고 경로를 돌려주는지 적어 둔다. 소스에는 이 경로 한 줄만 둔다.

## 1. 왜 이렇게까지 하나

GitHub Desktop 은 저장소 목록을 **IndexedDB** 에 둔다 — `%APPDATA%\GitHub Desktop\IndexedDB\file__0.indexeddb.leveldb\`.
내보내기 파일도, 읽어 갈 API 도 없다. 그리고 `.ldb` 블록이 **snappy 로 눌려 있어** 파일을 바이트로 뒤지면
경로 문자열이 **하나도** 안 나온다(2026-10-09 실측 — ASCII·UTF-16 양쪽 0건, 텍스트 로그에도 없음).

## 2. 어떻게 읽나

1. `IndexedDB` 아래의 `.ldb`·`.log`·`.sst` 를 **읽기 전용·공유 열기**로 연다(Desktop 이 켜져 있어도 된다).
2. `.ldb` 는 SSTable 이다 — 끝의 48바이트 footer 에서 index block 핸들을 읽고, index block 의 각 항목이 가리키는
   data block 을 차례로 연다. 블록 뒤에는 압축 종류 1바이트 + CRC 4바이트가 붙는다(`0`=없음 · `1`=snappy).
3. snappy 블록은 직접 푼다(`Snappy.TryDecode`). zstd 등 그 밖의 종류는 건너뛴다.
4. `.log`(WAL)는 눌려 있지 않아 통째로 훑는다.
5. 푼 바이트를 **Latin-1 과 UTF-16LE 두 가지로** 읽어(2바이트 문자열이 홀수 위치에서 시작할 수 있어 한 칸 밀어 한 번 더)
   `C:\…` 꼴을 정규식으로 줍는다.
6. ★ **주운 경로는 `.git` 이 실제로 있을 때만 돌려준다.** 레코드 형식(V8 직렬화)까지 풀지 않으므로
   **이 검증이 유일한 거름망**이다.

## 3. 한계 — 알고 쓰는 것

- **왜 맞는지**를 Desktop 의 자료 구조로 증명하지 않는다. 「경로처럼 생겼고 실제로 저장소다」까지다.
  Desktop 이 지운 저장소라도 폴더가 남아 있으면 목록에 오를 수 있다.
- Desktop 이 저장소를 한 번도 연 적 없으면 블록에 경로가 없어 **빈 목록 + 안내 한 줄**로 끝난다.
- Desktop 이 저장 형식을 바꾸면(압축 종류·DB 교체) 조용히 0건이 된다 — 그래서 **설정 화면에
  「지금 한 번 읽어 보기」**를 두어 사람이 바로 확인할 수 있게 했다.
- 목록이 0건이어도 **앱은 멈추지 않는다** — 루트 스캔 등 다른 출처로 계속 돈다.

## 4. 실측 (2026-10-09 · 개발 PC)

```text
설치 감지   %LOCALAPPDATA%\GitHubDesktop\GitHubDesktop.exe · bin\github.bat
읽기        .ldb 3개 + .log 1개 → 저장소 22건 · 80 ms
대조        같은 PC 의 루트 스캔(C:\DEV\Github, 깊이 3)은 23건
차이 1건    JHJ_VPN — 로컬에만 있고 Desktop 에는 등록돼 있지 않다(= 파일 시스템이 아니라 Desktop 목록을 읽고 있다는 증거)
```
