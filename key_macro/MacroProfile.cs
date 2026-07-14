using System.Collections.Generic;
using System.Linq;

namespace KeyMacro
{
    public class MacroProfile
    {
        public string Name { get; set; } = string.Empty;
        
        // 프로필 종류 구분: "수동 매크로" 또는 "녹화 매크로"
        public string ProfileType { get; set; } = "수동 매크로";

        // 수동 매크로용 액션 리스트
        public List<MacroAction> ManualActions { get; set; } = new List<MacroAction>();

        // 녹화 매크로용 키 이벤트 리스트
        public List<RecordedKeyEvent> RecordedEvents { get; set; } = new List<RecordedKeyEvent>();

        // UI에 선택 항목으로 표시될 텍스트
        public string DisplayText => $"[{ProfileType}] {Name}";

        // 프로필 상세 스펙 요약 정보 (UI 표시용)
        public string Description
        {
            get
            {
                if (ProfileType == "수동 매크로")
                {
                    int keyCount = ManualActions?.Sum(a => (a.VirtualKeys?.Count ?? 0) * a.RepeatCount) ?? 0;
                    double totalSec = ManualActions?.Sum(a => (a.Duration + a.DelayAfter) * a.RepeatCount) ?? 0;
                    return $"입력 {keyCount}회 | 시간: {totalSec:F1}초";
                }
                else
                {
                    int keyCount = RecordedEvents?.Count ?? 0;
                    double totalSec = 0;
                    if (RecordedEvents != null)
                    {
                        foreach (var e in RecordedEvents)
                        {
                            totalSec += e.TimeOffsetSeconds;
                        }
                    }
                    return $"입력 {keyCount}회 | 시간: {totalSec:F1}초";
                }
            }
        }
    }
}
