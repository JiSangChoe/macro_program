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

namespace KeyMacro
{
    public partial class MainWindow : Window
    {
        // P/Invoke for Hotkeys
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID_START = 9000;
        private const int HOTKEY_ID_STOP = 9001;

        // Observable Collections for Data Binding
        private readonly ObservableCollection<MacroAction> _macroActions = new ObservableCollection<MacroAction>();
        private readonly ObservableCollection<RecordedKeyEvent> _recordedEvents = new ObservableCollection<RecordedKeyEvent>();
        private readonly ObservableCollection<Win32Api.WindowInfo> _windows = new ObservableCollection<Win32Api.WindowInfo>();
        private readonly ObservableCollection<MacroProfile> _profiles = new ObservableCollection<MacroProfile>();

        private readonly MacroRecorder _recorder = new MacroRecorder();
        private readonly HashSet<ushort> _currentCustomKeys = new HashSet<ushort>();

        private CancellationTokenSource? _macroCts;
        private bool _isMacroRunning = false;
        private MacroProfile? _activeProfile = null;

        public MainWindow()
        {
            InitializeComponent();

            // Bind Lists
            MacroActionListView.ItemsSource = _macroActions;
            WindowComboBox.ItemsSource = _windows;
            ProfileComboBox.ItemsSource = _profiles;

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

            // Register Hotkeys: F5 (0x74) for Start, F6 (0x75) for Stop
            RegisterHotKey(handle, HOTKEY_ID_START, 0, 0x74);
            RegisterHotKey(handle, HOTKEY_ID_STOP, 0, 0x75);

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
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID_START);
            UnregisterHotKey(handle, HOTKEY_ID_STOP);
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

        private void AddActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCustomKeys.Count == 0)
            {
                MessageBox.Show("동작할 키를 하나 이상 지정해주세요.", "키 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(DurationTextBox.Text, out double duration) || duration <= 0)
            {
                MessageBox.Show("누르고 있을 시간은 0보다 큰 숫자여야 합니다 (예: 0.1).", "시간 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
                VirtualKeys = _currentCustomKeys.ToList(),
                KeysText = KeyInputTextBox.Text,
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

            if (_currentCustomKeys.Count == 0)
            {
                MessageBox.Show("동작할 키를 하나 이상 지정해주세요.", "키 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(DurationTextBox.Text, out double duration) || duration <= 0)
            {
                MessageBox.Show("누르고 있을 시간은 0보다 큰 숫자여야 합니다 (예: 0.1).", "시간 입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
                _currentCustomKeys.Clear();
                foreach (var vk in action.VirtualKeys)
                {
                    _currentCustomKeys.Add(vk);
                }
                UpdateKeyInputTextBoxText();
                DurationTextBox.Text = action.Duration.ToString("F1");
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

            _recorder.Start();
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
            RecordTimerText.Text = "남은 녹화 시간: 05:00";

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
            StatusText.Text = "매크로 동작 실행 중...";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            StartMacroButton.IsEnabled = false;

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

            Task.Run(async () =>
            {
                List<ushort> pressedKeys = new List<ushort>();

                try
                {
                    do
                    {
                        if (targetHwnd != IntPtr.Zero)
                        {
                            Win32Api.ShowWindow(targetHwnd, Win32Api.SW_RESTORE);
                            Win32Api.SetForegroundWindow(targetHwnd);
                            await Task.Delay(300, token);
                        }

                        if (selectedTab == 0)
                        {
                            // Manual Macro Run
                            foreach (var action in _macroActions)
                            {
                                token.ThrowIfCancellationRequested();

                                for (int r = 0; r < action.RepeatCount; r++)
                                {
                                    token.ThrowIfCancellationRequested();

                                    pressedKeys = action.VirtualKeys;
                                    SendKeys(pressedKeys, true);

                                    await Task.Delay((int)(action.Duration * 1000), token);

                                    SendKeys(pressedKeys, false);
                                    pressedKeys = new List<ushort>();

                                    await Task.Delay((int)(action.DelayAfter * 1000), token);
                                }
                            }
                        }
                        else
                        {
                            // Recorded Macro Run
                            foreach (var evt in _recordedEvents)
                            {
                                token.ThrowIfCancellationRequested();

                                if (evt.TimeOffsetSeconds > 0)
                                {
                                    await Task.Delay((int)(evt.TimeOffsetSeconds * 1000), token);
                                }

                                if (evt.IsKeyDown)
                                {
                                    if (!pressedKeys.Contains(evt.VirtualKey))
                                    {
                                        pressedKeys.Add(evt.VirtualKey);
                                    }
                                    SendKey(evt.VirtualKey, true);
                                }
                                else
                                {
                                    pressedKeys.Remove(evt.VirtualKey);
                                    SendKey(evt.VirtualKey, false);
                                }
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
            StartMacroButton.IsEnabled = true;
        }

        private void StartMacroButton_Click(object sender, RoutedEventArgs e)
        {
            StartMacro();
        }

        private void StopMacroButton_Click(object sender, RoutedEventArgs e)
        {
            StopMacro();
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

        private static bool IsExtendedKey(ushort vk)
        {
            return (vk >= 33 && vk <= 46) || (vk >= 91 && vk <= 93);
        }
    }
}