using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace KeyMacro
{
    public class MacroRecorder : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private Win32Api.LowLevelKeyboardProc? _proc;
        private Win32Api.LowLevelMouseProc? _mouseProc;
        private IntPtr _targetHwnd = IntPtr.Zero;
        private List<RecordedKeyEvent> _recordedEvents = new List<RecordedKeyEvent>();
        private HashSet<ushort> _pressedKeys = new HashSet<ushort>();
        private Stopwatch _stopwatch = new Stopwatch();
        private double _lastEventTime = 0;
        private DispatcherTimer? _limitTimer;
        private bool _isRecording = false;

        // 마우스 감지 최적화용 캐시 변수
        private int _lastRecordedMouseX = -9999;
        private int _lastRecordedMouseY = -9999;

        public event Action<double>? Tick; // 남은 시간 알림용 (초 단위)
        public event Action? RecordingFinished; // 녹화 종료 알림
        public event Action<int>? KeyEventCountChanged; // 실시간 입력 키 개수 변경 알림

        public List<RecordedKeyEvent> RecordedEvents => _recordedEvents;
        public bool IsRecording => _isRecording;

        public void Start(IntPtr targetHwnd, bool recordKeyboard = true, bool recordMouse = true)
        {
            if (_isRecording) return;
            _isRecording = true;
            _targetHwnd = targetHwnd;
            _recordedEvents.Clear();
            _pressedKeys.Clear();
            _stopwatch.Restart();
            _lastEventTime = 0;
            _lastRecordedMouseX = -9999;
            _lastRecordedMouseY = -9999;

            // 글로벌 훅 설치
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                IntPtr modHandle = Win32Api.GetModuleHandle(curModule.ModuleName!);

                if (recordKeyboard)
                {
                    _proc = HookCallback;
                    _hookId = Win32Api.SetWindowsHookEx(
                        Win32Api.WH_KEYBOARD_LL,
                        _proc,
                        modHandle,
                        0
                    );
                }

                if (recordMouse)
                {
                    _mouseProc = HookMouseCallback;
                    _mouseHookId = Win32Api.SetWindowsHookExMouse(
                        Win32Api.WH_MOUSE_LL,
                        _mouseProc,
                        modHandle,
                        0
                    );
                }
            }

            // 최대 10분(600초) 제한 타이머
            _limitTimer = new DispatcherTimer();
            _limitTimer.Interval = TimeSpan.FromSeconds(1);
            _limitTimer.Tick += (s, e) =>
            {
                double elapsed = _stopwatch.Elapsed.TotalSeconds;
                Tick?.Invoke(600 - elapsed);
                if (elapsed >= 600)
                {
                    Stop();
                }
            };
            _limitTimer.Start();
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _isRecording = false;
            _stopwatch.Stop();
            _limitTimer?.Stop();

            if (_hookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            if (_mouseHookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }

            RecordingFinished?.Invoke();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                bool isKeyDown = message == Win32Api.WM_KEYDOWN || message == Win32Api.WM_SYSKEYDOWN;
                bool isKeyUp = message == Win32Api.WM_KEYUP || message == Win32Api.WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    ushort key = (ushort)vkCode;

                    if (isKeyDown)
                    {
                        // 이미 누름 상태(Auto-Repeat)이면 중복 무시
                        if (_pressedKeys.Contains(key))
                        {
                            return Win32Api.CallNextHookEx(_hookId, nCode, wParam, lParam);
                        }
                        _pressedKeys.Add(key);
                    }
                    else if (isKeyUp)
                    {
                        _pressedKeys.Remove(key);
                    }

                    double currentTime = _stopwatch.Elapsed.TotalSeconds;
                    double offset = currentTime - _lastEventTime;
                    _lastEventTime = currentTime;

                    _recordedEvents.Add(new RecordedKeyEvent
                    {
                        VirtualKey = key,
                        IsKeyDown = isKeyDown,
                        TimeOffsetSeconds = offset
                    });

                    KeyEventCountChanged?.Invoke(_recordedEvents.Count);
                }
            }
            return Win32Api.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private IntPtr HookMouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                if (message == Win32Api.WM_MOUSEMOVE || message == Win32Api.WM_LBUTTONDOWN || message == Win32Api.WM_LBUTTONUP || message == Win32Api.WM_MOUSEWHEEL)
                {
                    var hookStruct = Marshal.PtrToStructure<Win32Api.MSLLHOOKSTRUCT>(lParam);
                    int relX = hookStruct.pt.X;
                    int relY = hookStruct.pt.Y;

                    // 대상 창 기준의 상대 좌표로 동적 계산
                    if (_targetHwnd != IntPtr.Zero)
                    {
                        Win32Api.GetWindowRect(_targetHwnd, out var rect);
                        relX -= rect.Left;
                        relY -= rect.Top;
                    }

                    // 마우스 단순 이동(WM_MOUSEMOVE) 시 과포화 방지용 필터링 (최적화)
                    if (message == Win32Api.WM_MOUSEMOVE)
                    {
                        if (Math.Abs(relX - _lastRecordedMouseX) < 8 && Math.Abs(relY - _lastRecordedMouseY) < 8)
                        {
                            return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
                        }
                        _lastRecordedMouseX = relX;
                        _lastRecordedMouseY = relY;
                    }

                    double currentTime = _stopwatch.Elapsed.TotalSeconds;
                    double offset = currentTime - _lastEventTime;
                    _lastEventTime = currentTime;

                    uint rawMouseData = hookStruct.mouseData;
                    if (message == Win32Api.WM_MOUSEWHEEL)
                    {
                        short delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                        rawMouseData = (uint)(int)delta;
                    }

                    _recordedEvents.Add(new RecordedKeyEvent
                    {
                        IsMouseEvent = true,
                        MouseX = relX,
                        MouseY = relY,
                        MouseEventFlags = GetMouseFlag(message),
                        MouseData = rawMouseData,
                        TimeOffsetSeconds = offset
                    });

                    KeyEventCountChanged?.Invoke(_recordedEvents.Count);
                }
            }
            return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private static uint GetMouseFlag(int message)
        {
            if (message == Win32Api.WM_LBUTTONDOWN) return Win32Api.MOUSEEVENTF_LEFTDOWN;
            if (message == Win32Api.WM_LBUTTONUP) return Win32Api.MOUSEEVENTF_LEFTUP;
            if (message == Win32Api.WM_MOUSEWHEEL) return Win32Api.MOUSEEVENTF_WHEEL;
            return Win32Api.MOUSEEVENTF_MOVE;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    public struct RecordedKeyEvent
    {
        public ushort VirtualKey { get; set; }
        public bool IsKeyDown { get; set; }
        public double TimeOffsetSeconds { get; set; }

        // 마우스 관련 확장 속성
        public bool IsMouseEvent { get; set; }
        public int MouseX { get; set; }
        public int MouseY { get; set; }
        public uint MouseEventFlags { get; set; }
        public uint MouseData { get; set; }

        public string KeyName
        {
            get
            {
                if (IsMouseEvent) return "Mouse";
                try
                {
                    var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(VirtualKey);
                    return key.ToString();
                }
                catch
                {
                    return $"VK_{VirtualKey:X2}";
                }
            }
        }
        public string ActionText => IsKeyDown ? "눌림" : "뗌";
        public string DisplayText
        {
            get
            {
                if (IsMouseEvent)
                {
                    string action = "이동";
                    if ((MouseEventFlags & 0x0002) != 0) action = "클릭 누름";
                    else if ((MouseEventFlags & 0x0004) != 0) action = "클릭 뗌";
                    else if ((MouseEventFlags & 0x0800) != 0)
                    {
                        int delta = (int)MouseData;
                        string scrollDir = delta > 0 ? "위로" : "아래로";
                        action = $"휠 스크롤 ({scrollDir})";
                    }
                    return $"[마우스 {action}] X={MouseX}, Y={MouseY} (지연: {TimeOffsetSeconds:F2}초)";
                }
                else
                {
                    return $"[{ActionText}] {KeyName} (지연: {TimeOffsetSeconds:F2}초)";
                }
            }
        }
    }
}
