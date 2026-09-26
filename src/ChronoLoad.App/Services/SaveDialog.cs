using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChronoLoad.App.Services;

/// <summary>
/// 체크 상자를 하나 단 「다른 이름으로 저장」 대화상자.
/// </summary>
/// <remarks>
/// <para>
/// WPF 의 <c>Microsoft.Win32.SaveFileDialog</c> 는 속을 셸의 <c>IFileSaveDialog</c> 로 만들면서도
/// <c>IFileDialogCustomize</c> 를 밖으로 내주지 않는다. 옵션 하나를 달려면 셸 대화상자를 직접
/// 여는 수밖에 없다.
/// </para>
/// <para>
/// 셸은 추가 컨트롤을 <b>버튼 옆 아래 띠</b>에 오른쪽으로 붙여 놓는다. 자리는 고를 수 없고,
/// 폭도 좁아 긴 이름표는 두 줄로 접히다 결국 잘린다(실측). 시각 그룹의 이름표만큼 왼쪽으로
/// 밀리므로 <b>이름표는 그룹에, 상자 글씨는 짧게</b> 둔다.
/// </para>
/// <para>
/// 인터페이스는 <b>쓰는 자리까지만</b> 선언한다. COM 은 메서드 <b>순서</b>로 vtable 을 잡으므로
/// 앞자리는 이름과 개수만 맞으면 되고 뒷자리는 없어도 된다. 대신 <b>중간을 하나라도 빠뜨리면
/// 엉뚱한 함수가 불린다</b> — 순서를 건드릴 일이 있으면 MSDN 의 선언 순서를 그대로 따른다.
/// </para>
/// </remarks>
internal static class SaveDialog
{
    private static readonly Guid FileSaveDialogClass = new("c0b4e2f3-ba21-4773-8dba-335ec946eb8b");

    /// <summary>컨트롤 id. 상자가 하나뿐이라 값 자체에는 의미가 없다.</summary>
    private const int CheckId = 1;
    private const int GroupId = 2;

    private const uint OverwritePrompt = 0x2;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint NoReadOnlyReturn = 0x8000;

    private const uint SigdnFileSysPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <param name="check">
    /// 체크 상자를 달지 않으려면 null. <c>Group</c> 은 상자 왼쪽에 붙는 이름표다 —
    /// 셸은 추가 컨트롤을 <b>버튼 쪽에 붙여</b> 놓으므로, 이름표가 없으면 오른쪽 구석에 몰린다.
    /// </param>
    /// <returns>취소하면 null. 아니면 고른 경로와 체크 상자의 상태.</returns>
    public static (string Path, bool Checked)? Show(
        Window owner, string fileName, string filterLabel, string extension,
        (string Group, string Label, bool Default)? check)
    {
        var dialog = (IFileDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(FileSaveDialogClass)!)!;

        try
        {
            dialog.SetOptions(OverwritePrompt | ForceFileSystem | PathMustExist | NoReadOnlyReturn);
            dialog.SetFileTypes(1, [new FilterSpec { Name = filterLabel, Spec = "*" + extension }]);
            dialog.SetDefaultExtension(extension.TrimStart('.'));
            dialog.SetFileName(fileName);

            if (check is { } option)
            {
                var custom = (IFileDialogCustomize)dialog;
                custom.StartVisualGroup(GroupId, option.Group);
                custom.AddCheckButton(CheckId, option.Label, option.Default);
                custom.EndVisualGroup();
            }

            int hr = dialog.Show(new WindowInteropHelper(owner).Handle);
            if (hr == Cancelled) return null;
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var item);
            item.GetDisplayName(SigdnFileSysPath, out string path);

            bool state = check?.Default ?? false;
            if (check is not null)
                ((IFileDialogCustomize)dialog).GetCheckButtonState(CheckId, out state);

            return (path, state);
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        public string Name;
        public string Spec;
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig] int Show(nint parent);

        // IFileDialog. 쓰지 않는 자리는 이름만 두고 인자를 적지 않는다 — 부르지 않으므로
        // 서명은 상관없지만 자리는 비울 수 없다.
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(uint options);
        void GetOptions();
        void SetDefaultFolder();
        void SetFolder();
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
        void AddPlace();
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
    }

    [ComImport, Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown();
        void AddMenu();
        void AddPushButton();
        void AddComboBox();
        void AddRadioButtonList();
        void AddCheckButton(int id, [MarshalAs(UnmanagedType.LPWStr)] string label,
                            [MarshalAs(UnmanagedType.Bool)] bool state);
        void AddEditBox();
        void AddSeparator();
        void AddText();
        void SetControlLabel();
        void GetControlState();
        void SetControlState();
        void GetEditBoxText();
        void SetEditBoxText();
        void GetCheckButtonState(int id, [MarshalAs(UnmanagedType.Bool)] out bool state);
        void SetCheckButtonState();
        void AddControlItem();
        void RemoveControlItem();
        void RemoveAllControlItems();
        void GetControlItemState();
        void SetControlItemState();
        void GetSelectedControlItem();
        void SetSelectedControlItem();
        void StartVisualGroup(int id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void EndVisualGroup();
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
