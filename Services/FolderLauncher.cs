using System.Diagnostics;
using System.IO;

namespace FocusClip.Services;

/// <summary>
/// 폴더를 지정한 파일 관리자(예: Q-Dir)로 연다. fmPath가 비었거나 없으면 기본 탐색기로 폴백.
/// 인자 템플릿(fmArgs)의 %path%가 폴더 경로로 치환된다 — 관리자별 스위치(탭/단일 인스턴스 등)를 사용자가 조정 가능.
/// (단, Q-Dir의 '기존 인스턴스 새 탭'은 Q-Dir 자체 설정이라 인자만으로는 강제되지 않을 수 있다.)
/// </summary>
public static class FolderLauncher
{
    public static void OpenFolder(string folder, string? fmPath, string? fmArgs)
    {
        try
        {
            ProcessStartInfo psi;
            if (!string.IsNullOrWhiteSpace(fmPath) && File.Exists(fmPath))
            {
                string tmpl = string.IsNullOrWhiteSpace(fmArgs) ? "\"%path%\"" : fmArgs!;
                psi = new ProcessStartInfo(fmPath, tmpl.Replace("%path%", folder));
            }
            else
            {
                psi = new ProcessStartInfo(folder); // 기본 탐색기
            }
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch { }
    }
}
