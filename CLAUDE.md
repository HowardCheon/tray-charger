# TrayCharger - 프로젝트 가이드

## 개요
Windows 11 시스템 트레이에 노트북 배터리 충전전류를 실시간 표시하는 경량 앱.

## 기술 스택
- **언어**: C# (C# 5, .NET Framework 4.x)
- **컴파일러**: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (Windows 내장)
- **의존성**: 없음 (System.Windows.Forms, System.Drawing만 사용)

## 빌드
```
build.bat
```
또는 직접:
```
csc /target:winexe /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:TrayCharger.exe TrayCharger.cs
```

## 아키텍처
단일 파일 (`TrayCharger.cs`) 구조:

| 영역 | 설명 |
|------|------|
| P/Invoke | Win32 API 선언 (powrprof, setupapi, kernel32) |
| Battery Reading | `CallNtPowerInformation` → 전력(W), `IOCTL_BATTERY_QUERY_STATUS` → 전압(V), 전류 = W/V |
| Icon | GDI+ GraphicsPath로 숫자 아이콘 동적 생성. 흰색 외곽선 + 검정(충전)/빨강(방전) |
| Auto-Start | `HKCU\...\Run` 레지스트리 토글 |
| UI | NotifyIcon + ContextMenuStrip, 3초 타이머 갱신 |

## 주의사항
- C# 5 문법만 사용 (`?.`, `$""`, `nameof` 등 C# 6+ 기능 사용 금지)
- `Icon.FromHandle` → `Clone()` → `DestroyIcon` 패턴으로 메모리 누수 방지
- 단일 인스턴스 보장: `Mutex`
- 전압 읽기 실패 시 W 표시로 fallback
