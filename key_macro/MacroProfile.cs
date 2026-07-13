using System.Collections.Generic;

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
    }
}
