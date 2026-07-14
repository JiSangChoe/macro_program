using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KeyMacro
{
    public partial class MainWindow : Window
    {
        // P/Invoke for Hotkeys and Cursor position control
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID_START = 9000;
        private const int HOTKEY_ID_STOP = 9001;
        private const int HOTKEY_ID_PAUSE = 9002;
        private const int HOTKEY_ID_PICKER = 9003;

        // Observable Collections for Data Binding
        private readonly ObservableCollection<MacroAction> _macroActions = new ObservableCollection<MacroAction>();
        private readonly ObservableCollection<RecordedKeyEvent> _recordedEvents = new ObservableCollection<RecordedKeyEvent>();
        private readonly ObservableCollection<Win32Api.WindowInfo> _windows = new ObservableCollection<Win32Api.WindowInfo>();
        private readonly ObservableCollection<MacroProfile> _profiles = new ObservableCollection<MacroProfile>();
        private readonly ObservableCollection<MacroProfile> _sequencerQueue = new ObservableCollection<MacroProfile>();

        private readonly MacroRecorder _recorder = new MacroRecorder();
        private readonly HashSet<ushort> _currentCustomKeys = new HashSet<ushort>();
        private static readonly Random _random = new Random();

        private CancellationTokenSource? _macroCts;
        private bool _isMacroRunning = false;
        private bool _isMacroPaused = false;
        private MacroProfile? _activeProfile = null;

        private Stopwatch? _macroRuntimeStopwatch;
        private DispatcherTimer? _macroRuntimeTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Bind Lists
            MacroActionListView.ItemsSource = _macroActions;
            WindowComboBox.ItemsSource = _windows;
            ProfileComboBox.ItemsSource = _profiles;
            SequencerAvailableProfilesListView.ItemsSource = _profiles;
            SequencerQueueListView.ItemsSource = _sequencerQueue;

            // Setup Recorder Events
            _recorder.Tick += Recorder_Tick;
            _recorder.RecordingFinished += Recorder_RecordingFinished;
            _recorder.KeyEventCountChanged += Recorder_KeyEventCountChanged;

            // Load initial configurations
            RefreshWindowList();
            LoadAllProfilesFromDisk();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;

            // Register Hotkeys: F5 (0x74) for Start, F6 (0x75) for Stop, F7 (0x76) for Pause, F8 (0x77) for Coordinate Picker
            RegisterHotKey(handle, HOTKEY_ID_START, 0, 0x74);
            RegisterHotKey(handle, HOTKEY_ID_STOP, 0, 0x75);
            RegisterHotKey(handle, HOTKEY_ID_PAUSE, 0, 0x76);
            RegisterHotKey(handle, HOTKEY_ID_PICKER, 0, 0x77);

            HwndSource source = HwndSource.FromHwnd(handle);
            source.AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID_START)
                {
                    StartMacro();
                    handled = true;
                }
                else if (id == HOTKEY_ID_STOP)
                {
                    StopMacro();
                    handled = true;
                }
                else if (id == HOTKEY_ID_PAUSE)
                {
                    TogglePauseMacro();
                    handled = true;
                }
                else if (id == HOTKEY_ID_PICKER)
                {
                    PickMouseCoordinate();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID_START);
            UnregisterHotKey(handle, HOTKEY_ID_STOP);
            UnregisterHotKey(handle, HOTKEY_ID_PAUSE);
            UnregisterHotKey(handle, HOTKEY_ID_PICKER);
            _recorder.Dispose();
            base.OnClosed(e);
        }

        // ------------------ Window Control & Refresh ------------------
        private void RefreshWindowList()
        {
            _windows.Clear();
            var openWindows = Win32Api.GetOpenWindows();
            
            // Add self to avoid targeting own window
            var selfHandle = new WindowInteropHelper(this).Handle;
            
            foreach (var win in openWindows)
            {
                if (win.Handle != selfHandle)
                {
                    _windows.Add(win);
                }
            }

            if (_windows.Count > 0)
            {
                WindowComboBox.SelectedIndex = 0;
            }
        }

        private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshWindowList();
        }

        // ------------------ Profiles Management (Save / Load / Delete) ------------------
        private void LoadAllProfilesFromDisk()
        {
            _profiles.Clear();
            var list = ProfileManager.LoadProfiles();
            foreach (var p in list)
            {
                _profiles.Add(p);
            }
            if (_profiles.Count > 0)
            {
                ProfileComboBox.SelectedIndex = 0;
            }
        }

        private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            string profileName = ProfileNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(profileName))
            {
                MessageBox.Show("저장할 프로필 이름을 입력해 주세요.", "이름 누락", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string type = MainTabControl.SelectedIndex == 0 ? "수동 매크로" : "녹화 매크로";

            if (MainTabControl.SelectedIndex == 0 && _macroActions.Count == 0)
            {
                MessageBox.Show("저장할 수동 매크로 동작이 없습니다. 시퀀스를 구성해 주세요.", "동작 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MainTabControl.SelectedIndex == 1 && _recordedEvents.Count == 0)
            {
                MessageBox.Show("저장할 녹화 데이터가 없습니다. 먼저 키를 녹화해 주세요.", "녹화 데이터 없음", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check duplicate
            var existingProfile = _profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            if (existingProfile != null)
            {
                var result = MessageBox.Show($"이미 '{profileName}' 프로필이 존재합니다. 덮어쓰시겠습니까?", "덮어쓰기 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No)
                {
                    return;
                }
                _profiles.Remove(existingProfile);
            }

            var newProfile = new MacroProfile
            {
                Name = profileName,
                ProfileType = type
            };

            if (MainTabControl.SelectedIndex == 0)
            {
                newProfile.ManualActions = _macroActions.ToList();
            }
            else
            {
                newProfile.RecordedEvents = _recordedEvents.ToList();
            }

            _profiles.Add(newProfile);
            ProfileManager.SaveProfiles(_profiles.ToList());
            _activeProfile = newProfile;
            ProfileComboBox.SelectedItem = newProfile;
            ProfileNameTextBox.Clear();

            StatusText.Text = $"프로필 '{profileName}' 저장 완료";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
        }

        private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileComboBox.SelectedItem is MacroProfile profile)
            {
                LoadProfile(profile);
            }
            else
            {
                MessageBox.Show("불러올 프로필을 선택해 주세요.", "선택 누락", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProfileComboBox.SelectedItem is MacroProfile profile)
            {
                LoadProfile(profile);
            }
        }

        private void WindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WindowComboBox.SelectedItem is Win32Api.WindowInfo win)
            {
                StatusText.Text = $"대상 창 지정 완료: [{win.Title}]";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            }
        }

        private void LoadProfile(MacroProfile profile)
        {
            _activeProfile = profile;
            ProfileNameTextBox.Text = profile.Name;

            if (profile.ProfileType == "수동 매크로")
            {
                MainTabControl.SelectedIndex = 0;
                _macroActions.Clear();
                foreach (var action in profile.ManualActions)
                {
                    _macroActions.Add(action);
                }
            }
            else // 녹화 매크로
            {
                MainTabControl.SelectedIndex = 1;
                _recordedEvents.Clear();
                foreach (var evt in profile.RecordedEvents)
                {
                    _recordedEvents.Add(evt);
                }
            }

            StatusText.Text = $"프로필 [{profile.Name}] 데이터 불러오기 완료";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileComboBox.SelectedItem is not MacroProfile profile)
            {
                MessageBox.Show("삭제할 프로필을 선택해 주세요.", "선택 누락", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"정말로 '{profile.Name}' 프로필을 삭제하시겠습니까?", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _profiles.Remove(profile);
                ProfileManager.SaveProfiles(_profiles.ToList());

                // 통합 실행 순서 조합 큐에서도 삭제된 프로필을 완전 연쇄 제거 (버그 해결)
                for (int i = _sequencerQueue.Count - 1; i >= 0; i--)
                {
                    if (_sequencerQueue[i].Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _sequencerQueue.RemoveAt(i);
                    }
                }

                _activeProfile = null;
                ProfileNameTextBox.Clear();
                if (_profiles.Count > 0)
                {
                    ProfileComboBox.SelectedIndex = 0;
                }
                else
                {
                    ProfileComboBox.SelectedItem = null;
                }

                StatusText.Text = $"'{profile.Name}' 프로필 삭제 완료";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            }
        }

        // ------------------ Manual Action Configuration ------------------
        private void KeyInputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            KeyInputTextBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 46, 129));
            if (_currentCustomKeys.Count == 0)
            {
                KeyInputTextBox.Text = "[입력 대기 중...] 키를 누르세요";
            }
        }

        private void KeyInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            KeyInputTextBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 47));
            UpdateKeyInputTextBoxText();
        }

        private void KeyInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            
            if (actualKey == Key.ImeProcessed || actualKey == Key.None) return;

            ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(actualKey);
            if (vk > 0 && !_currentCustomKeys.Contains(vk))
            {
                _currentCustomKeys.Add(vk);
                UpdateKeyInputTextBoxText();
            }
        }

        private void KeyInputTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            e.Handled = true;
        }

        private void UpdateKeyInputTextBoxText()
        {
            if (_currentCustomKeys.Count == 0)
            {
                KeyInputTextBox.Text = "이곳을 클릭 후 키를 누르세요";
                return;
            }

            var keyNames = _currentCustomKeys.Select(vk =>
            {
                try
                {
                    return ((Key)KeyInterop.KeyFromVirtualKey(vk)).ToString();
                }
                catch
                {
                    return $"VK_{vk:X2}";
                }
            });
            KeyInputTextBox.Text = string.Join(" + ", keyNames);
        }

        private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCustomKeys.Clear();
            UpdateKeyInputTextBoxText();
        }

        private void ActionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeyboardInputArea == null || MouseInputArea == null) return;
            if (ActionTypeComboBox.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? "Keyboard";
                if (tag == "Keyboard")
                {
                    KeyboardInputArea.Visibility = Visibility.Visible;
                    MouseInputArea.Visibility = Visibility.Collapsed;
                }
                else
                {
                    KeyboardInputArea.Visibility = Visibility.Collapsed;
                    MouseInputArea.Visibility = Visibility.Visible;
                }
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scv)
            {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void PickMouseCoordinate()
        {
            Win32Api.POINT pt;
            if (Win32Api.GetCursorPos(out pt))
            {
                int relX = pt.X;
                int relY = pt.Y;

                // 대상 윈도우가 선택되어 있으면 상대좌표로 변환
                if (WindowComboBox.SelectedItem is Win32Api.WindowInfo targetWin)
                {
                    Win32Api.GetWindowRect(targetWin.Handle, out var rect);
                    relX -= rect.Left;
                    relY -= rect.Top;
                }

                // 텍스트박스에 꽂아넣기
                MouseXTextBox.Text = relX.ToString();
                MouseYTextBox.Text = relY.ToString();

                // 성공 사운드 피드백 및 상태바 피드백
                System.Media.SystemSounds.Beep.Play();
                StatusText.Text = $"마우스 상대 좌표 자동 캡처 완료: X={relX}, Y={relY}";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 140, 248)); // 보라색
            }
        }

        private void AddActionButton_Click(object sender, RoutedEventArgs e)
        {
            string actionType = "Keyboard";
            if (ActionTypeComboBox.SelectedItem is ComboBoxItem comboItem)
            {
                actionType = comboItem.Tag?.ToString() ?? "Keyboard";
            }

            int mouseX = 0;
            int mouseY = 0;
            double duration = 0.1;

            if (actionType == "Keyboard")
            {
                if (_currentCustomKeys.Count == 0)
                {
                    MessageBox.Show("동작할 키를 하나 이상 지정해주세요.", "키 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(DurationTextBox.Text, out duration) || duration <= 0)
                {
                    MessageBox.Show("누르고 있을 시간은 0보다 큰 숫자여야 합니다 (예: 0.1).", "시간 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else // MouseClick or MouseMove
            {
                if (!int.TryParse(MouseXTextBox.Text, out mouseX))
                {
                    MessageBox.Show("마우스 X 좌표는 정수여야 합니다.", "좌표 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(MouseYTextBox.Text, out mouseY))
                {
                    MessageBox.Show("마우스 Y 좌표는 정수여야 합니다.", "좌표 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                duration = 0.0; // 마우스는 즉시 격발
            }

            if (!int.TryParse(RepeatTextBox.Text, out int repeat) || repeat <= 0)
            {
                MessageBox.Show("반복 횟수는 1 이상의 정수여야 합니다.", "반복 횟수 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(DelayAfterTextBox.Text, out double delayAfter) || delayAfter < 0)
            {
                MessageBox.Show("대기 시간은 0 이상의 숫자여야 합니다.", "대기 시간 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var action = new MacroAction
            {
                ActionType = actionType,
                MouseX = mouseX,
                MouseY = mouseY,
                VirtualKeys = actionType == "Keyboard" ? _currentCustomKeys.ToList() : new List<ushort>(),
                KeysText = actionType == "Keyboard" ? KeyInputTextBox.Text : string.Empty,
                Duration = duration,
                RepeatCount = repeat,
                DelayAfter = delayAfter
            };

            _macroActions.Add(action);
            StatusText.Text = "동작 추가 완료";
            ResetActionInputs();
        }

        private void ResetActionInputs()
        {
            _currentCustomKeys.Clear();
            UpdateKeyInputTextBoxText();
            DurationTextBox.Text = "0.1";
            RepeatTextBox.Text = "1";
            DelayAfterTextBox.Text = "0.1";
            MouseXTextBox.Text = "0";
            MouseYTextBox.Text = "0";
            RandomDelayCheckBox.IsChecked = false;
            ActionTypeComboBox.SelectedIndex = 0;
            KeyboardInputArea.Visibility = Visibility.Visible;
            MouseInputArea.Visibility = Visibility.Collapsed;
            MacroActionListView.SelectedItem = null;
        }

        private void UpdateActionButton_Click(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MacroActionListView.SelectedIndex;
            if (selectedIndex < 0)
            {
                MessageBox.Show("수정할 매크로 동작을 시퀀스 목록에서 선택해주세요.", "수정 알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string actionType = "Keyboard";
            if (ActionTypeComboBox.SelectedItem is ComboBoxItem comboItem)
            {
                actionType = comboItem.Tag?.ToString() ?? "Keyboard";
            }

            int mouseX = 0;
            int mouseY = 0;
            double duration = 0.1;

            if (actionType == "Keyboard")
            {
                if (_currentCustomKeys.Count == 0)
                {
                    MessageBox.Show("동작할 키를 하나 이상 지정해주세요.", "키 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(DurationTextBox.Text, out duration) || duration <= 0)
                {
                    MessageBox.Show("누르고 있을 시간은 0보다 큰 숫자여야 합니다 (예: 0.1).", "시간 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else // MouseClick or MouseMove
            {
                if (!int.TryParse(MouseXTextBox.Text, out mouseX))
                {
                    MessageBox.Show("마우스 X 좌표는 정수여야 합니다.", "좌표 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(MouseYTextBox.Text, out mouseY))
                {
                    MessageBox.Show("마우스 Y 좌표는 정수여야 합니다.", "좌표 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                duration = 0.0;
            }

            if (!int.TryParse(RepeatTextBox.Text, out int repeat) || repeat <= 0)
            {
                MessageBox.Show("반복 횟수는 1 이상의 정수여야 합니다.", "반복 횟수 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(DelayAfterTextBox.Text, out double delayAfter) || delayAfter < 0)
            {
                MessageBox.Show("대기 시간은 0 이상의 숫자여야 합니다.", "대기 시간 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedAction = _macroActions[selectedIndex];
            selectedAction.VirtualKeys = _currentCustomKeys.ToList();
            selectedAction.KeysText = KeyInputTextBox.Text;
            selectedAction.Duration = duration;
            selectedAction.RepeatCount = repeat;
            selectedAction.DelayAfter = delayAfter;

            // Refresh UI
            _macroActions[selectedIndex] = selectedAction;
            StatusText.Text = "선택 동작 수정 완료";
            ResetActionInputs();
        }

        private void DeleteActionButton_Click(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MacroActionListView.SelectedIndex;
            if (selectedIndex >= 0)
            {
                _macroActions.RemoveAt(selectedIndex);
                StatusText.Text = "동작 삭제 완료";
                ResetActionInputs();
            }
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            int index = MacroActionListView.SelectedIndex;
            if (index > 0)
            {
                var item = _macroActions[index];
                _macroActions.RemoveAt(index);
                _macroActions.Insert(index - 1, item);
                MacroActionListView.SelectedIndex = index - 1;
            }
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            int index = MacroActionListView.SelectedIndex;
            if (index >= 0 && index < _macroActions.Count - 1)
            {
                var item = _macroActions[index];
                _macroActions.RemoveAt(index);
                _macroActions.Insert(index + 1, item);
                MacroActionListView.SelectedIndex = index + 1;
            }
        }

        private void MacroActionListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MacroActionListView.SelectedItem is MacroAction action)
            {
                // 동작 구분에 맞는 UI 콤보박스 값 및 토글 동기화
                if (action.ActionType == "MouseClick")
                {
                    ActionTypeComboBox.SelectedIndex = 1;
                    MouseXTextBox.Text = action.MouseX.ToString();
                    MouseYTextBox.Text = action.MouseY.ToString();
                }
                else if (action.ActionType == "MouseMove")
                {
                    ActionTypeComboBox.SelectedIndex = 2;
                    MouseXTextBox.Text = action.MouseX.ToString();
                    MouseYTextBox.Text = action.MouseY.ToString();
                }
                else
                {
                    ActionTypeComboBox.SelectedIndex = 0;
                    _currentCustomKeys.Clear();
                    foreach (var vk in action.VirtualKeys)
                    {
                        _currentCustomKeys.Add(vk);
                    }
                    UpdateKeyInputTextBoxText();
                    DurationTextBox.Text = action.Duration.ToString("F1");
                }

                RepeatTextBox.Text = action.RepeatCount.ToString();
                DelayAfterTextBox.Text = action.DelayAfter.ToString("F1");
            }
        }

        // ------------------ Keyboard Real-time Recording ------------------
        private void StartRecordButton_Click(object sender, RoutedEventArgs e)
        {
            _recordedEvents.Clear();
            RecordStatusText.Text = "녹화 중...";
            RecordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            
            // 녹화 중 카드 색상 빨갛게 전환 (위험/기록 중 상태 연출)
            RecordInfoCard.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 28, 28));
            RecordInfoCard.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));

            RecordCountText.Text = "0 회";
            StartRecordButton.IsEnabled = false;
            StopRecordButton.IsEnabled = true;

            IntPtr targetHwnd = IntPtr.Zero;
            if (WindowComboBox.SelectedItem is Win32Api.WindowInfo targetWin)
            {
                targetHwnd = targetWin.Handle;
            }
            _recorder.Start(targetHwnd);
        }

        private void StopRecordButton_Click(object sender, RoutedEventArgs e)
        {
            _recorder.Stop();
        }

        private void Recorder_Tick(double remainingSeconds)
        {
            int minutes = (int)remainingSeconds / 60;
            int seconds = (int)remainingSeconds % 60;
            RecordTimerText.Text = $"남은 녹화 시간: {minutes:D2}:{seconds:D2}";
        }

        private void Recorder_KeyEventCountChanged(int count)
        {
            Dispatcher.Invoke(() =>
            {
                RecordCountText.Text = $"{count} 회";
            });
        }

        private void Recorder_RecordingFinished()
        {
            StartRecordButton.IsEnabled = true;
            StopRecordButton.IsEnabled = false;
            RecordStatusText.Text = "녹화 완료 및 저장 대기";
            RecordStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            RecordTimerText.Text = "남은 녹화 시간: 10:00";

            // 카드 색상 원래 상태(다크 블루)로 원복
            RecordInfoCard.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 22, 46));
            RecordInfoCard.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 46, 129));

            _recordedEvents.Clear();
            foreach (var evt in _recorder.RecordedEvents)
            {
                _recordedEvents.Add(evt);
            }
            RecordCountText.Text = $"{_recordedEvents.Count} 회";

            if (_recordedEvents.Count == 0)
            {
                MessageBox.Show("녹화된 키 입력이 없습니다. 저장을 취소합니다.", "녹화 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. 활성화된 기존 녹화 프로필이 있는 경우: 덮어쓸지 여부 확인
            if (_activeProfile != null && _activeProfile.ProfileType == "녹화 매크로")
            {
                var askResult = MessageBox.Show(
                    $"현재 활성화된 프로필 '{_activeProfile.Name}'에 방금 녹화한 내용으로 덮어쓰시겠습니까?\n[아니오]를 누르면 새 프로필로 저장합니다.", 
                    "프로필 저장/덮어쓰기", 
                    MessageBoxButton.YesNoCancel, 
                    MessageBoxImage.Question
                );

                if (askResult == MessageBoxResult.Yes)
                {
                    _activeProfile.RecordedEvents = _recordedEvents.ToList();
                    ProfileManager.SaveProfiles(_profiles.ToList());
                    StatusText.Text = $"'{_activeProfile.Name}' 프로필에 녹화 데이터 덮어쓰기 완료!";
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    ProfileComboBox.Items.Refresh();
                    return;
                }
                else if (askResult == MessageBoxResult.Cancel)
                {
                    StatusText.Text = "프로필 저장 취소됨";
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    return;
                }
            }

            // 2. 새 이름으로 프로필 저장창 띄우기 (또는 [아니오]를 누른 경우)
            InputDialog inputDlg = new InputDialog("저장할 녹화 프로필 이름을 입력하세요:", _activeProfile?.Name ?? "")
            {
                Owner = this
            };

            if (inputDlg.ShowDialog() == true)
            {
                string profileName = inputDlg.ResponseText.Trim();

                // 중복 검사
                var existingProfile = _profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
                if (existingProfile != null)
                {
                    var overwriteResult = MessageBox.Show(
                        $"이미 '{profileName}' 프로필이 존재합니다. 덮어쓰시겠습니까?", 
                        "덮어쓰기 확인", 
                        MessageBoxButton.YesNo, 
                        MessageBoxImage.Question
                    );
                    if (overwriteResult == MessageBoxResult.No)
                    {
                        StatusText.Text = "프로필 저장 취소됨";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        return;
                    }
                    _profiles.Remove(existingProfile);
                }

                var newProfile = new MacroProfile
                {
                    Name = profileName,
                    ProfileType = "녹화 매크로",
                    RecordedEvents = _recordedEvents.ToList()
                };

                _profiles.Add(newProfile);
                ProfileManager.SaveProfiles(_profiles.ToList());
                _activeProfile = newProfile;
                ProfileComboBox.SelectedItem = newProfile;

                StatusText.Text = $"새 녹화 프로필 '{profileName}' 저장 완료!";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            }
            else
            {
                StatusText.Text = "프로필 저장 취소됨";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            }
        }

        private bool IsManualMacroDirty()
        {
            if (_activeProfile == null || _activeProfile.ProfileType != "수동 매크로") return true;
            if (_activeProfile.ManualActions == null || _activeProfile.ManualActions.Count != _macroActions.Count) return true;
            
            for (int i = 0; i < _macroActions.Count; i++)
            {
                var a = _macroActions[i];
                var b = _activeProfile.ManualActions[i];
                
                if (a.Duration != b.Duration) return true;
                if (a.RepeatCount != b.RepeatCount) return true;
                if (a.DelayAfter != b.DelayAfter) return true;
                if (a.VirtualKeys == null || b.VirtualKeys == null) return true;
                if (!a.VirtualKeys.SequenceEqual(b.VirtualKeys)) return true;
            }
            return false;
        }

        // ------------------ Macro Execution Engine ------------------
        private void StartMacro()
        {
            if (_isMacroRunning) return;
            
            // Check targets
            if (MainTabControl.SelectedIndex == 0 && _macroActions.Count == 0)
            {
                MessageBox.Show("수동 매크로 동작 시퀀스가 비어 있습니다.", "실행 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MainTabControl.SelectedIndex == 1 && _recordedEvents.Count == 0)
            {
                MessageBox.Show("녹화된 키 입력 시퀀스가 비어 있습니다.", "실행 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MainTabControl.SelectedIndex == 2 && _sequencerQueue.Count == 0)
            {
                MessageBox.Show("통합 매크로 시퀀스 목록이 비어 있습니다.", "실행 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 수동 매크로 실행 전 미저장 상태인 경우 저장 유도
            if (MainTabControl.SelectedIndex == 0 && IsManualMacroDirty())
            {
                var result = MessageBox.Show(
                    "현재 수동 매크로 설정에 저장되지 않은 변경 사항이 있습니다.\n실행 전에 프로필에 저장하시겠습니까?\n\n([아니오]를 누르면 저장하지 않고 바로 실행합니다.)", 
                    "프로필 저장 확인", 
                    MessageBoxButton.YesNoCancel, 
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Cancel)
                {
                    return; // 실행 중단
                }
                else if (result == MessageBoxResult.Yes)
                {
                    if (_activeProfile != null && _activeProfile.ProfileType == "수동 매크로")
                    {
                        // 덮어쓰기
                        _activeProfile.ManualActions = _macroActions.ToList();
                        ProfileManager.SaveProfiles(_profiles.ToList());
                        StatusText.Text = $"'{_activeProfile.Name}' 프로필 업데이트 완료";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                        ProfileComboBox.Items.Refresh();
                    }
                    else
                    {
                        // 새 이름으로 저장
                        InputDialog inputDlg = new InputDialog("저장할 수동 매크로 프로필 이름을 입력하세요:", "")
                        {
                            Owner = this
                        };

                        if (inputDlg.ShowDialog() == true)
                        {
                            string profileName = inputDlg.ResponseText.Trim();

                            // 중복 검사
                            var existingProfile = _profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
                            if (existingProfile != null)
                            {
                                var overwriteResult = MessageBox.Show(
                                    $"이미 '{profileName}' 프로필이 존재합니다. 덮어쓰시겠습니까?", 
                                    "덮어쓰기 확인", 
                                    MessageBoxButton.YesNo, 
                                    MessageBoxImage.Question
                                );
                                if (overwriteResult == MessageBoxResult.No)
                                {
                                    return; // 실행 중단
                                }
                                _profiles.Remove(existingProfile);
                            }

                            var newProfile = new MacroProfile
                            {
                                Name = profileName,
                                ProfileType = "수동 매크로",
                                ManualActions = _macroActions.ToList()
                            };

                            _profiles.Add(newProfile);
                            ProfileManager.SaveProfiles(_profiles.ToList());
                            _activeProfile = newProfile;
                            ProfileComboBox.SelectedItem = newProfile;
                        }
                        else
                        {
                            return; // 저장 취소 시 실행도 중단
                        }
                    }
                }
            }

            _isMacroRunning = true;
            _isMacroPaused = false;
            StatusText.Text = "매크로 동작 실행 중...";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            StartMacroButton.IsEnabled = false;
            PauseMacroButton.IsEnabled = true;
            PauseMacroButton.Content = "일시정지 (F7)";

            // 런타임 타이머 셋팅 및 시작
            MacroTimerText.Text = "실행 시간: 0.0초";
            _macroRuntimeStopwatch = new Stopwatch();
            _macroRuntimeStopwatch.Start();
            _macroRuntimeTimer = new DispatcherTimer();
            _macroRuntimeTimer.Interval = TimeSpan.FromMilliseconds(100);
            _macroRuntimeTimer.Tick += (s, e) =>
            {
                if (_isMacroPaused)
                {
                    if (_macroRuntimeStopwatch.IsRunning) _macroRuntimeStopwatch.Stop();
                }
                else
                {
                    if (!_macroRuntimeStopwatch.IsRunning) _macroRuntimeStopwatch.Start();
                }
                MacroTimerText.Text = $"실행 시간: {_macroRuntimeStopwatch.Elapsed.TotalSeconds:F1}초";
            };
            _macroRuntimeTimer.Start();

            _macroCts = new CancellationTokenSource();
            var token = _macroCts.Token;

            // Target window focusing
            IntPtr targetHwnd = IntPtr.Zero;
            if (WindowComboBox.SelectedItem is Win32Api.WindowInfo targetWin)
            {
                targetHwnd = targetWin.Handle;
            }

            int selectedTab = MainTabControl.SelectedIndex;
            bool isLoop = LoopMacroCheckBox.IsChecked == true;
            bool applyRandom = RandomDelayCheckBox.IsChecked == true;
            bool isRandomMode = SequencerRandomRadio.IsChecked == true;

            Task.Run(async () =>
            {
                List<ushort> pressedKeys = new List<ushort>();

                try
                {
                    // 1. 실행 시작 시점 딱 한 번, 대상 윈도우를 최전면으로 활성화하고 0.5초 동안 대기하여 포커스 안착 보장
                    if (targetHwnd != IntPtr.Zero)
                    {
                        if (Win32Api.IsIconic(targetHwnd))
                        {
                            Win32Api.ShowWindow(targetHwnd, Win32Api.SW_RESTORE);
                        }
                        Win32Api.SetForegroundWindow(targetHwnd);
                        await Task.Delay(500, token);
                    }

                    do
                    {
                        // 2. 무한 루프 반복 시 혹시 포커스가 풀린 경우를 대비해 전면 배치 상태 유지
                        if (targetHwnd != IntPtr.Zero)
                        {
                            if (Win32Api.IsIconic(targetHwnd))
                            {
                                Win32Api.ShowWindow(targetHwnd, Win32Api.SW_RESTORE);
                            }
                            Win32Api.SetForegroundWindow(targetHwnd);
                            await Task.Delay(200, token);
                        }

                        if (selectedTab == 0)
                        {
                            // Manual Macro Run - 스냅샷 복사본 순회로 스레드 안전성 보장
                            foreach (var action in _macroActions.ToList())
                            {
                                token.ThrowIfCancellationRequested();

                                for (int r = 0; r < action.RepeatCount; r++)
                                {
                                    token.ThrowIfCancellationRequested();

                                    if (action.ActionType == "MouseClick" || action.ActionType == "MouseMove")
                                    {
                                        int targetX = action.MouseX;
                                        int targetY = action.MouseY;
                                        if (targetHwnd != IntPtr.Zero)
                                        {
                                            Win32Api.GetWindowRect(targetHwnd, out var rect);
                                            targetX += rect.Left;
                                            targetY += rect.Top;
                                        }

                                        await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);

                                        if (action.ActionType == "MouseClick")
                                        {
                                            await Task.Delay(20, token);
                                            SendMouseClickEvent(Win32Api.MOUSEEVENTF_LEFTDOWN, targetX, targetY);
                                            await Task.Delay(20, token);
                                            SendMouseClickEvent(Win32Api.MOUSEEVENTF_LEFTUP, targetX, targetY);
                                        }
                                        else
                                        {
                                            SetCursorPos(targetX, targetY);
                                        }

                                        await Task.Delay(GetRandomizedDelay(action.DelayAfter, applyRandom), token);
                                    }
                                    else // Keyboard Action
                                    {
                                        pressedKeys = action.VirtualKeys.ToList();
                                        await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);
                                        SendKeys(pressedKeys, true);

                                        await Task.Delay(GetRandomizedDelay(action.Duration, applyRandom), token);

                                        SendKeys(pressedKeys, false);
                                        pressedKeys = new List<ushort>();

                                        await Task.Delay(GetRandomizedDelay(action.DelayAfter, applyRandom), token);
                                    }
                                }
                            }
                        }
                        else if (selectedTab == 1)
                        {
                            // Recorded Macro Run - 스냅샷 복사본 순회로 스레드 안전성 보장
                            foreach (var evt in _recordedEvents.ToList())
                            {
                                token.ThrowIfCancellationRequested();

                                if (evt.TimeOffsetSeconds > 0)
                                {
                                    await Task.Delay((int)(evt.TimeOffsetSeconds * 1000), token);
                                }

                                if (evt.IsMouseEvent)
                                {
                                    int targetX = evt.MouseX;
                                    int targetY = evt.MouseY;
                                    if (targetHwnd != IntPtr.Zero)
                                    {
                                        Win32Api.GetWindowRect(targetHwnd, out var rect);
                                        targetX += rect.Left;
                                        targetY += rect.Top;
                                    }

                                    await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);

                                    if (evt.MouseEventFlags == Win32Api.MOUSEEVENTF_WHEEL)
                                    {
                                        SendMouseWheelEvent(evt.MouseEventFlags, evt.MouseData, targetX, targetY);
                                    }
                                    else if (evt.MouseEventFlags == Win32Api.MOUSEEVENTF_MOVE)
                                    {
                                        SetCursorPos(targetX, targetY);
                                    }
                                    else if (evt.MouseEventFlags != 0)
                                    {
                                        SendMouseClickEvent(evt.MouseEventFlags, targetX, targetY);
                                    }
                                }
                                else // Keyboard
                                {
                                    if (evt.IsKeyDown)
                                    {
                                        if (!pressedKeys.Contains(evt.VirtualKey))
                                        {
                                            pressedKeys.Add(evt.VirtualKey);
                                        }
                                        await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);
                                        SendKey(evt.VirtualKey, true);
                                    }
                                    else
                                    {
                                        pressedKeys.Remove(evt.VirtualKey);
                                        SendKey(evt.VirtualKey, false);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Master Sequencer Run
                            List<MacroProfile> currentQueue;
                            if (isRandomMode)
                            {
                                currentQueue = _sequencerQueue.OrderBy(x => _random.Next()).ToList();
                            }
                            else
                            {
                                currentQueue = _sequencerQueue.ToList();
                            }

                            for (int i = 0; i < currentQueue.Count; i++)
                            {
                                var profile = currentQueue[i];
                                token.ThrowIfCancellationRequested();

                                // 프로필 시작 전 윈도우 가상 키 버퍼를 완전히 리셋하여 Shift 등 물림 제거
                                ReleaseAllPossibleKeys();

                                // 실행 중인 순서 조합 리스트뷰 포커싱 및 스크롤 추적 (원본 큐 내의 인덱스를 찾아 하이라이트 매칭)
                                Dispatcher.Invoke(() =>
                                {
                                    int originalIdx = _sequencerQueue.IndexOf(profile);
                                    if (originalIdx >= 0)
                                    {
                                        SequencerQueueListView.SelectedIndex = originalIdx;
                                        SequencerQueueListView.ScrollIntoView(profile);
                                    }
                                });

                                if (profile.ProfileType == "수동 매크로")
                                {
                                    foreach (var action in profile.ManualActions.ToList())
                                    {
                                        token.ThrowIfCancellationRequested();

                                        for (int r = 0; r < action.RepeatCount; r++)
                                        {
                                            token.ThrowIfCancellationRequested();

                                            if (action.ActionType == "MouseClick" || action.ActionType == "MouseMove")
                                            {
                                                int targetX = action.MouseX;
                                                int targetY = action.MouseY;
                                                if (targetHwnd != IntPtr.Zero)
                                                {
                                                    Win32Api.GetWindowRect(targetHwnd, out var rect);
                                                    targetX += rect.Left;
                                                    targetY += rect.Top;
                                                }

                                                await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);

                                                if (action.ActionType == "MouseClick")
                                                {
                                                    await Task.Delay(20, token);
                                                    SendMouseClickEvent(Win32Api.MOUSEEVENTF_LEFTDOWN, targetX, targetY);
                                                    await Task.Delay(20, token);
                                                    SendMouseClickEvent(Win32Api.MOUSEEVENTF_LEFTUP, targetX, targetY);
                                                }
                                                else
                                                {
                                                    SetCursorPos(targetX, targetY);
                                                }

                                                await Task.Delay(GetRandomizedDelay(action.DelayAfter, applyRandom), token);
                                            }
                                            else // Keyboard Action
                                            {
                                                pressedKeys = action.VirtualKeys.ToList();
                                                await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);
                                                SendKeys(pressedKeys, true);

                                                await Task.Delay(GetRandomizedDelay(action.Duration, applyRandom), token);

                                                SendKeys(pressedKeys, false);
                                                pressedKeys = new List<ushort>();

                                                await Task.Delay(GetRandomizedDelay(action.DelayAfter, applyRandom), token);
                                            }
                                        }
                                    }
                                }
                                else // Recorded Profile Run
                                {
                                    foreach (var evt in profile.RecordedEvents.ToList())
                                    {
                                        token.ThrowIfCancellationRequested();

                                        if (evt.TimeOffsetSeconds > 0)
                                        {
                                            await Task.Delay((int)(evt.TimeOffsetSeconds * 1000), token);
                                        }

                                        if (evt.IsMouseEvent)
                                        {
                                            int targetX = evt.MouseX;
                                            int targetY = evt.MouseY;
                                            if (targetHwnd != IntPtr.Zero)
                                            {
                                                Win32Api.GetWindowRect(targetHwnd, out var rect);
                                                targetX += rect.Left;
                                                targetY += rect.Top;
                                            }

                                            await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);

                                            if (evt.MouseEventFlags == Win32Api.MOUSEEVENTF_WHEEL)
                                            {
                                                SendMouseWheelEvent(evt.MouseEventFlags, evt.MouseData, targetX, targetY);
                                            }
                                            else if (evt.MouseEventFlags == Win32Api.MOUSEEVENTF_MOVE)
                                            {
                                                SetCursorPos(targetX, targetY);
                                            }
                                            else if (evt.MouseEventFlags != 0)
                                            {
                                                SendMouseClickEvent(evt.MouseEventFlags, targetX, targetY);
                                            }
                                        }
                                        else // Keyboard
                                        {
                                            if (evt.IsKeyDown)
                                            {
                                                if (!pressedKeys.Contains(evt.VirtualKey))
                                                {
                                                    pressedKeys.Add(evt.VirtualKey);
                                                }
                                                await EnsureTargetWindowFocused(targetHwnd, pressedKeys, token);
                                                SendKey(evt.VirtualKey, true);
                                            }
                                            else
                                            {
                                                pressedKeys.Remove(evt.VirtualKey);
                                                SendKey(evt.VirtualKey, false);
                                            }
                                        }
                                    }
                                }
                                
                                // 프로필 전환 간 0.3초 쿨다운 지연을 주어 윈도우 OS 한영 입력기 안착 보장
                                await Task.Delay(300, token);
                            }
                        }

                    } while (isLoop && !token.IsCancellationRequested);

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "매크로 실행 완료";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                        ResetMacroControlState();
                    });
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "매크로 실행이 강제 중지되었습니다.";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        ReleaseKeys(pressedKeys);
                        ResetMacroControlState();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"매크로 실행 중 오류가 발생했습니다: {ex.Message}", "매크로 에러", MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusText.Text = "오류로 인한 매크로 중단";
                        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        ReleaseKeys(pressedKeys);
                        ResetMacroControlState();
                    });
                }
            }, token);
        }

        private void StopMacro()
        {
            if (!_isMacroRunning) return;
            _macroCts?.Cancel();
        }

        private void ResetMacroControlState()
        {
            _isMacroRunning = false;
            _isMacroPaused = false;

            // 매크로 정지/종료 시 혹시 눌려 있을지 모르는 모든 가상 키 강제 릴리즈
            ReleaseAllPossibleKeys();

            _macroRuntimeTimer?.Stop();
            _macroRuntimeStopwatch?.Stop();
            if (_macroRuntimeStopwatch != null)
            {
                MacroTimerText.Text = $"실행 시간: {_macroRuntimeStopwatch.Elapsed.TotalSeconds:F1}초 (종료)";
            }

            StartMacroButton.IsEnabled = true;
            PauseMacroButton.IsEnabled = false;
            PauseMacroButton.Content = "일시정지 (F7)";
        }

        private void StartMacroButton_Click(object sender, RoutedEventArgs e)
        {
            StartMacro();
        }

        private void StopMacroButton_Click(object sender, RoutedEventArgs e)
        {
            StopMacro();
        }

        private static void SendMouseClickEvent(uint flags, int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            int absoluteX = (int)Math.Round((screenX * 65536.0) / screenWidth);
            int absoluteY = (int)Math.Round((screenY * 65536.0) / screenHeight);

            Win32Api.INPUT[] inputs = new Win32Api.INPUT[1];
            inputs[0].type = Win32Api.INPUT_MOUSE;
            inputs[0].U.mi.dx = absoluteX;
            inputs[0].U.mi.dy = absoluteY;
            inputs[0].U.mi.dwFlags = flags | Win32Api.MOUSEEVENTF_ABSOLUTE;
            Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.INPUT)));
        }

        private static void SendMouseWheelEvent(uint flags, uint mouseData, int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            int absoluteX = (int)Math.Round((screenX * 65536.0) / screenWidth);
            int absoluteY = (int)Math.Round((screenY * 65536.0) / screenHeight);

            Win32Api.INPUT[] inputs = new Win32Api.INPUT[1];
            inputs[0].type = Win32Api.INPUT_MOUSE;
            inputs[0].U.mi.dx = absoluteX;
            inputs[0].U.mi.dy = absoluteY;
            inputs[0].U.mi.dwFlags = flags | Win32Api.MOUSEEVENTF_ABSOLUTE;
            inputs[0].U.mi.mouseData = mouseData;
            Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.INPUT)));
        }

        // ------------------ SendInput Helper Methods ------------------
        private static void SendKey(ushort vk, bool isKeyDown)
        {
            Win32Api.INPUT[] inputs = new Win32Api.INPUT[1];
            inputs[0].type = Win32Api.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = vk;
            inputs[0].U.ki.wScan = (ushort)Win32Api.MapVirtualKey(vk, 0);
            inputs[0].U.ki.dwFlags = (isKeyDown ? 0 : Win32Api.KEYEVENTF_KEYUP);

            if (IsExtendedKey(vk))
            {
                inputs[0].U.ki.dwFlags |= Win32Api.KEYEVENTF_EXTENDEDKEY;
            }

            Win32Api.SendInput(1, inputs, Marshal.SizeOf(typeof(Win32Api.INPUT)));
        }

        private static void SendKeys(List<ushort> vks, bool isKeyDown)
        {
            if (vks == null || vks.Count == 0) return;

            Win32Api.INPUT[] inputs = new Win32Api.INPUT[vks.Count];
            for (int i = 0; i < vks.Count; i++)
            {
                inputs[i].type = Win32Api.INPUT_KEYBOARD;
                inputs[i].U.ki.wVk = vks[i];
                inputs[i].U.ki.wScan = (ushort)Win32Api.MapVirtualKey(vks[i], 0);
                inputs[i].U.ki.dwFlags = (isKeyDown ? 0 : Win32Api.KEYEVENTF_KEYUP);
                if (IsExtendedKey(vks[i]))
                {
                    inputs[i].U.ki.dwFlags |= Win32Api.KEYEVENTF_EXTENDEDKEY;
                }
            }

            Win32Api.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Win32Api.INPUT)));
        }

        private static void ReleaseKeys(List<ushort> vks)
        {
            if (vks == null || vks.Count == 0) return;
            SendKeys(vks, false);
        }

        private static void ReleaseAllPossibleKeys()
        {
            List<ushort> keysToRelease = new List<ushort> { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, Win
            for (ushort vk = 8; vk <= 190; vk++)
            {
                // 토글 및 시스템 오작동 방지를 위해 특정 기능키 릴리즈 제외:
                // - 한영키(0x15), 한자키(0x19), CapsLock(0x14)
                // - Apps(메뉴/컨텍스트)키(0x5D), F10(0x79) - 단독/조합 시 우클릭 컨텍스트 메뉴 유입 차단
                // - F12(0x7B) - 개발자 도구 팝업 방지
                if (vk == 0x15 || vk == 0x19 || vk == 0x14 || vk == 0x5D || vk == 0x79 || vk == 0x7B) continue;

                keysToRelease.Add(vk);
            }
            SendKeys(keysToRelease, false);
        }

        private static bool IsExtendedKey(ushort vk)
        {
            return (vk >= 33 && vk <= 46) || (vk >= 91 && vk <= 93);
        }

        private async Task EnsureTargetWindowFocused(IntPtr targetHwnd, List<ushort> pressedKeys, CancellationToken token)
        {
            // 수동 일시정지 혹은 포커스 상실 상태이면 루프 진입
            if (_isMacroPaused || (targetHwnd != IntPtr.Zero && Win32Api.GetForegroundWindow() != targetHwnd))
            {
                // 포커스/동작 중지되는 순간 키 꼬임 예방을 위해 모든 눌림 키 강제 해제
                if (pressedKeys.Count > 0)
                {
                    ReleaseKeys(pressedKeys);
                }

                while (_isMacroPaused || (targetHwnd != IntPtr.Zero && Win32Api.GetForegroundWindow() != targetHwnd))
                {
                    token.ThrowIfCancellationRequested();

                    Dispatcher.Invoke(() =>
                    {
                        if (_isMacroPaused)
                        {
                            StatusText.Text = "수동 일시정지 중 (F7로 이어하기)";
                            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // 주황색
                        }
                        else
                        {
                            StatusText.Text = "대상 창 포커스 상실 - 일시정지 중...";
                            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)); // 빨강
                        }
                    });

                    await Task.Delay(150, token);
                }

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "매크로 동작 실행 중...";
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                });

                // 복귀 지연 안착
                await Task.Delay(200, token);
            }
        }

        private static int GetRandomizedDelay(double seconds, bool applyRandom)
        {
            int baseMs = (int)(seconds * 1000);
            if (baseMs <= 0) return 0;
            if (!applyRandom) return baseMs;

            // ±12%의 미세 오차 난수 가산 (88% ~ 112% 범위)
            double factor = 0.88 + (_random.NextDouble() * 0.24);
            return (int)(baseMs * factor);
        }

        private void SequencerAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (SequencerAvailableProfilesListView.SelectedItem is MacroProfile profile)
            {
                _sequencerQueue.Add(profile);
                StatusText.Text = $"시퀀스 목록에 '{profile.Name}' 추가 완료";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                MessageBox.Show("추가할 프로필을 선택해 주세요.", "선택 누락", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SequencerRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SequencerQueueListView.SelectedItem is MacroProfile profile)
            {
                _sequencerQueue.Remove(profile);
                StatusText.Text = $"시퀀스 목록에서 '{profile.Name}' 제거 완료";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
            else
            {
                MessageBox.Show("삭제할 시퀀스 항목을 선택해 주세요.", "선택 누락", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SequencerMoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            int index = SequencerQueueListView.SelectedIndex;
            if (index > 0)
            {
                var selected = _sequencerQueue[index];
                _sequencerQueue.RemoveAt(index);
                _sequencerQueue.Insert(index - 1, selected);
                SequencerQueueListView.SelectedIndex = index - 1;
            }
        }

        private void SequencerMoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            int index = SequencerQueueListView.SelectedIndex;
            if (index >= 0 && index < _sequencerQueue.Count - 1)
            {
                var selected = _sequencerQueue[index];
                _sequencerQueue.RemoveAt(index);
                _sequencerQueue.Insert(index + 1, selected);
                SequencerQueueListView.SelectedIndex = index + 1;
            }
        }

        private void TogglePauseMacro()
        {
            if (!_isMacroRunning) return;

            _isMacroPaused = !_isMacroPaused;

            if (_isMacroPaused)
            {
                PauseMacroButton.Content = "이어하기 (F7)";
                StatusText.Text = "수동 일시정지 중 (F7로 이어하기)";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // 주황색
            }
            else
            {
                PauseMacroButton.Content = "일시정지 (F7)";
                StatusText.Text = "매크로 동작 실행 중...";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // 주황색
            }
        }

        private void PauseMacroButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePauseMacro();
        }
    }
}