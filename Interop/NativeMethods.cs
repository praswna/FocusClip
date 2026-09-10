using System;
using System.Runtime.InteropServices;

namespace FocusClip.Interop;

/// <summary>
/// Win32 P/Invoke 선언 모음. (★ 핸들/포인터는 IntPtr 로 선언해 64비트에서 잘리지 않게 함 —
/// 기존 Clipboard-Manager 의 ctypes 핸들 잘림 버그 재발 방지)
/// </summary>
internal static class NativeMethods
{
    // 로우레벨 키보드 후크
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int VK_CAPITAL = 0x14; // CapsLock
    public const int VK_OEM_3 = 0xC0;   // ` (백틱) — 도크 표시 중 탐색기 폴더 순환

    // 창 확장 스타일 (포커스 비탈취 팝업)
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020; // 히트테스트 제외 → 마우스 입력이 아래 창으로 통과

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    // 로우레벨 마우스 후크 (사이드바 자동 숨김: 화면 왼쪽 가장자리 진입 감지)
    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200;

    // MSLLHOOKSTRUCT 의 선두 8바이트가 POINT pt(x, y) 다. 후크 콜백은 마우스가
    // 움직일 때마다 시스템 전역에서 불리므로 구조체 전체를 마샬링하지 않고
    // Marshal.ReadInt32 로 필요한 두 필드만 직접 읽는다.
    public const int MSLLHOOKSTRUCT_X_OFFSET = 0;
    public const int MSLLHOOKSTRUCT_Y_OFFSET = 4;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // 마우스 버튼 눌림 상태(오토클로즈 외부 클릭 감지)
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // 토글 상태(CapsLock 등)는 GetKeyState 의 최하위 비트로 읽는다(0x0001 set = 켜짐).
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // x64 대상: GetWindowLongPtr/SetWindowLongPtr 사용 (스타일 비트 64비트 안전)
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ── 아이콘 추출 ──
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[]? piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── 창 활성화/제어 ──
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ── 최상위 창 열거 (AHK 의 ahk_exe 매칭 대응 — 실행 파일명으로 창 찾기) ──
    // Chromium/Electron/WebView2 계열(예: ChatGPT 데스크톱)은 보이는 창을 소유한 프로세스가
    // Process.MainWindowHandle 을 노출하지 않는 경우가 있어, 그 앱들은 프로세스 단위 조회로는
    // 창을 못 찾는다. 대신 시스템의 모든 최상위 창을 열거해 프로세스 ID 로 직접 매칭한다.
    public const uint GW_OWNER = 4;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>창 클래스명. 탐색기 폴더 창(CabinetWClass)을 같은 explorer.exe 소유의
    /// 바탕화면(Progman)·작업표시줄과 구분하는 데 쓴다.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ── 항상 위(TopMost) 토글 ──
    public const int WS_EX_TOPMOST = 0x00000008;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ── 포그라운드 창 변경 이벤트 후크 (사이드바 topmost 재확인 트리거) ──
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ── 클립보드 변경 리스너 ──
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ── 붙여넣기(Ctrl+V) 합성 ──
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_HANGUL = 0x15;
    public const byte VK_V = 0x56;

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // FocusClip 이 합성한 CapsLock 토글에 붙이는 표식. 후크가 dwExtraInfo 로 자기 입력을 구별해 무시한다.
    public const long CAPS_SYNTH_TAG = 0x4643CA95;

    /// <summary>CapsLock 토글을 합성(대소문자 전환). CAPS_SYNTH_TAG 표식이 붙어 단축키 후크는 이 입력을 무시한다.</summary>
    public static void ToggleCapsLock()
    {
        UIntPtr tag = (UIntPtr)(ulong)CAPS_SYNTH_TAG;
        keybd_event((byte)VK_CAPITAL, 0x3A, 0, tag);                 // down (0x3A = CapsLock 스캔코드)
        keybd_event((byte)VK_CAPITAL, 0x3A, KEYEVENTF_KEYUP, tag);   // up
    }

    /// <summary>한/영 키를 합성해 직전에 활성화된 앱의 입력 언어를 전환한다.</summary>
    public static void ToggleIme()
    {
        UIntPtr tag = (UIntPtr)(ulong)CAPS_SYNTH_TAG;
        keybd_event(VK_HANGUL, 0, 0, tag);
        keybd_event(VK_HANGUL, 0, KEYEVENTF_KEYUP, tag);
    }

    /// <summary>포그라운드 잠금을 우회하여 대상 창을 확실히 활성화한다(FM의 활성화 로직 대응).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint cur = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != cur)
            attached = AttachThreadInput(cur, fgThread, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        if (attached)
            AttachThreadInput(cur, fgThread, false);
    }

    /// <summary>창을 포커스 비탈취(WS_EX_NOACTIVATE) + 툴윈도우 스타일로 전환.</summary>
    public static void MakeNoActivateToolWindow(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>창을 마우스 입력에 대해 투명하게(혹은 원래대로) 만든다.
    /// 자동 숨김된 사이드바가 화면 끝에 남기는 1px 표시선이 그 자리의 클릭을
    /// 가로채지 않도록 하는 데 쓴다 — 보이기만 하고 입력은 아래 창으로 통과한다.</summary>
    public static void SetClickThrough(IntPtr hwnd, bool on)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~(long)WS_EX_TRANSPARENT);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }
}
