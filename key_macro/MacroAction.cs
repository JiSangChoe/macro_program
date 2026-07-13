using System.Collections.Generic;
using System.Linq;

namespace KeyMacro
{
    public class MacroAction
    {
        // 입력할 키들의 가상 키 코드 목록 (동시 입력 지원)
        public List<ushort> VirtualKeys { get; set; } = new List<ushort>();

        // 키 목록의 텍스트 표현 (예: "Space + Right")
        public string KeysText { get; set; } = string.Empty;

        // 키 누르고 있을 지속 시간 (초 단위, 기본 0.1초)
        public double Duration { get; set; } = 0.1;

        // 반복 횟수 (기본 1회)
        public int RepeatCount { get; set; } = 1;

        // 작업 완료 후 대기 시간 (초 단위, 기본 0.1초)
        public double DelayAfter { get; set; } = 0.1;

        // UI에 표시될 요약 텍스트
        public string DisplayText
        {
            get
            {
                string repeatStr = RepeatCount > 1 ? $" (x{RepeatCount})" : "";
                return $"[키: {KeysText}] -> 누름: {Duration:F1}초{repeatStr} | 대기: {DelayAfter:F1}초";
            }
        }
    }
}
