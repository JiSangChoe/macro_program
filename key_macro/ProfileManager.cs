using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KeyMacro
{
    public static class ProfileManager
    {
        private static readonly string FolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");
        private static readonly string FilePath = Path.Combine(FolderPath, "profiles.json");

        // 로컬 파일에서 모든 매크로 프로필 리스트를 읽어옵니다.
        public static List<MacroProfile> LoadProfiles()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                if (!File.Exists(FilePath))
                {
                    return new List<MacroProfile>();
                }

                string json = File.ReadAllText(FilePath);
                var profiles = JsonSerializer.Deserialize<List<MacroProfile>>(json);
                return profiles ?? new List<MacroProfile>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProfileManager] 로드 중 오류 발생: {ex.Message}");
                return new List<MacroProfile>();
            }
        }

        // 로컬 파일에 매크로 프로필 리스트를 JSON 형태로 영구 저장합니다.
        public static void SaveProfiles(List<MacroProfile> profiles)
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) // 한글 깨짐 방지용 인코더 설정
                };
                string json = JsonSerializer.Serialize(profiles, options);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProfileManager] 저장 중 오류 발생: {ex.Message}");
            }
        }
    }
}
