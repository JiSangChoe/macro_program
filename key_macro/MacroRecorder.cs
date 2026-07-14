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
        private Win32Api.LowLevelKeyboardProc? _proc;
        private List<RecordedKeyEvent> _recordedEvents = new List<RecordedKeyEvent>();
        private HashSet<ushort> _pressedKeys = new HashSet<ushort>();
        private Stopwatch _stopwatch = new Stopwatch();
        private double _lastEventTime = 0;
        private DispatcherTimer? _limitTimer;
        private bool _isRecording = false;

        public event Action<double>? Tick; // 남은 시간 알림용 (초 단위)
        public event Action? RecordingFinished; // 녹화 종료 알림
        public event Action<int>? KeyEventCountChanged; // 실시간 입력 키 개수 변경 알림

        public List<RecordedKeyEvent> RecordedEvents => _recordedEvents;
        public bool IsRecording => _isRecording;

        public void Start()
        {
            if (_isRecording) return;
            _isRecording = true;
            _recordedEvents.Clear();
            _pressedKeys.Clear();
            _stopwatch.Restart();
            _lastEventTime = 0;

            // 글로벌 훅 설치
            _proc = HookCallback;
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                _hookId = Win32Api.SetWindowsHookEx(
                    Win32Api.WH_KEYBOARD_LL,
                    _proc,
                    Win32Api.GetModuleHandle(curModule.ModuleName!),
                    0
                );
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

        public string KeyName
        {
            get
            {
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
        public string DisplayText => $"[{ActionText}] {KeyName} (이전 이벤트 후: {TimeOffsetSeconds:F2}초)";
    }
}
